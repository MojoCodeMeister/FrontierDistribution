namespace Frontier.Orders;

// Synthetic packing record and shipment PREPARATION only. No dispatch, carrier booking,
// invoice, inventory posting, WMS command, or modification to existing order/pick state.
public sealed record PackedLine(string ProductId, int Quantity);
public sealed record PackingConfirmation(string PackingId, string SalesOrderId, string WarehouseId,
    string PickConfirmationId, IReadOnlyList<PackedLine> Lines);
public enum PackingStatus { ReadyForShipment, Incomplete, AlreadyRecorded, Rejected }
public sealed record PackingResult(PackingStatus Status, string SalesOrderId, string WarehouseId,
    string? PackingId, string? PickConfirmationId, IReadOnlyList<PackedLine> Lines, string Message);

/// <summary>Single synthetic packing confirmation per picked order/warehouse, in memory.</summary>
public sealed class Packing
{
    private readonly Dictionary<(string Order, string Warehouse), (PackingConfirmation Input, PackingResult Result)> _recorded = new();

    public PackingResult Confirm(PickPlan plan, PickExecutionResult pick, PackingConfirmation? confirmation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pick);
        if (plan.Status != PickPlanStatus.Ready || string.IsNullOrWhiteSpace(plan.SalesOrderId) ||
            string.IsNullOrWhiteSpace(plan.WarehouseId) || plan.Lines is null || plan.Lines.Count == 0 ||
            plan.Lines.Any(x => x is null || string.IsNullOrWhiteSpace(x.ProductId) || x.Quantity <= 0) ||
            plan.Lines.Select(x => x.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Lines.Count)
            return Reject(plan, "Valid ready plan required");
        if (pick.Status is not (PickExecutionStatus.Completed or PickExecutionStatus.Partial) ||
            string.IsNullOrWhiteSpace(pick.ConfirmationId) ||
            !Same(pick.SalesOrderId, plan.SalesOrderId) || !Same(pick.WarehouseId, plan.WarehouseId) ||
            pick.Lines is null || pick.Lines.Count != plan.Lines.Count)
            return Reject(plan, "Valid matching pick execution required");
        var planned = plan.Lines.ToDictionary(x => x.ProductId, x => x.Quantity, StringComparer.OrdinalIgnoreCase);
        var picked = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in pick.Lines)
        {
            if (line is null || string.IsNullOrWhiteSpace(line.ProductId) || line.Quantity < 0 ||
                !planned.TryGetValue(line.ProductId, out var max) || line.Quantity > max ||
                !picked.TryAdd(line.ProductId, line.Quantity))
                return Reject(plan, "Pick execution inconsistent with plan");
        }
        var pickedComplete = plan.Lines.All(x => picked[x.ProductId] == x.Quantity);
        if ((pick.Status == PickExecutionStatus.Completed) != pickedComplete)
            return Reject(plan, "Pick completion status inconsistent with quantities");
        if (confirmation is null || string.IsNullOrWhiteSpace(confirmation.PackingId) ||
            !Same(confirmation.SalesOrderId, plan.SalesOrderId) ||
            !Same(confirmation.WarehouseId, plan.WarehouseId) ||
            !Same(confirmation.PickConfirmationId, pick.ConfirmationId) || confirmation.Lines is null)
            return Reject(plan, "Packing ID and matching pick confirmation required");

        var key = (plan.SalesOrderId.ToUpperInvariant(), plan.WarehouseId.ToUpperInvariant());
        if (_recorded.TryGetValue(key, out var prior))
            return Equivalent(prior.Input, confirmation)
                ? prior.Result with { Status = PackingStatus.AlreadyRecorded, Message = "Packing already recorded" }
                : Reject(plan, "Different packing confirmation already recorded");

        if (confirmation.Lines.Count != planned.Count)
            return Reject(plan, "Packing must account for every planned product");
        var packed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in confirmation.Lines)
        {
            if (line is null || string.IsNullOrWhiteSpace(line.ProductId) || line.Quantity < 0 ||
                !picked.TryGetValue(line.ProductId, out var max) || line.Quantity > max ||
                !packed.TryAdd(line.ProductId, line.Quantity))
                return Reject(plan, "Invalid, duplicate or excessive packed quantity");
        }
        var packedComplete = pickedComplete && plan.Lines.All(x => packed[x.ProductId] == x.Quantity);
        var result = new PackingResult(packedComplete ? PackingStatus.ReadyForShipment : PackingStatus.Incomplete,
            plan.SalesOrderId, plan.WarehouseId, confirmation.PackingId, pick.ConfirmationId,
            Array.AsReadOnly(plan.Lines.Select(x => new PackedLine(x.ProductId, packed[x.ProductId])).ToArray()),
            packedComplete ? "Shipment preparation ready; no shipment created or dispatched" :
                "Packing incomplete; shipment preparation blocked");
        var snapshot = confirmation with {
            Lines = Array.AsReadOnly(confirmation.Lines.Select(x => x with { }).ToArray())
        };
        _recorded.Add(key, (snapshot, result));
        return result;
    }

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static PackingResult Reject(PickPlan plan, string message) =>
        new(PackingStatus.Rejected, plan.SalesOrderId, plan.WarehouseId, null, null, Array.Empty<PackedLine>(), message);
    private static bool Equivalent(PackingConfirmation a, PackingConfirmation b) =>
        Same(a.PackingId, b.PackingId) && Same(a.SalesOrderId, b.SalesOrderId) &&
        Same(a.WarehouseId, b.WarehouseId) && Same(a.PickConfirmationId, b.PickConfirmationId) &&
        a.Lines.Count == b.Lines.Count && a.Lines.All(x =>
            b.Lines.Count(y => y is not null && Same(x.ProductId, y.ProductId) && x.Quantity == y.Quantity) == 1);
}
