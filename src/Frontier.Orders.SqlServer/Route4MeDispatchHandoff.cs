using System.Data;
using Microsoft.Data.SqlClient;

namespace Frontier.Orders.SqlServer;

// Q25.5d1: application boundary only. The real Route4Me transport is deliberately not implemented.
public sealed record Route4MeDispatchLine(string ProductId, int Quantity);
public sealed record Route4MeDeliveryDestination(string LocationId, string Address1,
    string? Address2, string City, string Region, string PostalCode, string CountryCode,
    string? ContactName, string? ContactPhone);
public sealed record Route4MeDispatchRequest(string DispatchRequestId, string DepartureId,
    string SalesOrderId, string WarehouseId, string PackingId, IReadOnlyList<Route4MeDispatchLine> Lines,
    Route4MeDeliveryDestination Destination);
public enum RemoteDispatchState { NotFound, Accepted, Uncertain, Rejected }
public sealed record RemoteDispatchResult(RemoteDispatchState State, string? DispatchRequestId = null,
    string? ExternalShipmentId = null, string? Error = null);

/// <summary>
/// Implementations MUST reconcile by the stable DispatchRequestId and MUST ensure repeated
/// SubmitAsync calls cannot create two remote shipments. An unknown outcome must be Uncertain.
/// Do not enable a live implementation until these capabilities have been verified with Route4Me.
/// </summary>
public interface IRoute4MeDispatchGateway
{
    Task<RemoteDispatchResult> ReconcileAsync(string dispatchRequestId, CancellationToken cancellationToken);
    Task<RemoteDispatchResult> SubmitAsync(Route4MeDispatchRequest request, CancellationToken cancellationToken);
}

public enum HandoffState { NoClaim, Accepted, RetryScheduled, Failed, LeaseLost }
public sealed record HandoffResult(HandoffState State, string DispatchRequestId, string Message);

/// <summary>ERP queue consumer. No writes to the Route4Me schema or inventory tables.</summary>
public sealed class Route4MeDispatchHandoff
{
    private readonly SqlDispatchIntentDelivery _delivery;
    private readonly string _connectionString;
    private readonly IRoute4MeDispatchGateway _gateway;

    public Route4MeDispatchHandoff(string connectionString, IRoute4MeDispatchGateway gateway)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _connectionString = connectionString;
        _delivery = new SqlDispatchIntentDelivery(connectionString);
    }

    public async Task<HandoffResult> ProcessRequestAsync(string dispatchRequestId, TimeSpan lease,
        TimeSpan retryDelay, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchRequestId);
        if (retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        cancellationToken.ThrowIfCancellationRequested();
        var claim = _delivery.ClaimRequest(dispatchRequestId, lease);
        if (claim is null) return new(HandoffState.NoClaim, dispatchRequestId, "Request is not claimable");
        HandoffResult Retry(string reason)
        {
            var stored = _delivery.MarkRetryableFailure(dispatchRequestId, claim.LeaseToken,
                Truncate(reason), retryDelay);
            return new(stored ? HandoffState.RetryScheduled : HandoffState.LeaseLost, dispatchRequestId, reason);
        }
        HandoffResult Fail(string reason)
        {
            var stored = _delivery.MarkTerminalFailure(dispatchRequestId, claim.LeaseToken, Truncate(reason));
            return new(stored ? HandoffState.Failed : HandoffState.LeaseLost, dispatchRequestId, reason);
        }
        HandoffResult Accept(string reason, string remoteShipmentId)
        {
            var stored = _delivery.MarkAccepted(dispatchRequestId, claim.LeaseToken, remoteShipmentId);
            return new(stored ? HandoffState.Accepted : HandoffState.LeaseLost, dispatchRequestId, reason);
        }
        try
        {
            var request = ReadManifest(claim);
            if (request.Lines.Count == 0) return Fail("ERP consumption contains no shipment lines");
            var existing = await _gateway.ReconcileAsync(request.DispatchRequestId, cancellationToken);
            var previous = Evaluate(existing, request, "reconciliation");
            if (previous is not null) return previous;
            // A lookup miss does not prove non-creation if a remote write was attempted.
            if (_delivery.WasRemoteSubmissionAttempted(request.DispatchRequestId))
                return Retry("Remote lookup NotFound after prior submission attempt; duplicate submission blocked");
            // Atomically mark the possible external write before initiating network I/O.
            if (!_delivery.MarkRemoteSubmissionAttempted(dispatchRequestId, claim.LeaseToken))
                return new(HandoffState.LeaseLost, dispatchRequestId, "Submission claim no longer held");
            var submitted = await _gateway.SubmitAsync(request, cancellationToken);
            return Evaluate(submitted, request, "submission") ?? Retry("Submission unexpectedly reported not found");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Lease expires and can be reclaimed. Caller did not attest remote outcome.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Retry("Uncertain transport/manifest outcome: " + ex.GetType().Name + ": " + ex.Message);
        }

        HandoffResult? Evaluate(RemoteDispatchResult response, Route4MeDispatchRequest request, string phase)
        {
            if (response is null) return Retry(phase + " returned no response");
            switch (response.State)
            {
                case RemoteDispatchState.NotFound: return null;
                case RemoteDispatchState.Uncertain: return Retry(phase + " uncertain: " + response.Error);
                case RemoteDispatchState.Rejected: return Fail(phase + " rejected: " + response.Error);
                case RemoteDispatchState.Accepted:
                    if (!string.Equals(response.DispatchRequestId, request.DispatchRequestId, StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(response.ExternalShipmentId))
                        return Retry("Remote acceptance lacks matching request identity and shipment reference; manual reconciliation required");
                    return Accept(phase + " accepted: " + response.ExternalShipmentId, response.ExternalShipmentId);
                default: return Retry(phase + " returned unrecognized status");
            }
        }
    }

    private Route4MeDispatchRequest ReadManifest(DispatchClaim claim)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand("""
            SELECT cr.ProductId, cr.Quantity
            FROM erp.ConsumedReservationLine AS cr
            WHERE cr.SalesOrderId=@order AND cr.WarehouseId=@warehouse
            ORDER BY cr.ProductId;
            """, conn);
        cmd.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = claim.SalesOrderId;
        cmd.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = claim.WarehouseId;
        var lines = new List<Route4MeDispatchLine>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) lines.Add(new(r.GetString(0), r.GetInt32(1)));

        using var destination = new SqlCommand("""
            SELECT loc.LocationId, loc.Address1, loc.Address2, loc.City, loc.Region,
                   loc.PostalCode, loc.CountryCode, loc.ContactName, loc.ContactPhone
            FROM erp.SalesOrder AS so
            INNER JOIN erp.CustomerLocation AS loc
                ON loc.LocationId=so.ShipToLocationId AND loc.CustomerId=so.CustomerId
            WHERE so.SalesOrderId=@order;
            """, conn);
        destination.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = claim.SalesOrderId;
        using var reader = destination.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("ERP ship-to location missing");
        string? Value(int n) => reader.IsDBNull(n) ? null : reader.GetString(n);
        string Required(int n, string field)
        {
            var value = Value(n);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("ERP ship-to " + field + " missing");
            return value;
        }
        var address = new Route4MeDeliveryDestination(
            Required(0, "location"), Required(1, "address line 1"), Value(2),
            Required(3, "city"), Required(4, "region"), Required(5, "postal code"),
            Required(6, "country"), Value(7), Value(8));
        return new(claim.DispatchRequestId, claim.DepartureId, claim.SalesOrderId,
            claim.WarehouseId, claim.PackingId, lines.AsReadOnly(), address);
    }

    private static string Truncate(string message) => message.Length <= 512 ? message : message[..512];
}
