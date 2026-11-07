using System.Text.Json;
using Frontier.Identity;
using Frontier.Orders;

var dir = AppContext.BaseDirectory;
var catalog = IdentityCatalog.Load(Path.Combine(dir, "synthetic-identities.json"));
var incoming = JsonSerializer.Deserialize<IncomingOrder>(File.ReadAllText(Path.Combine(dir, "synthetic-order.json")))!;
var stock = JsonSerializer.Deserialize<StockQuantity[]>(File.ReadAllText(Path.Combine(dir, "synthetic-stock.json")))!;
var order = new OrderIntake(catalog, new ProductIdentityResolver(catalog)).Receive(incoming).Order!;
var inventory = new InventoryAvailability(catalog, "WH-01", stock);
var plan = new PickPlanning().Create(order, inventory.Assess(order));
var pickInput = new PickConfirmation("PICK-0001", order.SalesOrderId, "WH-01", new[] {
    new PickedLine("P000142", 3), new PickedLine("P000143", 2)
});
var pick = new PickExecution().Confirm(plan, pickInput);
var confirmation = new PackingConfirmation("PACK-0001", order.SalesOrderId, "WH-01", "PICK-0001", new[] {
    new PackedLine("P000142", 3), new PackedLine("P000143", 2)
});
var packing = new Packing();
var result = packing.Confirm(plan, pick, confirmation);
Console.WriteLine("STAGE 2: order -> availability -> plan -> pick -> pack -> shipment readiness (no dispatch)");
Console.WriteLine("PICK " + JsonSerializer.Serialize(pick));
Console.WriteLine("PACKING " + JsonSerializer.Serialize(result));
int total = 0, failed = 0;
void Check(string name, bool pass) { total++; if (!pass) failed++; Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}"); }
PackingConfirmation With(params PackedLine[] lines) => confirmation with { Lines = lines };
Check("Full packing ready for shipment", result.Status == PackingStatus.ReadyForShipment);
Check("Identifiers preserved", result.SalesOrderId == order.SalesOrderId && result.WarehouseId == "WH-01" && result.PackingId == "PACK-0001" && result.PickConfirmationId == "PICK-0001");
Check("Packed quantities retained", result.Lines.Single(x => x.ProductId == "P000142").Quantity == 3 && result.Lines.Single(x => x.ProductId == "P000143").Quantity == 2);
Check("Identical replay is idempotent", packing.Confirm(plan, pick, confirmation).Status == PackingStatus.AlreadyRecorded);
Check("Reordered replay is idempotent", packing.Confirm(plan, pick, With(new PackedLine("P000143", 2), new PackedLine("P000142", 3))).Status == PackingStatus.AlreadyRecorded);
Check("Changed replay rejected", packing.Confirm(plan, pick, With(new PackedLine("P000142", 2), new PackedLine("P000143", 2))).Status == PackingStatus.Rejected);
Check("Different packing ID rejected", packing.Confirm(plan, pick, confirmation with { PackingId = "PACK-0002" }).Status == PackingStatus.Rejected);
Check("Overpacking rejected", new Packing().Confirm(plan, pick, With(new PackedLine("P000142", 4), new PackedLine("P000143", 2))).Status == PackingStatus.Rejected);
Check("Negative packed quantity rejected", new Packing().Confirm(plan, pick, With(new PackedLine("P000142", -1), new PackedLine("P000143", 2))).Status == PackingStatus.Rejected);
Check("Duplicate product rejected", new Packing().Confirm(plan, pick, With(new PackedLine("P000142", 1), new PackedLine("P000142", 2))).Status == PackingStatus.Rejected);
Check("Missing line rejected", new Packing().Confirm(plan, pick, With(new PackedLine("P000142", 3))).Status == PackingStatus.Rejected);
Check("Unknown product rejected", new Packing().Confirm(plan, pick, With(new PackedLine("P000142", 3), new PackedLine("P999999", 2))).Status == PackingStatus.Rejected);
Check("Wrong order rejected", new Packing().Confirm(plan, pick, confirmation with { SalesOrderId = "SO-WRONG" }).Status == PackingStatus.Rejected);
Check("Wrong warehouse rejected", new Packing().Confirm(plan, pick, confirmation with { WarehouseId = "WH-WRONG" }).Status == PackingStatus.Rejected);
Check("Wrong pick confirmation rejected", new Packing().Confirm(plan, pick, confirmation with { PickConfirmationId = "PICK-WRONG" }).Status == PackingStatus.Rejected);
Check("Missing packing ID rejected", new Packing().Confirm(plan, pick, confirmation with { PackingId = "" }).Status == PackingStatus.Rejected);
Check("Blocked plan rejected", new Packing().Confirm(plan with { Status = PickPlanStatus.BlockedShort }, pick, confirmation).Status == PackingStatus.Rejected);
Check("Incomplete packing remains blocked", new Packing().Confirm(plan, pick, With(new PackedLine("P000142", 2), new PackedLine("P000143", 2))).Status == PackingStatus.Incomplete);
var partialPick = new PickExecution().Confirm(plan, pickInput with { ConfirmationId = "PICK-PARTIAL", Lines = new[] {
    new PickedLine("P000142", 1), new PickedLine("P000143", 2)
}});
Check("Partial pick cannot become shipment-ready", new Packing().Confirm(plan, partialPick,
    confirmation with { PickConfirmationId = "PICK-PARTIAL", Lines = new[] { new PackedLine("P000142", 1), new PackedLine("P000143", 2) } }).Status == PackingStatus.Incomplete);
Check("Packing more than partially picked rejected", new Packing().Confirm(plan, partialPick,
    confirmation with { PickConfirmationId = "PICK-PARTIAL" }).Status == PackingStatus.Rejected);
var retry = new Packing();
retry.Confirm(plan, pick, With(new PackedLine("P000142", 9), new PackedLine("P000143", 2)));
Check("Valid retry after rejection succeeds", retry.Confirm(plan, pick, confirmation).Status == PackingStatus.ReadyForShipment);
Check("Order state unchanged", order.Status == "Received");
Check("Inventory unchanged", inventory.Assess(order).Lines.Single(x => x.ProductId == "P000142").OnHand == 10);
Check("Pick state unchanged", pick.Status == PickExecutionStatus.Completed && pick.Lines.Single(x => x.ProductId == "P000142").Quantity == 3);
Console.WriteLine($"RESULT: {total - failed}/{total} passed");
return failed == 0 ? 0 : 1;
