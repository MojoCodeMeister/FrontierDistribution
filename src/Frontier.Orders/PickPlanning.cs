namespace Frontier.Orders;

// Synthetic warehouse work proposal. A pick plan is NOT a reservation, pick confirmation,
// stock movement, allocation, or instruction issued to a real WMS/Voxware environment.
public enum PickPlanStatus { Ready, BlockedShort, BlockedUnknown }
public sealed record PickLine(string ProductId, int Quantity);
public sealed record PickPlan(string SalesOrderId, string WarehouseId, PickPlanStatus Status,
    IReadOnlyList<PickLine> Lines, string Message);

public sealed class PickPlanning
{
    public PickPlan Create(SalesOrder order, AvailabilityResult assessment)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(assessment);
        if (string.IsNullOrWhiteSpace(order.SalesOrderId) ||
            !string.Equals(order.SalesOrderId, assessment.SalesOrderId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Availability assessment does not belong to order", nameof(assessment));
        if (string.IsNullOrWhiteSpace(assessment.WarehouseId))
            throw new ArgumentException("Warehouse is required", nameof(assessment));
        if (order.Lines is null || order.Lines.Count == 0 ||
            order.Lines.Any(x => x is null || string.IsNullOrWhiteSpace(x.ProductId) || x.Quantity <= 0))
            throw new ArgumentException("Order lines must identify products and positive quantities", nameof(order));
        var demand = order.Lines.GroupBy(x => x.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(l => (long)l.Quantity), StringComparer.OrdinalIgnoreCase);
        if (demand.Values.Any(x => x > int.MaxValue))
            throw new ArgumentException("Quantity exceeds supported range", nameof(order));
        if (assessment.Lines is null || assessment.Lines.Count != demand.Count)
            throw new ArgumentException("Assessment does not cover order demand", nameof(assessment));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in assessment.Lines)
        {
            if (line is null || string.IsNullOrWhiteSpace(line.ProductId) || !seen.Add(line.ProductId) ||
                !demand.TryGetValue(line.ProductId, out var requested) || requested != line.Requested ||
                line.OnHand < 0 || line.ShortBy < 0 ||
                (line.OnHand is null && line.ShortBy is not null) ||
                (line.OnHand is not null && line.ShortBy != Math.Max(0, line.Requested - line.OnHand.Value)))
                throw new ArgumentException("Assessment is inconsistent with order", nameof(assessment));
        }
        var expected = assessment.Lines.Any(x => x.OnHand is null) ? AvailabilityStatus.Unknown :
            assessment.Lines.Any(x => x.ShortBy > 0) ? AvailabilityStatus.Short : AvailabilityStatus.Available;
        if (assessment.Status != expected)
            throw new ArgumentException("Assessment status is inconsistent", nameof(assessment));
        if (expected == AvailabilityStatus.Unknown)
            return new PickPlan(order.SalesOrderId, assessment.WarehouseId, PickPlanStatus.BlockedUnknown,
                Array.Empty<PickLine>(), "Inventory not known for every product; no pick list generated");
        if (expected == AvailabilityStatus.Short)
            return new PickPlan(order.SalesOrderId, assessment.WarehouseId, PickPlanStatus.BlockedShort,
                Array.Empty<PickLine>(), "Insufficient stock; no pick list generated");
        var lines = demand.Select(x => new PickLine(x.Key, (int)x.Value)).ToArray();
        return new PickPlan(order.SalesOrderId, assessment.WarehouseId, PickPlanStatus.Ready,
            lines, "Proposed pick list; no inventory committed");
    }
}
