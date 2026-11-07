using System.Text.Json;
using Frontier.Identity;
using Frontier.Orders;

var dir = AppContext.BaseDirectory;
var catalog = IdentityCatalog.Load(Path.Combine(dir, "synthetic-identities.json"));
var incoming = JsonSerializer.Deserialize<IncomingOrder>(File.ReadAllText(Path.Combine(dir, "synthetic-order.json")))!;
var stock = JsonSerializer.Deserialize<StockQuantity[]>(File.ReadAllText(Path.Combine(dir, "synthetic-stock.json")))!;
var intake = new OrderIntake(catalog, new ProductIdentityResolver(catalog));
var order = intake.Receive(incoming).Order ?? throw new InvalidOperationException("Order intake failed");
var availability = new InventoryAvailability(catalog, "WH-01", stock);
var assessment = availability.Assess(order);
var planner = new PickPlanning();
var pick = planner.Create(order, assessment);
Console.WriteLine("STAGE 2: received order -> inventory assessment -> proposed pick plan (no reservation)");
Console.WriteLine("ORDER " + JsonSerializer.Serialize(order));
Console.WriteLine("ASSESSMENT " + JsonSerializer.Serialize(assessment));
Console.WriteLine("PICK PLAN " + JsonSerializer.Serialize(pick));
int total = 0, failed = 0;
void Check(string title, bool pass) { total++; if (!pass) failed++; Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {title}"); }
void Rejects(string title, Action action)
{
    try { action(); Check(title, false); }
    catch (ArgumentException) { Check(title, true); }
}
Check("Ready pick plan generated", pick.Status == PickPlanStatus.Ready && pick.Lines.Count == 2);
Check("Order and warehouse retained", pick.SalesOrderId == "SO-0001" && pick.WarehouseId == "WH-01");
Check("Correct quantities", pick.Lines.Single(x => x.ProductId == "P000142").Quantity == 3 &&
    pick.Lines.Single(x => x.ProductId == "P000143").Quantity == 2);
var shortAssessment = new InventoryAvailability(catalog, "WH-01", new[] {
    new StockQuantity("WH-01", "P000142", 2), new StockQuantity("WH-01", "P000143", 5)
}).Assess(order);
var shortPick = planner.Create(order, shortAssessment);
Check("Shortage blocks picking", shortPick.Status == PickPlanStatus.BlockedShort && shortPick.Lines.Count == 0);
var unknownAssessment = new InventoryAvailability(catalog, "WH-01", new[] {
    new StockQuantity("WH-01", "P000142", 10)
}).Assess(order);
var unknownPick = planner.Create(order, unknownAssessment);
Check("Unknown inventory blocks picking", unknownPick.Status == PickPlanStatus.BlockedUnknown && unknownPick.Lines.Count == 0);
var repeated = order with { Lines = new[] { new SalesOrderLine("1", "P000142", 3), new SalesOrderLine("2", "P000142", 4) } };
var repeatPick = planner.Create(repeated, availability.Assess(repeated));
Check("Repeated product consolidated", repeatPick.Lines.Count == 1 && repeatPick.Lines[0].Quantity == 7);
Rejects("Other order assessment rejected", () => planner.Create(order, assessment with { SalesOrderId = "SO-OTHER" }));
Rejects("Incomplete assessment rejected", () => planner.Create(order, assessment with { Lines = assessment.Lines.Take(1).ToArray() }));
Rejects("Incorrect demand rejected", () => planner.Create(order, assessment with { Lines = new[] { assessment.Lines[0] with { Requested = 99 }, assessment.Lines[1] } }));
Rejects("Inconsistent availability status rejected", () => planner.Create(order, assessment with { Status = AvailabilityStatus.Short }));
Rejects("Duplicate assessment product rejected", () => planner.Create(order, assessment with { Lines = new[] { assessment.Lines[0], assessment.Lines[0] } }));
Check("Order remains received", order.Status == "Received");
Check("No stock consumed by planning", availability.Assess(order).Status == AvailabilityStatus.Available);
Console.WriteLine($"RESULT: {total - failed}/{total} passed");
return failed == 0 ? 0 : 1;
