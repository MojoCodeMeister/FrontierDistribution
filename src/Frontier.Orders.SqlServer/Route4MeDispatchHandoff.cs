using System.Data;
using Microsoft.Data.SqlClient;

namespace Frontier.Orders.SqlServer;

// Q25.5d1: application boundary only. The real Route4Me transport is deliberately not implemented.
public sealed record Route4MeDispatchLine(string ProductId, int Quantity);
public sealed record Route4MeDispatchRequest(string DispatchRequestId, string DepartureId,
    string SalesOrderId, string WarehouseId, string PackingId, IReadOnlyList<Route4MeDispatchLine> Lines);
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
        HandoffResult Accept(string reason)
        {
            var stored = _delivery.MarkAccepted(dispatchRequestId, claim.LeaseToken);
            return new(stored ? HandoffState.Accepted : HandoffState.LeaseLost, dispatchRequestId, reason);
        }
        try
        {
            var request = ReadManifest(claim);
            if (request.Lines.Count == 0) return Fail("ERP consumption contains no shipment lines");
            var existing = await _gateway.ReconcileAsync(request.DispatchRequestId, cancellationToken);
            var previous = Evaluate(existing, request, "reconciliation");
            if (previous is not null) return previous;
            // Only a definitive NotFound result permits an attempted submission.
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
                        return Fail("Remote acceptance lacks matching request identity and shipment reference");
                    return Accept(phase + " accepted: " + response.ExternalShipmentId);
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
        using var r = cmd.ExecuteReader();
        while (r.Read()) lines.Add(new(r.GetString(0), r.GetInt32(1)));
        return new(claim.DispatchRequestId, claim.DepartureId, claim.SalesOrderId,
            claim.WarehouseId, claim.PackingId, lines.AsReadOnly());
    }

    private static string Truncate(string message) => message.Length <= 512 ? message : message[..512];
}
