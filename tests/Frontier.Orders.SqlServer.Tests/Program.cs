using System.Data;
using Frontier.Identity.SqlServer;
using Frontier.Orders;
using Frontier.Orders.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

var connectionString = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING is required; no SQL verification performed.");
    return 2;
}

var services = new ServiceCollection();
services.AddSqlProductIdentityResolver(connectionString);
services.AddSqlOrderIntake(connectionString);
using var provider = services.BuildServiceProvider(validateScopes: true);
var count = 0;
var failed = 0;
void Check(string name, bool passed)
{
    count++;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
    if (!passed) failed++;
}
SqlOrderIntake NewIntake()
{
    using var scope = provider.CreateScope();
    // Transient resolution for test creates a new store and resolver each time.
    return scope.ServiceProvider.GetRequiredService<SqlOrderIntake>();
}

var reference = "TEST-SQL-ORDER-" + Guid.NewGuid().ToString("N");
var order = new IncomingOrder("nopCommerce", reference, "C0001", "LOC-01", new[]
{
    new IncomingOrderLine("1", "FD-DOG-0142", 3),
    new IncomingOrderLine("2", "FD-DOG-0143", 2)
});
var createdId = (string?)null;
try
{
    var intake = NewIntake();
    Check("DI resolves SQL order intake", intake is SqlOrderIntake);
    Check("Null intake rejected", intake.Receive(null).Status == IntakeStatus.Rejected);
    var result = intake.Receive(order);
    createdId = result.Order?.SalesOrderId;
    Check("Durable intake creates ERP order", result.Status == IntakeStatus.Created && createdId?.StartsWith("SO-") == true && createdId != "SO-0001");
    Check("Resolved products and quantities retained", result.Order?.Lines.Count == 2 &&
        result.Order.Lines[0].ProductId == "P000142" && result.Order.Lines[0].Quantity == 3 &&
        result.Order.Lines[1].ProductId == "P000143" && result.Order.Lines[1].Quantity == 2);
    Check("Customer and ship-to preserved", result.Order is { CustomerId: "C0001", ShipToLocationId: "LOC-01" });

    using (var sql = new SqlConnection(connectionString))
    {
        sql.Open();
        using var cmd = new SqlCommand("""
            SELECT COUNT(*) FROM erp.SalesOrder AS h
            JOIN erp.SalesOrderLine AS l ON l.SalesOrderId = h.SalesOrderId
            WHERE h.SourceSystem = @source AND h.ExternalOrderId = @external;
            """, sql);
        cmd.Parameters.Add("@source", SqlDbType.VarChar, 80).Value = order.SourceSystem;
        cmd.Parameters.Add("@external", SqlDbType.VarChar, 100).Value = order.ExternalOrderId;
        Check("One persisted header and two lines", (int)cmd.ExecuteScalar()! == 2);
    }

    var replay = NewIntake().Receive(order);
    Check("Replay across DI scopes returns persisted order", replay.Status == IntakeStatus.AlreadyReceived &&
        replay.Order?.SalesOrderId == createdId);
    Check("Persisted replay returns resolved lines", replay.Order?.Lines.Count == 2 &&
        replay.Order.Lines[1].ProductId == "P000143");
    var casing = NewIntake().Receive(order with { SourceSystem = "NOPCOMMERCE", ExternalOrderId = reference.ToLowerInvariant() });
    Check("Case-insensitive source key replay", casing.Status == IntakeStatus.AlreadyReceived && casing.Order?.SalesOrderId == createdId);
    Check("Changed duplicate rejected", NewIntake().Receive(order with {
        Lines = new[] { new IncomingOrderLine("1", "FD-DOG-0142", 4), order.Lines[1] }
    }).Status == IntakeStatus.Rejected);
    Check("Unknown customer rejected", NewIntake().Receive(order with {
        ExternalOrderId = reference + "-BAD-CUST", CustomerId = "C-UNKNOWN"
    }).Status == IntakeStatus.Rejected);
    Check("Unknown SKU rejected", NewIntake().Receive(order with {
        ExternalOrderId = reference + "-BAD-SKU", Lines = new[] { new IncomingOrderLine("1", "NOT-A-SKU", 1) }
    }).Status == IntakeStatus.Rejected);
    Check("Wrong ship-to rejected", NewIntake().Receive(order with {
        ExternalOrderId = reference + "-BAD-LOC", ShipToLocationId = "LOC-UNKNOWN"
    }).Status == IntakeStatus.Rejected);
    Check("Duplicate source-line IDs rejected", NewIntake().Receive(order with {
        ExternalOrderId = reference + "-BAD-LINE", Lines = new[] { order.Lines[0], order.Lines[0] }
    }).Status == IntakeStatus.Rejected);

    using (var sql = new SqlConnection(connectionString))
    {
        sql.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM erp.SalesOrder WHERE ExternalOrderId LIKE @prefix;", sql);
        cmd.Parameters.Add("@prefix", SqlDbType.VarChar, 100).Value = reference + "%";
        Check("Rejections leave no additional orders", (int)cmd.ExecuteScalar()! == 1);
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL SQL execution: {exception}");
    return 1;
}
finally
{
    // Delete ONLY the unique test row created by this run, leaving seeded and other records untouched.
    if (createdId is not null)
    {
        using var sql = new SqlConnection(connectionString);
        sql.Open();
        using var tx = sql.BeginTransaction();
        foreach (var statement in new[]
        {
            "DELETE FROM erp.SalesOrderLine WHERE SalesOrderId = @id;",
            "DELETE FROM erp.SalesOrder WHERE SalesOrderId = @id AND ExternalOrderId = @external;"
        })
        {
            using var cmd = new SqlCommand(statement, sql, tx);
            cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = createdId;
            cmd.Parameters.Add("@external", SqlDbType.VarChar, 100).Value = reference;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
Console.WriteLine($"RESULT: {count - failed}/{count} passed");
return failed == 0 ? 0 : 1;
