using System.Text.Json;
using Frontier.Identity;
using Frontier.Orders;

var baseDir = AppContext.BaseDirectory;
var catalog = IdentityCatalog.Load(Path.Combine(baseDir, "synthetic-identities.json"));
var incoming = JsonSerializer.Deserialize<IncomingOrder>(File.ReadAllText(Path.Combine(baseDir, "synthetic-order.json")))!;
var stock = JsonSerializer.Deserialize<StockQuantity[]>(File.ReadAllText(Path.Combine(baseDir, "synthetic-stock.json")))!;
var intake = new OrderIntake(catalog, new ProductIdentityResolver(catalog));
var order = intake.Receive(incoming).Order ?? throw new InvalidOperationException("Cannot create synthetic order");
var availability = new InventoryAvailability(catalog, "WH-01", stock);
var report = availability.Assess(order);
Console.WriteLine("STAGE 2: received order -> synthetic warehouse stock assessment (no reservation)");
Console.WriteLine("ORDER        " + JsonSerializer.Serialize(order));
Console.WriteLine("AVAILABILITY " + JsonSerializer.Serialize(report));
int total = 0, failed = 0;
void Check(string name, bool pass)
{
    total++;
    if (!pass) failed++;
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}");
}
Check("All lines available", report.Status == AvailabilityStatus.Available && report.Lines.Count == 2);
Check("Warehouse recorded", report.WarehouseId == "WH-01" && report.SalesOrderId == order.SalesOrderId);
Check("No mutation to order status", order.Status == "Received");
var shortReport = new InventoryAvailability(catalog, "WH-01", new[] {
    new StockQuantity("WH-01", "P000142", 2), new StockQuantity("WH-01", "P000143", 5)
}).Assess(order);
Check("Shortage reported", shortReport.Status == AvailabilityStatus.Short &&
    shortReport.Lines.Single(x => x.ProductId == "P000142").ShortBy == 1);
var unknownReport = new InventoryAvailability(catalog, "WH-01", new[] {
    new StockQuantity("WH-01", "P000142", 10)
}).Assess(order);
Check("Missing stock is unknown, not zero", unknownReport.Status == AvailabilityStatus.Unknown &&
    unknownReport.Lines.Single(x => x.ProductId == "P000143").OnHand is null);
var repeated = order with { Lines = new[] { new SalesOrderLine("1", "P000142", 3),
    new SalesOrderLine("2", "P000142", 8) } };
var aggregated = availability.Assess(repeated);
Check("Demand aggregated across lines", aggregated.Status == AvailabilityStatus.Short &&
    aggregated.Lines.Count == 1 && aggregated.Lines[0].Requested == 11 && aggregated.Lines[0].ShortBy == 1);
try { _ = new InventoryAvailability(catalog, "BAD", stock); Check("Unknown warehouse rejected", false); }
catch (ArgumentException) { Check("Unknown warehouse rejected", true); }
try { _ = new InventoryAvailability(catalog, "WH-01", new[] {new StockQuantity("WH-01", "P000142", -1)});
    Check("Negative stock rejected", false); }
catch (InvalidDataException) { Check("Negative stock rejected", true); }
try { _ = new InventoryAvailability(catalog, "WH-01", new[] {
    new StockQuantity("WH-01", "P000142", 1), new StockQuantity("WH-01", "P000142", 2)});
    Check("Duplicate snapshot rejected", false); }
catch (InvalidDataException) { Check("Duplicate snapshot rejected", true); }
Check("Assessment does not consume stock", availability.Assess(order).Status == AvailabilityStatus.Available);
Console.WriteLine($"RESULT: {total - failed}/{total} passed");
return failed == 0 ? 0 : 1;
