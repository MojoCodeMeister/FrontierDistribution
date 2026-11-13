using Frontier.Contracts;
using Frontier.Identity.SqlServer;
using Frontier.Identity;
using Frontier.Domain;
using Frontier.Orders;
using Microsoft.Extensions.DependencyInjection;

var connectionString = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING is required; no SQL verification was performed.");
    return 2;
}

var services = new ServiceCollection();
services.AddSqlProductIdentityResolver(connectionString);
// Stage 2 composition root: retain the existing OrderIntake contract.
// Customer/location lookup remains catalog-backed; SKU resolution is SQL-backed.
services.AddSingleton(new IdentityCatalog
{
    Customers = new() { new Customer("C0001", "Synthetic pet retailer") },
    CustomerLocations = new() { new CustomerLocation("LOC-01", "C0001", "Retail store") }
});
services.AddScoped<OrderIntake>();
using var provider = services.BuildServiceProvider(validateScopes: true);
using var scope = provider.CreateScope();
var resolver = scope.ServiceProvider.GetRequiredService<IProductIdentityResolver>();
var checks = 0;
var failures = 0;
void Check(string name, bool passed)
{
    checks++;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
    if (!passed) failures++;
}
static bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
}

Check("DI resolves SQL implementation", resolver is SqlProductIdentityResolver);
Check("Null query rejected", Throws<ArgumentNullException>(() => resolver.Resolve(null!)));
Check("Blank system rejected", Throws<ArgumentException>(() => resolver.Resolve(new(" ", "ItemNumber", "ITEM-000142"))));
Check("Blank type rejected", Throws<ArgumentException>(() => resolver.Resolve(new("ERP", " ", "ITEM-000142"))));
Check("Blank identifier rejected", Throws<ArgumentException>(() => resolver.Resolve(new("ERP", "ItemNumber", " "))));

try
{
    var erp = resolver.Resolve(new("ERP", "ItemNumber", "ITEM-000142"));
    var web = resolver.Resolve(new("nopCommerce", "SKU", "FD-DOG-0142"));
    Check("ERP identity Found", erp.Status == ResolveStatus.Found && erp.Product?.ProductId == "P000142");
    Check("Webstore identity resolves same ERP product", web.Status == ResolveStatus.Found && web.Product?.ProductId == erp.Product?.ProductId);
    Check("Product has description and base unit", erp.Product is { Description.Length: > 0, BaseUnit.Length: > 0 });
    Check("Case-insensitive lookup", resolver.Resolve(new("erp", "itemnumber", "item-000142")).Product?.ProductId == "P000142");
    var missing = resolver.Resolve(new("ERP", "ItemNumber", "DOES-NOT-EXIST"));
    Check("Unknown identity NotFound", missing.Status == ResolveStatus.NotFound && missing.Product is null && missing.CandidateProductIds.Count == 0);
    var ambiguous = resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01"));
    Check("Omitted context Ambiguous", ambiguous.Status == ResolveStatus.Ambiguous && ambiguous.Product is null && ambiguous.CandidateProductIds.SequenceEqual(new[] { "P000142", "P000143" }));
    Check("Context SUP-1 selects product", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01", "SUP-1")).Product?.ProductId == "P000142");
    Check("Context SUP-2 selects product", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01", "SUP-2")).Product?.ProductId == "P000143");
    Check("Context comparison is case-insensitive", resolver.Resolve(new("supplier", "itemnumber", "shared-01", "sup-2")).Product?.ProductId == "P000143");
    Check("Wrong context returns NotFound", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01", "SUP-UNKNOWN")).Status == ResolveStatus.NotFound);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL SQL execution: {exception}");
    return 1;
}

// Stage 2 Order Intake through the same DI scope and SQL identity resolver.
try
{
    var intake = scope.ServiceProvider.GetRequiredService<OrderIntake>();
    var incoming = new IncomingOrder("nopCommerce", "WEB-SQL-10001", "C0001", "LOC-01", new[]
    {
        new IncomingOrderLine("1", "FD-DOG-0142", 3),
        new IncomingOrderLine("2", "FD-DOG-0143", 2)
    });
    var received = intake.Receive(incoming);
    Check("DI resolves Stage 2 OrderIntake", intake is not null);
    Check("SQL-backed intake creates order", received.Status == IntakeStatus.Created && received.Order?.Status == "Received");
    Check("SQL-backed intake resolves both SKUs", received.Order?.Lines.Count == 2 &&
        received.Order.Lines[0].ProductId == "P000142" && received.Order.Lines[1].ProductId == "P000143");
    Check("SQL-backed intake preserves quantities", received.Order?.Lines[0].Quantity == 3 && received.Order.Lines[1].Quantity == 2);
    Check("SQL-backed intake preserves customer and location", received.Order?.CustomerId == "C0001" &&
        received.Order.ShipToLocationId == "LOC-01");
    Check("SQL-backed intake replay is idempotent", intake.Receive(incoming).Status == IntakeStatus.AlreadyReceived);
    Check("SQL-backed intake rejects changed duplicate", intake.Receive(incoming with {
        Lines = new[] { new IncomingOrderLine("1", "FD-DOG-0142", 99) }
    }).Status == IntakeStatus.Rejected);
    Check("SQL-backed intake rejects unknown customer", intake.Receive(incoming with {
        ExternalOrderId = "WEB-SQL-10002", CustomerId = "UNKNOWN"
    }).Status == IntakeStatus.Rejected);
    Check("SQL-backed intake rejects unknown SKU", intake.Receive(incoming with {
        ExternalOrderId = "WEB-SQL-10003", Lines = new[] { new IncomingOrderLine("1", "MISSING", 1) }
    }).Status == IntakeStatus.Rejected);
    Check("SQL-backed intake preserves order IDs after rejection", intake.Receive(incoming with {
        ExternalOrderId = "WEB-SQL-10004"
    }).Order?.SalesOrderId == "SO-0002");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL Stage 2 SQL-backed intake execution: {exception}");
    return 1;
}

Console.WriteLine($"RESULT: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
