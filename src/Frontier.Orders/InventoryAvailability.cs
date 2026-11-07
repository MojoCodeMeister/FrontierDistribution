using Frontier.Identity;

namespace Frontier.Orders;

// Synthetic, read-only availability snapshot. No allocation, reservation, or WMS authority is implied.
public sealed record StockQuantity(string WarehouseId, string ProductId, int OnHand);
public sealed record AvailabilityLine(string ProductId, int Requested, int? OnHand, int? ShortBy);
public enum AvailabilityStatus { Available, Short, Unknown }
public sealed record AvailabilityResult(string SalesOrderId, string WarehouseId, AvailabilityStatus Status,
    IReadOnlyList<AvailabilityLine> Lines);

public sealed class InventoryAvailability
{
    private readonly string _warehouseId;
    private readonly Dictionary<string, int> _stock;

    public InventoryAvailability(IdentityCatalog catalog, string warehouseId, IEnumerable<StockQuantity> quantities)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(quantities);
        if (catalog.Validate().Count != 0) throw new InvalidDataException("Invalid identity catalog");
        if (string.IsNullOrWhiteSpace(warehouseId) ||
            !catalog.Warehouses.Any(w => string.Equals(w.WarehouseId, warehouseId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Unknown warehouse", nameof(warehouseId));
        _warehouseId = warehouseId;
        var validPositions = catalog.InventoryPositions
            .Where(x => string.Equals(x.WarehouseId, warehouseId, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _stock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var quantity in quantities)
        {
            if (quantity is null || !string.Equals(quantity.WarehouseId, warehouseId, StringComparison.OrdinalIgnoreCase) ||
                !validPositions.Contains(quantity.ProductId) || quantity.OnHand < 0 ||
                !_stock.TryAdd(quantity.ProductId, quantity.OnHand))
                throw new InvalidDataException("Invalid, conflicting, or duplicate warehouse stock quantity");
        }
    }

    public AvailabilityResult Assess(SalesOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        // Aggregate repeated products before comparing to stock; evaluating line-by-line would overpromise availability.
        var lines = order.Lines.GroupBy(x => x.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var demand = group.Sum(x => x.Quantity);
                int? onHand = _stock.TryGetValue(group.Key, out var n) ? n : null;
                int? shortBy = onHand is null ? null : Math.Max(0, demand - onHand.Value);
                return new AvailabilityLine(group.Key, demand, onHand, shortBy);
            }).ToArray();
        var status = lines.Any(x => x.OnHand is null) ? AvailabilityStatus.Unknown :
            lines.Any(x => x.ShortBy > 0) ? AvailabilityStatus.Short : AvailabilityStatus.Available;
        return new AvailabilityResult(order.SalesOrderId, _warehouseId, status, lines);
    }
}
