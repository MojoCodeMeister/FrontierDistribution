using System.Data;
using Frontier.Identity.SqlServer;
using Frontier.Inventory.SqlServer;
using Frontier.Orders;
using Frontier.Orders.SqlServer;
using Frontier.Picking.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

// Cross-application demonstration composition root; no change to ERP or Voxware components.
var requested = args.Select(x => x.ToLowerInvariant()).ToArray();
if (requested.Any(x => x is not ("--keep" or "--short" or "--help")))
{
    Console.Error.WriteLine("Usage: dotnet run --project demos/Frontier.OrderFlow.Demo -- [--short] [--keep]");
    return 2;
}
if (requested.Contains("--help"))
{
    Console.WriteLine("--short: demo inventory shortage / blocked pick plan; --keep: retain demo SQL rows for inspection");
    return 0;
}
var keep = requested.Contains("--keep");
var shortage = requested.Contains("--short");
var connectionString = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("FRONTIER_SQL_CONNECTION_STRING is required.");
    return 2;
}

var services = new ServiceCollection();
services.AddSqlProductIdentityResolver(connectionString);
services.AddSqlOrderIntake(connectionString);
services.AddSqlInventoryAvailability(connectionString);
services.AddSqlPickPlanStore(connectionString);
using var provider = services.BuildServiceProvider(validateScopes: true);

// A fresh, private source reference avoids clashing with the SQL seed or another demo run.
var sourceReference = "DEMO-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
var incoming = new IncomingOrder("nopCommerce", sourceReference, "C0001", "LOC-01", new[]
{
    new IncomingOrderLine("1", "FD-DOG-0142", shortage ? 999 : 3),
    new IncomingOrderLine("2", "FD-DOG-0143", 2)
});
string? createdId = null;
var createdByThisRun = false;
var success = false;
var cleaned = false;

void PrintStep(string owner, string description) => Console.WriteLine($"\n[{owner}] {description}");
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("  PASS  " + message);
}

Console.WriteLine("FRONTIER | ORDER-TO-WAREHOUSE DEMONSTRATION");
Console.WriteLine("Scenario: " + (shortage ? "Insufficient stock" : "Available stock"));
Console.WriteLine("Source: nopCommerce   Reference: " + sourceReference);
Console.WriteLine("Mode: " + (keep ? "KEEP demo records" : "CLEAN UP demo records"));

try
{
    using var scope = provider.CreateScope();
    var intake = scope.ServiceProvider.GetRequiredService<SqlOrderIntake>();
    var inventory = scope.ServiceProvider.GetRequiredService<SqlInventoryAvailability>();
    var store = scope.ServiceProvider.GetRequiredService<SqlPickPlanStore>();
    var planner = new PickPlanning();

    PrintStep("ERP", "Accept customer order; resolve nopCommerce SKUs to ERP products");
    var admitted = intake.Receive(incoming);
    createdId = admitted.Order?.SalesOrderId;
    createdByThisRun = admitted.Status == IntakeStatus.Created && createdId is not null;
    Require(createdByThisRun, "New ERP sales order persisted");
    var order = admitted.Order!;
    Console.WriteLine($"  ERP order: {order.SalesOrderId} | Customer: {order.CustomerId} | Ship-to: {order.ShipToLocationId}");
    foreach (var (submitted, resolved) in incoming.Lines.Zip(order.Lines))
        Console.WriteLine($"  {submitted.Sku} -> {resolved.ProductId}, Qty {resolved.Quantity}");
    Require(order.Lines.Count == 2 && order.Lines[0].ProductId == "P000142" &&
        order.Lines[1].ProductId == "P000143", "Both product identities resolved");

    PrintStep("ERP", "Read SQL inventory and assess order demand");
    var availability = inventory.Assess(order, "WH-01");
    Console.WriteLine($"  Warehouse: {availability.WarehouseId} | Availability: {availability.Status}");
    foreach (var line in availability.Lines)
        Console.WriteLine($"  {line.ProductId}: Requested={line.Requested}; OnHand={line.OnHand?.ToString() ?? "Unknown"}; ShortBy={line.ShortBy?.ToString() ?? "Unknown"}");
    Require(availability.Status == (shortage ? AvailabilityStatus.Short : AvailabilityStatus.Available),
        "Availability matches requested scenario");

    PrintStep("Voxware", "Create a pick plan using existing planning logic");
    var plan = planner.Create(order, availability);
    Console.WriteLine($"  Plan status: {plan.Status} | Lines: {plan.Lines.Count}");
    Require(plan.Status == (shortage ? PickPlanStatus.BlockedShort : PickPlanStatus.Ready),
        shortage ? "Shortage correctly blocks warehouse picking" : "Order is ready for warehouse picking");

    PrintStep("Voxware", "Persist pick plan to SQL Server");
    var persisted = store.Save(plan);
    Require(persisted.Status == PickPlanStoreStatus.Created, "Voxware pick-plan snapshot persisted");

    PrintStep("SQL Server", "Verify records in their owning application schemas");
    using (var sql = new SqlConnection(connectionString))
    {
        sql.Open();
        using var command = new SqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM erp.SalesOrder WHERE SalesOrderId = @id AND ExternalOrderId = @external),
                (SELECT COUNT(*) FROM erp.SalesOrderLine WHERE SalesOrderId = @id),
                (SELECT COUNT(*) FROM voxware.PickPlan WHERE SalesOrderId = @id AND WarehouseId = @warehouse),
                (SELECT COUNT(*) FROM voxware.PickPlanLine WHERE SalesOrderId = @id AND WarehouseId = @warehouse);
            """, sql);
        command.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = order.SalesOrderId;
        command.Parameters.Add("@external", SqlDbType.VarChar, 100).Value = sourceReference;
        command.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = "WH-01";
        using var reader = command.ExecuteReader();
        Require(reader.Read(), "SQL verification query returned");
        var headers = reader.GetInt32(0);
        var orderLines = reader.GetInt32(1);
        var plans = reader.GetInt32(2);
        var pickLines = reader.GetInt32(3);
        Console.WriteLine($"  ERP orders: {headers}, ERP lines: {orderLines}; Voxware plans: {plans}, Voxware lines: {pickLines}");
        Require(headers == 1 && orderLines == 2 && plans == 1 && pickLines == (shortage ? 0 : 2),
            "ERP and Voxware SQL rows match the business transaction");
    }

    PrintStep("ERP + Voxware", "Replay from fresh DI scope (database-backed idempotency)");
    using (var newScope = provider.CreateScope())
    {
        var again = newScope.ServiceProvider.GetRequiredService<SqlOrderIntake>().Receive(incoming);
        Require(again.Status == IntakeStatus.AlreadyReceived && again.Order?.SalesOrderId == order.SalesOrderId,
            "Order replay returns same ERP ID");
        var planAgain = newScope.ServiceProvider.GetRequiredService<SqlPickPlanStore>().Save(plan);
        Require(planAgain.Status == PickPlanStoreStatus.AlreadyRecorded,
            "Pick-plan replay does not duplicate warehouse work");
    }

    success = true;
    Console.WriteLine("\nRESULT: PASS | " + (shortage ? "Order retained; warehouse picking blocked" : "Order ready for warehouse picking"));
    Console.WriteLine("Order: " + order.SalesOrderId + " | Source reference: " + sourceReference);
}
catch (Exception error)
{
    Console.Error.WriteLine("\nRESULT: FAIL | " + error.Message);
}
finally
{
    if (!keep && createdByThisRun && createdId is not null)
    {
        try
        {
            // Remove only rows created for this exact source reference and run.
            using var sql = new SqlConnection(connectionString);
            sql.Open();
            using var transaction = sql.BeginTransaction();
            using var cmd = new SqlCommand("""
                IF NOT EXISTS (SELECT 1 FROM erp.SalesOrder
                    WHERE SalesOrderId = @id AND ExternalOrderId = @external AND SourceSystem = 'nopCommerce')
                    THROW 50010, 'Demo order identity mismatch: cleanup cancelled', 1;
                DELETE FROM voxware.PickPlanLine WHERE SalesOrderId = @id AND WarehouseId = @warehouse;
                DELETE FROM voxware.PickPlan WHERE SalesOrderId = @id AND WarehouseId = @warehouse;
                DELETE FROM erp.SalesOrderLine WHERE SalesOrderId = @id;
                DELETE FROM erp.SalesOrder WHERE SalesOrderId = @id AND ExternalOrderId = @external;
                """, sql, transaction);
            cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = createdId;
            cmd.Parameters.Add("@external", SqlDbType.VarChar, 100).Value = sourceReference;
            cmd.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = "WH-01";
            cmd.ExecuteNonQuery();
            transaction.Commit();
            cleaned = true;
            Console.WriteLine("CLEANUP: Demo ERP and Voxware records removed; seeded records untouched.");
        }
        catch (Exception cleanupError)
        {
            Console.Error.WriteLine("CLEANUP FAILED: " + cleanupError.Message);
            Console.Error.WriteLine($"Manual inspection required: ERP order {createdId}, external reference {sourceReference}");
            success = false;
        }
    }
    else if (keep && createdByThisRun)
        Console.WriteLine("RETAINED: Demo records remain in SQL Server for inspection.");
    else if (!createdByThisRun)
        Console.WriteLine("CLEANUP: No order was created by this run.");
}
return success && (keep || cleaned) ? 0 : 1;
