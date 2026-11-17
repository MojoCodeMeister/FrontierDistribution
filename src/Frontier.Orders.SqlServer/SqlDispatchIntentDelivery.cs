using System.Data;
using Microsoft.Data.SqlClient;

namespace Frontier.Orders.SqlServer;

/// <summary>ERP-owned durable dispatch work item. No Route4Me API communication occurs here.</summary>
public sealed record DispatchClaim(string DispatchRequestId, string DepartureId, string SalesOrderId,
    string WarehouseId, string PackingId, Guid LeaseToken, int AttemptCount);

public sealed class SqlDispatchIntentDelivery
{
    private readonly string _connectionString;
    public SqlDispatchIntentDelivery(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>Atomically claims the oldest eligible Pending intent across the ERP queue.</summary>
    public DispatchClaim? ClaimNext(TimeSpan leaseDuration) => Claim(leaseDuration, null);

    /// <summary>Claims only the requested ERP dispatch intent, using the same lease and retry rules.</summary>
    public DispatchClaim? ClaimRequest(string requestId, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (requestId.Length > 64) throw new ArgumentOutOfRangeException(nameof(requestId));
        return Claim(leaseDuration, requestId);
    }

    private DispatchClaim? Claim(TimeSpan leaseDuration, string? requestedId)
    {
        if (leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        string? requestId = null, departureId = null;
        Guid token = Guid.Empty;
        int attempts = 0;
        using (var claim = new SqlCommand("""
            DECLARE @now datetime2(3) = SYSUTCDATETIME();
            ;WITH next_item AS (
                SELECT TOP (1) * FROM erp.DispatchIntent WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE (@request IS NULL OR DispatchRequestId=@request) AND Status='Pending' AND NextAttemptUtc <= @now
                    AND (LeaseUntilUtc IS NULL OR LeaseUntilUtc <= @now)
                ORDER BY CreatedUtc, DispatchRequestId
            )
            UPDATE next_item
                SET LeaseToken=NEWID(), LeaseUntilUtc=DATEADD(second,@leaseSeconds,@now),
                    LastAttemptUtc=@now, AttemptCount=AttemptCount+1
            OUTPUT inserted.DispatchRequestId, inserted.DepartureId, inserted.LeaseToken, inserted.AttemptCount;
            """, connection, tx))
        {
            claim.Parameters.Add("@request", SqlDbType.VarChar, 64).Value = (object?)requestedId ?? DBNull.Value;
            claim.Parameters.Add("@leaseSeconds", SqlDbType.Int).Value = (int)Math.Ceiling(leaseDuration.TotalSeconds);
            using var reader = claim.ExecuteReader();
            if (reader.Read())
            {
                requestId = reader.GetString(0);
                departureId = reader.GetString(1);
                token = reader.GetGuid(2);
                attempts = reader.GetInt32(3);
            }
        }
        if (requestId is null)
        {
            tx.Commit();
            return null;
        }
        using var detail = new SqlCommand("""
            SELECT SalesOrderId, WarehouseId, PackingId FROM erp.WarehouseDeparture
            WHERE DepartureId=@departure;
            """, connection, tx);
        detail.Parameters.Add("@departure", SqlDbType.VarChar, 64).Value = departureId!;
        using var details = detail.ExecuteReader();
        if (!details.Read()) throw new InvalidDataException("Departure missing for claimed dispatch intent");
        var item = new DispatchClaim(requestId, departureId!, details.GetString(0), details.GetString(1), details.GetString(2), token, attempts);
        details.Close();
        tx.Commit();
        return item;
    }

    /// <summary>Durably records that a remote write may occur before transport starts.</summary>
    public bool MarkRemoteSubmissionAttempted(string requestId, Guid leaseToken)
        => Transition(requestId, leaseToken, """
            UPDATE erp.DispatchIntent
               SET RemoteSubmissionAttemptedUtc=SYSUTCDATETIME()
            WHERE DispatchRequestId=@request AND LeaseToken=@token AND Status='Pending'
                AND LeaseUntilUtc > SYSUTCDATETIME() AND RemoteSubmissionAttemptedUtc IS NULL;
            """);

    public bool WasRemoteSubmissionAttempted(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand("""
            SELECT RemoteSubmissionAttemptedUtc FROM erp.DispatchIntent
            WHERE DispatchRequestId=@request;
            """, conn);
        cmd.Parameters.Add("@request", SqlDbType.VarChar, 64).Value = requestId;
        return cmd.ExecuteScalar() is DateTime;
    }

    /// <summary>Only the current, unexpired lease holder may acknowledge acceptance.</summary>
    public bool MarkAccepted(string requestId, Guid leaseToken, string remoteShipmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteShipmentId);
        if (remoteShipmentId.Length > 200) throw new ArgumentOutOfRangeException(nameof(remoteShipmentId));
        return Transition(requestId, leaseToken, """
            UPDATE erp.DispatchIntent SET Status='Accepted', AcceptedUtc=SYSUTCDATETIME(),
                LeaseToken=NULL, LeaseUntilUtc=NULL, LastError=NULL, RemoteShipmentId=@remoteId
            WHERE DispatchRequestId=@request AND LeaseToken=@token AND Status='Pending'
                AND LeaseUntilUtc > SYSUTCDATETIME();
            """, remoteShipmentId: remoteShipmentId);
    }

    /// <summary>Release the lease and schedule a retry; a new claim is required for another attempt.</summary>
    public bool MarkRetryableFailure(string requestId, Guid leaseToken, string error, TimeSpan retryDelay)
    {
        if (retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        ValidateError(error);
        return Transition(requestId, leaseToken, """
            UPDATE erp.DispatchIntent SET Status='Pending', LeaseToken=NULL, LeaseUntilUtc=NULL,
                NextAttemptUtc=DATEADD(second,@retrySeconds,SYSUTCDATETIME()), LastError=@error
            WHERE DispatchRequestId=@request AND LeaseToken=@token AND Status='Pending'
                AND LeaseUntilUtc > SYSUTCDATETIME();
            """, error, (int)Math.Ceiling(retryDelay.TotalSeconds));
    }

    /// <summary>Record a terminal failure, preventing automatic reclaim until separately reviewed.</summary>
    public bool MarkTerminalFailure(string requestId, Guid leaseToken, string error)
    {
        ValidateError(error);
        return Transition(requestId, leaseToken, """
            UPDATE erp.DispatchIntent SET Status='Failed', LeaseToken=NULL, LeaseUntilUtc=NULL,
                LastError=@error
            WHERE DispatchRequestId=@request AND LeaseToken=@token AND Status='Pending'
                AND LeaseUntilUtc > SYSUTCDATETIME();
            """, error);
    }

    private bool Transition(string requestId, Guid token, string sql, string? error = null, int retrySeconds = 0, string? remoteShipmentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (requestId.Length > 64) throw new ArgumentOutOfRangeException(nameof(requestId));
        if (token == Guid.Empty) throw new ArgumentException("A nonempty claim token is required", nameof(token));
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@request", SqlDbType.VarChar, 64).Value = requestId;
        cmd.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = token;
        cmd.Parameters.Add("@error", SqlDbType.NVarChar, 512).Value = (object?)error ?? DBNull.Value;
        cmd.Parameters.Add("@retrySeconds", SqlDbType.Int).Value = retrySeconds;
        cmd.Parameters.Add("@remoteId", SqlDbType.NVarChar, 200).Value = (object?)remoteShipmentId ?? DBNull.Value;
        return cmd.ExecuteNonQuery() == 1;
    }

    private static void ValidateError(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (error.Length > 512) throw new ArgumentOutOfRangeException(nameof(error));
    }
}
