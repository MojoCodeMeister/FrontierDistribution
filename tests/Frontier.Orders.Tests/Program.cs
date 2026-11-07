using System.Text.Json;
using Frontier.Identity;
using Frontier.Orders;

var dir = AppContext.BaseDirectory;
var catalog = IdentityCatalog.Load(Path.Combine(dir, "synthetic-identities.json"));
var resolver = new ProductIdentityResolver(catalog);
var intake = new OrderIntake(catalog, resolver);
var incoming = JsonSerializer.Deserialize<IncomingOrder>(File.ReadAllText(Path.Combine(dir, "synthetic-order.json")))!;
var total = 0;
var failed = 0;
void Check(string title, bool success)
{
    total++;
    if (!success) failed++;
    Console.WriteLine($"{(success ? "PASS" : "FAIL")} {title}");
}

Console.WriteLine("STAGE 2: synthetic webstore order -> identity resolution -> internal order");
Console.WriteLine("INCOMING " + JsonSerializer.Serialize(incoming));
var first = intake.Receive(incoming);
Console.WriteLine("RESULT   " + JsonSerializer.Serialize(first));
Check("Order created", first.Status == IntakeStatus.Created && first.Order?.Status == "Received");
Check("Both source lines resolved", first.Order?.Lines.Count == 2 &&
    first.Order.Lines[0].ProductId == "P000142" && first.Order.Lines[1].ProductId == "P000143");
Check("Location/customer preserved", first.Order?.CustomerId == "C0001" && first.Order.ShipToLocationId == "LOC-01");
var again = intake.Receive(incoming);
Check("Exact duplicate returns existing order", again.Status == IntakeStatus.AlreadyReceived &&
    again.Order?.SalesOrderId == first.Order?.SalesOrderId);
Check("Changed duplicate rejected", intake.Receive(incoming with {
    Lines = new[] { new IncomingOrderLine("1", "FD-DOG-0142", 99) }
}).Status == IntakeStatus.Rejected);
Check("Unknown customer rejected", intake.Receive(incoming with {
    ExternalOrderId = "WEB-10002", CustomerId = "UNKNOWN"
}).Status == IntakeStatus.Rejected);
Check("Unknown SKU rejected", intake.Receive(incoming with {
    ExternalOrderId = "WEB-10003", Lines = new[] { new IncomingOrderLine("1", "MISSING", 1) }
}).Status == IntakeStatus.Rejected);
Check("Nonpositive quantity rejected", intake.Receive(incoming with {
    ExternalOrderId = "WEB-10004", Lines = new[] { new IncomingOrderLine("1", "FD-DOG-0142", 0) }
}).Status == IntakeStatus.Rejected);
Check("Missing ship-to rejected", intake.Receive(incoming with {
    ExternalOrderId = "WEB-10005", ShipToLocationId = "BAD"
}).Status == IntakeStatus.Rejected);
Check("Duplicate line IDs rejected", intake.Receive(incoming with {
    ExternalOrderId = "WEB-10006", Lines = new[] {
        new IncomingOrderLine("1", "FD-DOG-0142", 1), new IncomingOrderLine("1", "FD-DOG-0143", 1) }
}).Status == IntakeStatus.Rejected);
Check("Rejected submissions do not consume order IDs", intake.Receive(incoming with {
    ExternalOrderId = "WEB-10007"
}).Order?.SalesOrderId == "SO-0002");
Console.WriteLine($"RESULT: {total - failed}/{total} passed");
return failed == 0 ? 0 : 1;
