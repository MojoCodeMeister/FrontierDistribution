using System.Text.Json;
using Frontier.Identity;
using Frontier.Orders;

// Stage 2 synthetic end-to-end harness. No ERP/WMS/carrier calls or persistent changes.
var directory = AppContext.BaseDirectory;
var catalog = IdentityCatalog.Load(Path.Combine(directory, "synthetic-identities.json"));
var request = JsonSerializer.Deserialize<IncomingOrder>(File.ReadAllText(Path.Combine(directory, "synthetic-order.json")))
              ?? throw new InvalidDataException("Synthetic order missing");
var stock = JsonSerializer.Deserialize<StockQuantity[]>(File.ReadAllText(Path.Combine(directory, "synthetic-stock.json")))
            ?? throw new InvalidDataException("Synthetic stock missing");

var checks = 0;
var failures = 0;
void Verify(string label, bool condition)
{
    checks++;
    if (!condition) failures++;
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {label}");
}

static bool SameQuantities(IEnumerable<SalesOrderLine> expected, IEnumerable<ShipmentLine> actual)
{
    var demand = expected.GroupBy(x => x.ProductId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.Sum(l => (long)l.Quantity), StringComparer.OrdinalIgnoreCase);
    var dispatched = actual.GroupBy(x => x.ProductId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.Sum(l => (long)l.Quantity), StringComparer.OrdinalIgnoreCase);
    return demand.Count == dispatched.Count && demand.All(x => dispatched.TryGetValue(x.Key, out var n) && n == x.Value);
}

Console.WriteLine("STAGE 2 END-TO-END: SYNTHETIC ORDER TO DISPATCH");
Console.WriteLine("Boundaries: in-memory only; no stock reservation/posting, WMS operation, carrier booking or invoice.");
var intake = new OrderIntake(catalog, new ProductIdentityResolver(catalog));
var received = intake.Receive(request);
Verify("Order intake created", received.Status == IntakeStatus.Created && received.Order is not null);
if (received.Order is null) return 1;
var order = received.Order;
Verify("Webstore source retained", order.SourceSystem == request.SourceSystem && order.ExternalOrderId == request.ExternalOrderId);
Verify("Intake replay is idempotent", intake.Receive(request).Status == IntakeStatus.AlreadyReceived);
Verify("Order line quantities preserved", order.Lines.Sum(x => x.Quantity) == request.Lines.Sum(x => x.Quantity));

var inventory = new InventoryAvailability(catalog, "WH-01", stock);
var assessed = inventory.Assess(order);
Verify("Inventory available", assessed.Status == AvailabilityStatus.Available);
Verify("Assessment refers to order", assessed.SalesOrderId == order.SalesOrderId && assessed.WarehouseId == "WH-01");
var plan = new PickPlanning().Create(order, assessed);
Verify("Pick plan ready", plan.Status == PickPlanStatus.Ready && plan.Lines.Count == assessed.Lines.Count);
Verify("Pick plan carries order and warehouse", plan.SalesOrderId == order.SalesOrderId && plan.WarehouseId == assessed.WarehouseId);

var pickConfirmation = new PickConfirmation("PICK-E2E-0001", order.SalesOrderId, plan.WarehouseId,
    plan.Lines.Select(x => new PickedLine(x.ProductId, x.Quantity)).ToArray());
var picking = new PickExecution();
var picked = picking.Confirm(plan, pickConfirmation);
Verify("Pick execution completed", picked.Status == PickExecutionStatus.Completed);
Verify("Pick execution replay is idempotent", picking.Confirm(plan, pickConfirmation).Status == PickExecutionStatus.AlreadyRecorded);
var packingConfirmation = new PackingConfirmation("PACK-E2E-0001", order.SalesOrderId, plan.WarehouseId,
    pickConfirmation.ConfirmationId, picked.Lines.Select(x => new PackedLine(x.ProductId, x.Quantity)).ToArray());
var packing = new Packing();
var packed = packing.Confirm(plan, picked, packingConfirmation);
Verify("Packing ready for shipment", packed.Status == PackingStatus.ReadyForShipment);
Verify("Packing references pick execution", packed.PickConfirmationId == picked.ConfirmationId);
Verify("Packing replay is idempotent", packing.Confirm(plan, picked, packingConfirmation).Status == PackingStatus.AlreadyRecorded);

var shipping = new Shipping();
var prepared = shipping.Create(packed, "SHIP-E2E-0001");
Verify("Shipment prepared", prepared.Status == ShipmentStatus.Prepared && prepared.Shipment?.Status == "Prepared");
Verify("Shipment references packing and pick", prepared.Shipment?.PackingId == packed.PackingId &&
    prepared.Shipment?.PickConfirmationId == picked.ConfirmationId);
Verify("Shipment references order and warehouse", prepared.Shipment?.SalesOrderId == order.SalesOrderId &&
    prepared.Shipment.WarehouseId == plan.WarehouseId);
Verify("Shipment quantities equal accepted order", prepared.Shipment is not null && SameQuantities(order.Lines, prepared.Shipment.Lines));
Verify("Shipment replay is idempotent", shipping.Create(packed, "SHIP-E2E-0001").Status == ShipmentStatus.AlreadyRecorded);
Verify("Conflicting shipment rejected", shipping.Create(packed, "SHIP-E2E-CONFLICT").Status == ShipmentStatus.Rejected);

var dispatch = shipping.Dispatch(new DispatchConfirmation("DISP-E2E-0001", "SHIP-E2E-0001"));
Verify("Shipment dispatched", dispatch.Status == ShipmentStatus.Dispatched && dispatch.Shipment?.Status == "Dispatched");
Verify("Dispatch replay is idempotent", shipping.Dispatch(new DispatchConfirmation("DISP-E2E-0001", "SHIP-E2E-0001")).Status == ShipmentStatus.AlreadyRecorded);
Verify("Conflicting dispatch rejected", shipping.Dispatch(new DispatchConfirmation("DISP-E2E-CONFLICT", "SHIP-E2E-0001")).Status == ShipmentStatus.Rejected);
Verify("Dispatched quantities preserved", dispatch.Shipment is not null && SameQuantities(order.Lines, dispatch.Shipment.Lines));
Verify("Order not mutated", order.Status == "Received");
Verify("Inventory not consumed", inventory.Assess(order).Lines.All(x =>
    assessed.Lines.Any(y => string.Equals(x.ProductId, y.ProductId, StringComparison.OrdinalIgnoreCase) &&
                             x.OnHand == y.OnHand && x.ShortBy == y.ShortBy)));
Verify("Pick and pack states not mutated", picked.Status == PickExecutionStatus.Completed && packed.Status == PackingStatus.ReadyForShipment);

// Separate blocked path: shortage must never produce a ready plan or shipment.
var shortStock = stock.Select(x => x with { OnHand = 0 }).ToArray();
var shortAssessment = new InventoryAvailability(catalog, "WH-01", shortStock).Assess(order);
var blocked = new PickPlanning().Create(order, shortAssessment);
Verify("Short stock classified", shortAssessment.Status == AvailabilityStatus.Short);
Verify("Short stock blocks pick plan", blocked.Status == PickPlanStatus.BlockedShort && blocked.Lines.Count == 0);
Verify("Blocked pick cannot confirm", new PickExecution().Confirm(blocked, pickConfirmation).Status == PickExecutionStatus.Rejected);

Console.WriteLine($"RESULT: {checks - failures}/{checks} passed");
Console.WriteLine(failures == 0 ? "STAGE 2 INTEGRATION: VERIFIED IN THIS RUN" : "STAGE 2 INTEGRATION: FAILED");
return failures == 0 ? 0 : 1;
