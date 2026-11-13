using System.Data;
using Frontier.Orders;
using Frontier.Inventory.SqlServer;
using Frontier.Picking.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

var connectionString = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING required; no SQL verification performed.");
    return 2;
}
var services = new ServiceCollection();
services.AddSqlPickPlanStore(connectionString);
services.AddSqlInventoryAvailability(connectionString);
using var provider = services.BuildServiceProvider(validateScopes: true);
var count = 0;
var failed = 0;
void Check(string name, bool passed)
{
    count++;
    if (!passed) failed++;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
}
var id = "SO-TEST-" + Guid.NewGuid().ToString("N")[..12];
var shortId = "SO-TEST-" + Guid.NewGuid().ToString("N")[..12];
var unknownId = "SO-TEST-" + Guid.NewGuid().ToString("N")[..12];
var cleanupIds = new[] { id, shortId, unknownId };
try
{
    using var scope = provider.CreateScope();
    var store = scope.ServiceProvider.GetRequiredService<SqlPickPlanStore>();
    Check("DI resolves Voxware SQL pick-plan store", store is not null);
    Check("Null plan rejected", store.Save(null).Status == PickPlanStoreStatus.Rejected);
    var order = new SalesOrder(id, "nopCommerce", "WEB-TEST", "C0001", "LOC-01", "Received",
        new[] { new SalesOrderLine("A", "P000142", 2), new SalesOrderLine("B", "P000143", 1) });
    var assessment = scope.ServiceProvider.GetRequiredService<SqlInventoryAvailability>().Assess(order, "WH-01");
    Check("ERP SQL inventory supports Ready planning", assessment.Status == AvailabilityStatus.Available);
    var plan = new PickPlanning().Create(order, assessment);
    Check("Existing planner produces Ready plan", plan.Status == PickPlanStatus.Ready && plan.Lines.Count == 2);
    var created = store.Save(plan);
    Check("Ready plan persisted", created.Status == PickPlanStoreStatus.Created);
    Check("Persisted plan preserves identifiers", created.Plan?.SalesOrderId == id && created.Plan.WarehouseId == "WH-01");
    using (var sql = new SqlConnection(connectionString))
    {
        sql.Open();
        using var cmd = new SqlCommand("""
            SELECT (SELECT COUNT(*) FROM voxware.PickPlan WHERE SalesOrderId = @id),
                   (SELECT COUNT(*) FROM voxware.PickPlanLine WHERE SalesOrderId = @id);
            """, sql);
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = id;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        Check("Exactly one stored header", reader.GetInt32(0) == 1);
        Check("Exactly two stored lines", reader.GetInt32(1) == 2);
    }
    using (var replayScope = provider.CreateScope())
    {
        var replayStore = replayScope.ServiceProvider.GetRequiredService<SqlPickPlanStore>();
        var replay = replayStore.Save(plan);
        Check("Replay across DI scopes is idempotent", replay.Status == PickPlanStoreStatus.AlreadyRecorded);
        Check("Replay recovers persisted quantities", replay.Plan?.Lines.Count == 2 &&
            replay.Plan.Lines.Any(l => l.ProductId == "P000142" && l.Quantity == 2));
        Check("Case-insensitive key and products replay", replayStore.Save(plan with {
            SalesOrderId = id.ToLowerInvariant(), WarehouseId = "wh-01",
            Lines = new[] { new PickLine("p000142", 2), new PickLine("p000143", 1) }
        }).Status == PickPlanStoreStatus.AlreadyRecorded);
        Check("Changed plan rejected", replayStore.Save(plan with {
            Lines = new[] { new PickLine("P000142", 3), new PickLine("P000143", 1) }
        }).Status == PickPlanStoreStatus.Rejected);
        Check("Blocked status cannot replace Ready", replayStore.Save(plan with {
            Status = PickPlanStatus.BlockedShort, Lines = Array.Empty<PickLine>()
        }).Status == PickPlanStoreStatus.Rejected);
    }
    var blockedShort = new PickPlan(shortId, "WH-01", PickPlanStatus.BlockedShort,
        Array.Empty<PickLine>(), "Insufficient stock; no pick list generated");
    var blockedUnknown = new PickPlan(unknownId, "WH-01", PickPlanStatus.BlockedUnknown,
        Array.Empty<PickLine>(), "Inventory not known for every product; no pick list generated");
    Check("BlockedShort stored without lines", store.Save(blockedShort).Status == PickPlanStoreStatus.Created);
    Check("BlockedUnknown stored without lines", store.Save(blockedUnknown).Status == PickPlanStoreStatus.Created);
    Check("Blocked plan replay preserved", store.Save(blockedUnknown).Status == PickPlanStoreStatus.AlreadyRecorded);
    Check("Ready plan with no lines rejected", store.Save(plan with {
        SalesOrderId = "SO-INVALID", Lines = Array.Empty<PickLine>()
    }).Status == PickPlanStoreStatus.Rejected);
    Check("Duplicate product rejected", store.Save(plan with { SalesOrderId = "SO-INVALID",
        Lines = new[] { new PickLine("P000142", 1), new PickLine("p000142", 2) }
    }).Status == PickPlanStoreStatus.Rejected);
    Check("Invalid quantity rejected", store.Save(plan with { SalesOrderId = "SO-INVALID",
        Lines = new[] { new PickLine("P000142", 0) }
    }).Status == PickPlanStoreStatus.Rejected);
    Check("Existing planner output unchanged", plan.Status == PickPlanStatus.Ready && plan.Lines.Count == 2);
    using (var sql = new SqlConnection(connectionString))
    {
        sql.Open();
        using var cmd = new SqlCommand("""
            SELECT COUNT(*) FROM voxware.PickPlan WHERE SalesOrderId = @id
            """, sql);
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = "SO-0001";
        Check("Seeded Voxware plan unaffected", (int)cmd.ExecuteScalar()! == 1);
    }
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL SQL verification: " + error);
    return 2;
}
finally
{
    // Clean only IDs allocated by this runner; do not modify the seeded Voxware plan.
    try
    {
        using var sql = new SqlConnection(connectionString);
        sql.Open();
        foreach (var cleanupId in cleanupIds)
        {
            using var transaction = sql.BeginTransaction();
            using (var detail = new SqlCommand("DELETE FROM voxware.PickPlanLine WHERE SalesOrderId = @id", sql, transaction))
            {
                detail.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = cleanupId;
                detail.ExecuteNonQuery();
            }
            using (var header = new SqlCommand("DELETE FROM voxware.PickPlan WHERE SalesOrderId = @id", sql, transaction))
            {
                header.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = cleanupId;
                header.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("FAIL cleanup: " + error);
        failed++;
    }
}
Console.WriteLine($"RESULT: {count - failed}/{count} passed");
return failed == 0 ? 0 : 1;
