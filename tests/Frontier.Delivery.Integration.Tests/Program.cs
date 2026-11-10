using Frontier.Delivery;
using System.Text.Json;

ArchitectureContext.Write(Console.Out, typeof(DeliveryExecution).Assembly);
Console.WriteLine("HARNESS: Frontier.Delivery.Integration.Tests");
Console.WriteLine("INTEGRATION: Actual Stage 2 dispatch result consumed by Stage 3.1; no external API calls");
Console.WriteLine();

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Provide path to captured Stage 2 DISPATCH JSON.");
    return 2;
}

int passed = 0, failed = 0;
void Check(string description, bool condition)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} [ERP/Shipping -> Route4Me/Delivery] {description}");
    if (condition) passed++; else failed++;
}

using JsonDocument input = JsonDocument.Parse(File.ReadAllText(args[0]));
JsonElement root = input.RootElement;
var dispatchStatus = root.GetProperty("Status").GetInt32();
var source = root.GetProperty("Shipment");
string shipmentId = source.GetProperty("ShipmentId").GetString() ?? "";
string orderId = source.GetProperty("SalesOrderId").GetString() ?? "";
string warehouseId = source.GetProperty("WarehouseId").GetString() ?? "";
string shipmentStatus = source.GetProperty("Status").GetString() ?? "";
var lines = source.GetProperty("Lines").EnumerateArray()
    .Select(line => new ShipmentLine(line.GetProperty("ProductId").GetString() ?? "", line.GetProperty("Quantity").GetInt32()))
    .ToArray();

Console.WriteLine($"DISPATCH HANDOFF Shipment={shipmentId} Order={orderId} Warehouse={warehouseId} Status={shipmentStatus} Items={lines.Sum(x => x.Quantity)}");
Check("Stage 2 dispatch result succeeded", dispatchStatus == 1);
Check("Stage 2 shipment status is Dispatched", shipmentStatus == "Dispatched");
Check("Shipment and business references supplied", !string.IsNullOrWhiteSpace(shipmentId) && !string.IsNullOrWhiteSpace(orderId) && !string.IsNullOrWhiteSpace(warehouseId));
Check("Dispatched product quantities valid", lines.Length > 0 && lines.All(l => !string.IsNullOrWhiteSpace(l.ProductId) && l.Quantity > 0));

var delivery = new DeliveryExecution();
var handoff = new DispatchedShipment(shipmentId, orderId, warehouseId, shipmentStatus, lines);
Check("Route4Me delivery accepts actual dispatch handoff", delivery.RegisterShipment(handoff));
Check("Order identity preserved across ERP -> Route4Me", delivery.GetShipment(shipmentId)?.SalesOrderId == orderId);
Check("Warehouse identity preserved", delivery.GetShipment(shipmentId)?.WarehouseId == warehouseId);
Check("Product identity and quantities preserved", delivery.GetShipment(shipmentId)?.Lines.SequenceEqual(lines) == true);

var eventId = "DEL-E2E-0001";
var proof = "POD-E2E-0001";
var occurredAt = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(-5));
var completion = new DeliveryEvent(eventId, shipmentId, DeliveryOutcome.Delivered, occurredAt, proof);
var result = delivery.Record(completion);
Console.WriteLine("DELIVERY RESULT " + JsonSerializer.Serialize(result));
Check("Delivery completion recorded", result.Status == DeliveryResultStatus.Recorded);
Check("Proof of delivery traceable to original shipment", delivery.GetOutcome(shipmentId)?.ProofReference == proof);
Check("Replayed delivery confirmation is idempotent", delivery.Record(completion).Status == DeliveryResultStatus.Replayed);
Check("Conflicting delivery outcome rejected", delivery.Record(completion with { EventId = "DEL-E2E-0002", ProofReference = "OTHER" }).Status == DeliveryResultStatus.Rejected);
Check("Original quantities remain unchanged", delivery.GetShipment(shipmentId)?.Lines.SequenceEqual(lines) == true);

Console.WriteLine($"RESULT: {passed}/{passed + failed} passed | {failed} failed");
return failed == 0 ? 0 : 1;
