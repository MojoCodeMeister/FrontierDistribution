using System.Text.Json;
using Frontier.Identity;
using Frontier.Orders;

var dir = AppContext.BaseDirectory;
var catalog = IdentityCatalog.Load(Path.Combine(dir, "synthetic-identities.json"));
var incoming = JsonSerializer.Deserialize<IncomingOrder>(File.ReadAllText(Path.Combine(dir, "synthetic-order.json")))!;
var stock = JsonSerializer.Deserialize<StockQuantity[]>(File.ReadAllText(Path.Combine(dir, "synthetic-stock.json")))!;
var order = new OrderIntake(catalog, new ProductIdentityResolver(catalog)).Receive(incoming).Order!;
var availability = new InventoryAvailability(catalog, "WH-01", stock);
var plan = new PickPlanning().Create(order, availability.Assess(order));
var pick = new PickExecution().Confirm(plan, new PickConfirmation("PICK-0001", order.SalesOrderId, "WH-01", new[] {
    new PickedLine("P000142", 3), new PickedLine("P000143", 2)
}));
var pack = new Packing().Confirm(plan, pick, new PackingConfirmation("PACK-0001", order.SalesOrderId, "WH-01", "PICK-0001", new[] {
    new PackedLine("P000142", 3), new PackedLine("P000143", 2)
}));
var shipping = new Shipping();
var created = shipping.Create(pack, "SHIP-0001");
var dispatched = shipping.Dispatch(new DispatchConfirmation("DISP-0001", "SHIP-0001"));
Console.WriteLine("STAGE 2: webstore -> order -> availability -> pick -> pack -> shipment -> dispatch (synthetic)");
Console.WriteLine("CREATED " + JsonSerializer.Serialize(created));
Console.WriteLine("DISPATCH " + JsonSerializer.Serialize(dispatched));
int total = 0, failures = 0;
void Check(string name, bool condition) { total++; if (!condition) failures++; Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}"); }
Check("Shipment prepared from ready packing", created.Status == ShipmentStatus.Prepared && created.Shipment?.Status == "Prepared");
Check("Order, warehouse and packing references preserved", created.Shipment?.SalesOrderId == order.SalesOrderId && created.Shipment.WarehouseId == "WH-01" && created.Shipment.PackingId == "PACK-0001" && created.Shipment.PickConfirmationId == "PICK-0001");
Check("Shipment quantities preserved", created.Shipment?.Lines.Single(x => x.ProductId == "P000142").Quantity == 3 && created.Shipment.Lines.Single(x => x.ProductId == "P000143").Quantity == 2);
Check("Idempotent shipment replay", shipping.Create(pack, "SHIP-0001").Status == ShipmentStatus.AlreadyRecorded);
Check("Reordered packing replay equivalent", shipping.Create(pack with { Lines = new[] { pack.Lines[1], pack.Lines[0] } }, "SHIP-0001").Status == ShipmentStatus.AlreadyRecorded);
Check("Conflicting shipment ID rejected", shipping.Create(pack, "SHIP-0002").Status == ShipmentStatus.Rejected);
Check("Conflicting packing reference rejected", shipping.Create(pack with { PackingId = "PACK-OTHER" }, "SHIP-0001").Status == ShipmentStatus.Rejected);
Check("Conflicting shipment quantities rejected", shipping.Create(pack with { Lines = new[] { new PackedLine("P000142", 1), new PackedLine("P000143", 2) } }, "SHIP-0001").Status == ShipmentStatus.Rejected);
Check("Incomplete packing rejected", new Shipping().Create(pack with { Status = PackingStatus.Incomplete }, "SHIP-0002").Status == ShipmentStatus.Rejected);
Check("Missing shipment ID rejected", new Shipping().Create(pack, "").Status == ShipmentStatus.Rejected);
Check("Missing packing ID rejected", new Shipping().Create(pack with { PackingId = "" }, "SHIP-002").Status == ShipmentStatus.Rejected);
Check("Duplicate packing product rejected", new Shipping().Create(pack with { Lines = new[] { pack.Lines[0], pack.Lines[0] } }, "SHIP-002").Status == ShipmentStatus.Rejected);
Check("Invalid quantity rejected", new Shipping().Create(pack with { Lines = new[] { new PackedLine("P000142", 0) } }, "SHIP-002").Status == ShipmentStatus.Rejected);
Check("Null packing rejected", new Shipping().Create(null, "SHIP-002").Status == ShipmentStatus.Rejected);
var retry = new Shipping();
retry.Create(pack with { Status = PackingStatus.Incomplete }, "SHIP-0003");
Check("Valid retry after rejected creation succeeds", retry.Create(pack, "SHIP-0003").Status == ShipmentStatus.Prepared);
Check("Dispatch confirmed separately", dispatched.Status == ShipmentStatus.Dispatched && dispatched.Shipment?.Status == "Dispatched");
Check("Dispatch replay idempotent", shipping.Dispatch(new DispatchConfirmation("DISP-0001", "SHIP-0001")).Status == ShipmentStatus.AlreadyRecorded);
Check("Conflicting dispatch ID rejected", shipping.Dispatch(new DispatchConfirmation("DISP-0002", "SHIP-0001")).Status == ShipmentStatus.Rejected);
Check("Unknown shipment dispatch rejected", shipping.Dispatch(new DispatchConfirmation("DISP-0003", "SHIP-UNKNOWN")).Status == ShipmentStatus.Rejected);
Check("Missing dispatch ID rejected", shipping.Dispatch(new DispatchConfirmation("", "SHIP-0001")).Status == ShipmentStatus.Rejected);
Check("Null dispatch rejected", shipping.Dispatch(null).Status == ShipmentStatus.Rejected);
var duplicateId = new Shipping();
duplicateId.Create(pack, "SHIP-0001");
Check("Shipment ID cannot be reused for another order", duplicateId.Create(pack with { SalesOrderId = "SO-OTHER" }, "SHIP-0001").Status == ShipmentStatus.Rejected);
var second = duplicateId.Create(pack with { SalesOrderId = "SO-OTHER" }, "SHIP-0002");
duplicateId.Dispatch(new DispatchConfirmation("DISP-SHARED", "SHIP-0001"));
Check("Dispatch ID cannot be reused", second.Status == ShipmentStatus.Prepared && duplicateId.Dispatch(new DispatchConfirmation("DISP-SHARED", "SHIP-0002")).Status == ShipmentStatus.Rejected);
Check("Dispatch before shipment rejected", new Shipping().Dispatch(new DispatchConfirmation("DISP-0001", "SHIP-0001")).Status == ShipmentStatus.Rejected);
Check("Order state unchanged", order.Status == "Received");
Check("Inventory unchanged", availability.Assess(order).Lines.Single(x => x.ProductId == "P000142").OnHand == 10);
Check("Pick and packing states unchanged", pick.Status == PickExecutionStatus.Completed && pack.Status == PackingStatus.ReadyForShipment);
Console.WriteLine($"RESULT: {total - failures}/{total} passed");
return failures == 0 ? 0 : 1;
