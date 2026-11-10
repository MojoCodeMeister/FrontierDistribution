using System.Collections.ObjectModel;

namespace Frontier.Delivery;

// Business software: Route4Me (delivery execution); upstream business record: ERP/shipping.
// Boundary DTO, deliberately independent of the existing Frontier.Shipping assembly.
public sealed record ShipmentLine(string ProductId, int Quantity);
public sealed record DispatchedShipment(string ShipmentId, string SalesOrderId, string WarehouseId,
    string Status, IReadOnlyList<ShipmentLine> Lines);
public enum DeliveryOutcome { Delivered, Exception }
public sealed record DeliveryEvent(string EventId, string ShipmentId, DeliveryOutcome Outcome,
    DateTimeOffset OccurredAt, string? ProofReference = null, string? ExceptionReason = null);
public enum DeliveryResultStatus { Recorded, Replayed, Rejected }
public sealed record DeliveryResult(DeliveryResultStatus Status, string Message, DeliveryEvent? Event = null);

public sealed class DeliveryExecution
{
    private readonly Dictionary<string, DispatchedShipment> _shipments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeliveryEvent> _eventsByShipment = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeliveryEvent> _eventsById = new(StringComparer.Ordinal);

    public bool RegisterShipment(DispatchedShipment? shipment)
    {
        if (shipment is null || string.IsNullOrWhiteSpace(shipment.ShipmentId)
            || string.IsNullOrWhiteSpace(shipment.SalesOrderId)
            || string.IsNullOrWhiteSpace(shipment.WarehouseId)
            || shipment.Lines is null || shipment.Lines.Count == 0
            || shipment.Lines.Any(l => string.IsNullOrWhiteSpace(l.ProductId) || l.Quantity <= 0)
            || shipment.Lines.Select(l => l.ProductId).Distinct(StringComparer.Ordinal).Count() != shipment.Lines.Count)
            return false;
        if (_shipments.ContainsKey(shipment.ShipmentId)) return false;
        _shipments.Add(shipment.ShipmentId, new DispatchedShipment(shipment.ShipmentId,
            shipment.SalesOrderId, shipment.WarehouseId, shipment.Status,
            Array.AsReadOnly(shipment.Lines.Select(l => new ShipmentLine(l.ProductId, l.Quantity)).ToArray())));
        return true;
    }

    public DeliveryResult Record(DeliveryEvent? attempt)
    {
        if (attempt is null || string.IsNullOrWhiteSpace(attempt.EventId) || string.IsNullOrWhiteSpace(attempt.ShipmentId)
            || !Enum.IsDefined(attempt.Outcome) || attempt.OccurredAt == default)
            return Reject("Invalid delivery event");
        if (!_shipments.TryGetValue(attempt.ShipmentId, out var shipment))
            return Reject("Unknown shipment");
        if (shipment.Status != "Dispatched") return Reject("Shipment is not dispatched");
        if (attempt.Outcome == DeliveryOutcome.Delivered && string.IsNullOrWhiteSpace(attempt.ProofReference))
            return Reject("Delivery proof reference required");
        if (attempt.Outcome == DeliveryOutcome.Exception && string.IsNullOrWhiteSpace(attempt.ExceptionReason))
            return Reject("Delivery exception reason required");
        if (_eventsById.TryGetValue(attempt.EventId, out var eventIdOwner))
        {
            if (Equivalent(eventIdOwner, attempt)) return new(DeliveryResultStatus.Replayed, "Identical delivery event replay", eventIdOwner);
            return Reject("Delivery event ID reused with conflicting data");
        }
        if (_eventsByShipment.ContainsKey(attempt.ShipmentId)) return Reject("Shipment already has a terminal delivery outcome");
        _eventsByShipment.Add(attempt.ShipmentId, attempt);
        _eventsById.Add(attempt.EventId, attempt);
        return new(DeliveryResultStatus.Recorded, "Delivery outcome recorded", attempt);
    }

    public DeliveryEvent? GetOutcome(string shipmentId) => _eventsByShipment.GetValueOrDefault(shipmentId);
    public DispatchedShipment? GetShipment(string shipmentId) => _shipments.GetValueOrDefault(shipmentId);
    private static DeliveryResult Reject(string message) => new(DeliveryResultStatus.Rejected, message);
    private static bool Equivalent(DeliveryEvent a, DeliveryEvent b) => a == b;
}
