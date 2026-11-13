using System.Data;
using Frontier.Inventory.SqlServer;
using Frontier.Orders;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

var connectionString = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING is required; no SQL verification performed.");
    return 2;
}

var services = new ServiceCollection();
services.AddSqlInventoryAvailability(connectionString);
using var provider = services.BuildServiceProvider(validateScopes: true);
using var scope = provider.CreateScope();
var availability = scope.ServiceProvider.GetRequiredService<SqlInventoryAvailability>();

var count = 0;
var failed = 0;
void Check(string name, bool passed)
{
    count++;
    if (!passed) failed++;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
}

// The seeded snapshot is used read-only. No database records are created or deleted.
var order = new SalesOrder("SO-0001", "nopCommerce", "WEB-10001", "C0001", "LOC-01", "Received", new[]
{
    new SalesOrderLine("1", "P000142", 3),
    new SalesOrderLine("2", "P000143", 2)
});
var planner = new PickPlanning();

try
{
    Check("DI resolves ERP SQL inventory reader", availability is not null);
    var result = availability.Assess(order, "WH-01");
    Check("Seeded on-hand availability is Available", result.Status == AvailabilityStatus.Available);
    Check("Warehouse and sales order preserved", result.WarehouseId == "WH-01" && result.SalesOrderId == order.SalesOrderId);
    Check("SQL stock quantities read correctly", result.Lines.Count == 2 &&
        result.Lines[0].OnHand == 10 && result.Lines[1].OnHand == 5);
    Check("Requested quantities preserved", result.Lines[0].Requested == 3 && result.Lines[1].Requested == 2);
    Check("No shortages when sufficient", result.Lines.All(line => line.ShortBy == 0));
    Check("Voxware pick planning accepts Available result", planner.Create(order, result).Status == PickPlanStatus.Ready);

    var shortOrder = order with { Lines = new[] { new SalesOrderLine("1", "P000142", 11) } };
    var shortResult = availability.Assess(shortOrder, "WH-01");
    Check("SQL shortage assessed", shortResult.Status == AvailabilityStatus.Short &&
        shortResult.Lines.Single().ShortBy == 1);
    Check("Voxware pick planning blocks shortage", planner.Create(shortOrder, shortResult).Status == PickPlanStatus.BlockedShort);

    var missingProduct = "P-MISSING-" + Guid.NewGuid().ToString("N")[..12];
    var unknownOrder = order with { Lines = new[] { new SalesOrderLine("1", missingProduct, 2) } };
    var unknownResult = availability.Assess(unknownOrder, "WH-01");
    Check("Absent stock row remains Unknown", unknownResult.Status == AvailabilityStatus.Unknown &&
        unknownResult.Lines.Single().OnHand is null && unknownResult.Lines.Single().ShortBy is null);
    Check("Voxware pick planning blocks unknown stock", planner.Create(unknownOrder, unknownResult).Status == PickPlanStatus.BlockedUnknown);

    var aggregateOrder = order with { Lines = new[] {
        new SalesOrderLine("1", "P000142", 3), new SalesOrderLine("2", "p000142", 8) } };
    var aggregate = availability.Assess(aggregateOrder, "wh-01");
    Check("Repeated product and warehouse casing aggregated", aggregate.Status == AvailabilityStatus.Short &&
        aggregate.Lines.Count == 1 && aggregate.Lines[0].Requested == 11 && aggregate.Lines[0].ShortBy == 1);
    Check("Missing warehouse rejected", ThrowsArgument(() => availability.Assess(order, "WH-UNKNOWN")));
    Check("Null order rejected", ThrowsArgument(() => availability.Assess(null!, "WH-01")));
    Check("Invalid quantity rejected", ThrowsArgument(() => availability.Assess(order with {
        Lines = new[] { new SalesOrderLine("1", "P000142", 0) } }, "WH-01")));
    Check("Original order remains unchanged", order.Status == "Received" && order.Lines[0].Quantity == 3);
    var after = availability.Assess(order, "WH-01");
    Check("Read-only snapshot leaves SQL stock unchanged", after.Lines[0].OnHand == result.Lines[0].OnHand &&
        after.Lines[1].OnHand == result.Lines[1].OnHand);
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL SQL verification: " + error);
    return 2;
}

Console.WriteLine($"RESULT: {count - failed}/{count} passed");
return failed == 0 ? 0 : 1;

static bool ThrowsArgument(Action action)
{
    try { action(); return false; }
    catch (ArgumentException) { return true; }
}
