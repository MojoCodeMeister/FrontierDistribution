namespace Frontier.Orders;

// Synthetic shipment records. No carrier API, WMS command, inventory posting, invoicing,
// or mutation of the original sales order, pick execution, or packing confirmation.
public sealed record ShipmentLine(string ProductId, int Quantity);
public enum ShipmentStatus { Prepared, Dispatched, AlreadyRecorded, Rejected }
public sealed record ShipmentRecord(string ShipmentId, string SalesOrderId, string WarehouseId,
    string PackingId, string PickConfirmationId, IReadOnlyList<ShipmentLine> Lines, string Status);
public sealed record ShipmentResult(ShipmentStatus Status, ShipmentRecord? Shipment, string Message);
public sealed record DispatchConfirmation(string DispatchId, string ShipmentId);

/// <summary>Single in-memory shipment per order/warehouse, with explicit synthetic dispatch.</summary>
public sealed class Shipping
{
    private sealed class Entry
    {
        public required ShipmentRecord Record { get; set; }
        public string? DispatchId { get; set; }
    }
    private readonly Dictionary<(string Order, string Warehouse), Entry> _byOrder = new();
    private readonly Dictionary<string, Entry> _byShipment = new(StringComparer.OrdinalIgnoreCase);

    public ShipmentResult Create(PackingResult? packing, string? shipmentId)
    {
        if (packing is null || packing.Status != PackingStatus.ReadyForShipment ||
            string.IsNullOrWhiteSpace(packing.SalesOrderId) || string.IsNullOrWhiteSpace(packing.WarehouseId) ||
            string.IsNullOrWhiteSpace(packing.PackingId) || string.IsNullOrWhiteSpace(packing.PickConfirmationId) ||
            string.IsNullOrWhiteSpace(shipmentId) || packing.Lines is null || packing.Lines.Count == 0 ||
            packing.Lines.Any(x => x is null || string.IsNullOrWhiteSpace(x.ProductId) || x.Quantity <= 0) ||
            packing.Lines.Select(x => x.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packing.Lines.Count)
            return Reject("Shipment requires a valid ready packing result and shipment ID");

        var key = (packing.SalesOrderId.ToUpperInvariant(), packing.WarehouseId.ToUpperInvariant());
        if (_byOrder.TryGetValue(key, out var existing))
        {
            var r = existing.Record;
            return Same(shipmentId, r.ShipmentId) && Same(r.PackingId, packing.PackingId) &&
                   Same(r.PickConfirmationId, packing.PickConfirmationId) && SameLines(r.Lines, packing.Lines)
                ? new ShipmentResult(ShipmentStatus.AlreadyRecorded, r, "Shipment already recorded")
                : Reject("Different shipment already recorded for order and warehouse");
        }
        if (_byShipment.ContainsKey(shipmentId)) return Reject("Shipment ID already used");

        var lines = Array.AsReadOnly(packing.Lines.Select(x => new ShipmentLine(x.ProductId, x.Quantity)).ToArray());
        var record = new ShipmentRecord(shipmentId, packing.SalesOrderId, packing.WarehouseId,
            packing.PackingId!, packing.PickConfirmationId!, lines, "Prepared");
        var entry = new Entry { Record = record };
        _byOrder.Add(key, entry);
        _byShipment.Add(shipmentId, entry);
        return new ShipmentResult(ShipmentStatus.Prepared, record, "Synthetic shipment prepared; not dispatched");
    }

    public ShipmentResult Dispatch(DispatchConfirmation? confirmation)
    {
        if (confirmation is null || string.IsNullOrWhiteSpace(confirmation.DispatchId) ||
            string.IsNullOrWhiteSpace(confirmation.ShipmentId)) return Reject("Dispatch and shipment IDs required");
        if (!_byShipment.TryGetValue(confirmation.ShipmentId, out var entry))
            return Reject("Shipment does not exist");
        if (entry.DispatchId is not null)
            return Same(entry.DispatchId, confirmation.DispatchId)
                ? new ShipmentResult(ShipmentStatus.AlreadyRecorded, entry.Record, "Dispatch already recorded")
                : Reject("Shipment already dispatched with another confirmation");
        if (_byShipment.Values.Any(x => x.DispatchId is not null && Same(x.DispatchId, confirmation.DispatchId)))
            return Reject("Dispatch ID already used");
        entry.DispatchId = confirmation.DispatchId;
        entry.Record = entry.Record with { Status = "Dispatched" };
        return new ShipmentResult(ShipmentStatus.Dispatched, entry.Record, "Synthetic dispatch confirmed; no external posting");
    }

    private static ShipmentResult Reject(string reason) => new(ShipmentStatus.Rejected, null, reason);
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool SameLines(IReadOnlyList<ShipmentLine> a, IReadOnlyList<PackedLine> b) =>
        a.Count == b.Count && a.All(x => b.Count(y => Same(x.ProductId, y.ProductId) && x.Quantity == y.Quantity) == 1);
}
