namespace Frontier.Orders;

// A synthetic report of work performed. It is not a WMS command, inventory posting,
// reservation, shipment, or modification of the order or pick plan.
public sealed record PickedLine(string ProductId, int Quantity);
public sealed record PickConfirmation(string ConfirmationId, string SalesOrderId, string WarehouseId,
    IReadOnlyList<PickedLine> Lines);
public enum PickExecutionStatus { Completed, Partial, AlreadyRecorded, Rejected }
public sealed record PickExecutionResult(PickExecutionStatus Status, string SalesOrderId,
    string WarehouseId, string? ConfirmationId, IReadOnlyList<PickedLine> Lines, string Message);

/// <summary>One synthetic pick confirmation per plan, stored in memory for the life of this instance.</summary>
public sealed class PickExecution
{
    private readonly Dictionary<(string Order, string Warehouse), (PickConfirmation Input, PickExecutionResult Result)> _recorded =
        new();

    public PickExecutionResult Confirm(PickPlan plan, PickConfirmation? confirmation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.SalesOrderId) || string.IsNullOrWhiteSpace(plan.WarehouseId) ||
            plan.Status != PickPlanStatus.Ready || plan.Lines is null || plan.Lines.Count == 0 ||
            plan.Lines.Any(l => l is null || string.IsNullOrWhiteSpace(l.ProductId) || l.Quantity <= 0) ||
            plan.Lines.Select(l => l.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Lines.Count)
            return Reject(plan, "A valid ready pick plan is required");
        if (confirmation is null || string.IsNullOrWhiteSpace(confirmation.ConfirmationId) ||
            !string.Equals(confirmation.SalesOrderId, plan.SalesOrderId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(confirmation.WarehouseId, plan.WarehouseId, StringComparison.OrdinalIgnoreCase) ||
            confirmation.Lines is null)
            return Reject(plan, "Confirmation ID, order and warehouse must match the pick plan");

        // Check replay against an immutable snapshot, never mutable caller-owned collections.
        var key = (plan.SalesOrderId.ToUpperInvariant(), plan.WarehouseId.ToUpperInvariant());
        if (_recorded.TryGetValue(key, out var prior))
        {
            return Equivalent(prior.Input, confirmation)
                ? prior.Result with { Status = PickExecutionStatus.AlreadyRecorded, Message = "Confirmation already recorded" }
                : Reject(plan, "Pick plan already has a different confirmation");
        }

        var planned = plan.Lines.ToDictionary(l => l.ProductId, l => l.Quantity, StringComparer.OrdinalIgnoreCase);
        if (confirmation.Lines.Count != planned.Count)
            return Reject(plan, "Confirmation must account for every planned product");
        var picked = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in confirmation.Lines)
        {
            if (line is null || string.IsNullOrWhiteSpace(line.ProductId) || line.Quantity < 0 ||
                !planned.TryGetValue(line.ProductId, out var maximum) || line.Quantity > maximum ||
                !picked.TryAdd(line.ProductId, line.Quantity))
                return Reject(plan, "Invalid, duplicate, or excessive picked quantity");
        }
        var actual = plan.Lines.Select(l => new PickedLine(l.ProductId, picked[l.ProductId])).ToArray();
        var complete = plan.Lines.All(l => picked[l.ProductId] == l.Quantity);
        var status = complete ? PickExecutionStatus.Completed : PickExecutionStatus.Partial;
        var result = new PickExecutionResult(status, plan.SalesOrderId, plan.WarehouseId,
            confirmation.ConfirmationId, Array.AsReadOnly(actual),
            complete ? "Synthetic pick confirmed in full; no stock posted" : "Synthetic partial pick recorded; no stock posted");
        var snapshot = new PickConfirmation(confirmation.ConfirmationId, confirmation.SalesOrderId,
            confirmation.WarehouseId, Array.AsReadOnly(confirmation.Lines.Select(l => l with { }).ToArray()));
        _recorded.Add(key, (snapshot, result));
        return result;
    }

    private static PickExecutionResult Reject(PickPlan plan, string message) =>
        new(PickExecutionStatus.Rejected, plan.SalesOrderId, plan.WarehouseId, null,
            Array.Empty<PickedLine>(), message);

    private static bool Equivalent(PickConfirmation a, PickConfirmation b) =>
        string.Equals(a.ConfirmationId, b.ConfirmationId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.SalesOrderId, b.SalesOrderId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.WarehouseId, b.WarehouseId, StringComparison.OrdinalIgnoreCase) &&
        a.Lines is not null && b.Lines is not null && a.Lines.Count == b.Lines.Count &&
        a.Lines.All(x => b.Lines.Count(y => y is not null &&
            string.Equals(x.ProductId, y.ProductId, StringComparison.OrdinalIgnoreCase) && x.Quantity == y.Quantity) == 1);
}
