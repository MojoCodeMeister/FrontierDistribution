using System.Data;
using Frontier.Orders;
using Frontier.Picking.SqlServer;
using Microsoft.Data.SqlClient;

var connectionString = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("FRONTIER_SQL_CONNECTION_STRING required");
var suffix = Guid.NewGuid().ToString("N")[..12];
var warehouse = "Q25WH" + suffix;
var order = "Q25SO" + suffix;
var partialOrder = "Q25SP" + suffix;
var blockedOrder = "Q25SB" + suffix;
var store = new SqlPickConfirmationStore(connectionString);
var total = 0;
var failed = 0;
void Check(string name, bool ok)
{
    total++;
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
}
void Sql(string statement, string? orderId = null)
{
    using var connection = new SqlConnection(connectionString);
    connection.Open();
    using var command = new SqlCommand(statement, connection);
    command.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = orderId ?? order;
    command.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouse;
    command.ExecuteNonQuery();
}
int Count(string table, string orderId)
{
    using var connection = new SqlConnection(connectionString);
    connection.Open();
    using var cmd = new SqlCommand($"SELECT COUNT(*) FROM {table} WHERE SalesOrderId=@order AND WarehouseId=@warehouse", connection);
    cmd.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = orderId;
    cmd.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouse;
    return (int)cmd.ExecuteScalar()!;
}
try
{
    foreach (var id in new[] { order, partialOrder, blockedOrder })
    {
        var status = id == blockedOrder ? "BlockedShort" : "Ready";
        Sql($"INSERT INTO voxware.PickPlan (SalesOrderId, WarehouseId, Status) VALUES (@order,@warehouse,'{status}')", id);
        if (id != blockedOrder)
            Sql("""
                INSERT INTO voxware.PickPlanLine (SalesOrderId, WarehouseId, ProductId, Quantity)
                VALUES (@order,@warehouse,'P-Q25A',4),(@order,@warehouse,'P-Q25B',2)
                """, id);
    }
    PickConfirmation Make(string id, string confirmationId, int a, int b) =>
        new(confirmationId, id, warehouse, new[] { new PickedLine("P-Q25A", a), new PickedLine("P-Q25B", b) });
    Check("Unknown plan rejected", store.Confirm(Make("Q25missing" + suffix, "CONF-0", 4, 2)).Status == PickExecutionStatus.Rejected);
    Check("Blocked pick plan rejected", store.Confirm(Make(blockedOrder, "CONF-B", 4, 2)).Status == PickExecutionStatus.Rejected);
    Check("Overpick rejected", store.Confirm(Make(order, "CONF-1", 5, 2)).Status == PickExecutionStatus.Rejected);
    Check("Omitted line rejected", store.Confirm(new PickConfirmation("CONF-1", order, warehouse,
        new[] { new PickedLine("P-Q25A", 4) })).Status == PickExecutionStatus.Rejected);
    Check("Invalid attempt creates no row", Count("voxware.PickConfirmation", order) == 0);
    var full = store.Confirm(Make(order, "CONF-1", 4, 2));
    Check("Full pick confirmed", full.Status == PickExecutionStatus.Completed);
    Check("Full confirmation header persisted", Count("voxware.PickConfirmation", order) == 1);
    Check("Full confirmation lines persisted", Count("voxware.PickConfirmationLine", order) == 2);
    Check("Replay across instances idempotent", new SqlPickConfirmationStore(connectionString).Confirm(
        Make(order.ToLowerInvariant(), "conf-1", 4, 2)).Status == PickExecutionStatus.AlreadyRecorded);
    Check("Changed replay rejected", store.Confirm(Make(order, "CONF-2", 4, 2)).Status == PickExecutionStatus.Rejected);
    Check("Replayed full pick has one header", Count("voxware.PickConfirmation", order) == 1);
    var partial = store.Confirm(Make(partialOrder, "CONF-P", 1, 0));
    Check("Partial pick persisted", partial.Status == PickExecutionStatus.Partial);
    Check("Partial pick keeps zero quantity", partial.Lines.Single(x => x.ProductId == "P-Q25B").Quantity == 0);
    Check("Partial replay idempotent", new SqlPickConfirmationStore(connectionString).Confirm(
        new PickConfirmation("conf-p", partialOrder, warehouse,
            new[] { new PickedLine("p-q25b", 0), new PickedLine("p-q25a", 1) })).Status == PickExecutionStatus.AlreadyRecorded);
    Check("Partial pick has both persisted lines", Count("voxware.PickConfirmationLine", partialOrder) == 2);
    var competing = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        new SqlPickConfirmationStore(connectionString).Confirm(Make(blockedOrder, "CONF-C", 4, 2)))).ToArray();
    Task.WaitAll(competing);
    Check("Concurrent blocked confirmations rejected", competing.All(x => x.Result.Status == PickExecutionStatus.Rejected));
    Check("No confirmation for blocked plan", Count("voxware.PickConfirmation", blockedOrder) == 0);
}
finally
{
    foreach (var id in new[] { order, partialOrder, blockedOrder })
    {
        Sql("DELETE FROM voxware.PickConfirmationLine WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
        Sql("DELETE FROM voxware.PickConfirmation WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
        Sql("DELETE FROM voxware.PickPlanLine WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
        Sql("DELETE FROM voxware.PickPlan WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
    }
    Console.WriteLine("CLEANUP: Q25.2 isolated Voxware fixtures removed");
}
Console.WriteLine($"RESULT: {total - failed}/{total} checks passed");
return failed == 0 ? 0 : 1;
