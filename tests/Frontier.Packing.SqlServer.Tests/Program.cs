using System.Data;
using Frontier.Orders;
using Frontier.Picking.SqlServer;
using Microsoft.Data.SqlClient;

var connectionString = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("FRONTIER_SQL_CONNECTION_STRING required");
var suffix = Guid.NewGuid().ToString("N")[..12];
var warehouse = "Q253WH" + suffix;
var order = "Q253SO" + suffix;
var partialOrder = "Q253SP" + suffix;
var blockedOrder = "Q253SB" + suffix;
var unpickedOrder = "Q253SU" + suffix;
var store = new SqlPackingConfirmationStore(connectionString);
int total = 0, failed = 0;
void Check(string name, bool ok)
{
    total++;
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
}
void Sql(string statement, string id)
{
    using var connection = new SqlConnection(connectionString);
    connection.Open();
    using var command = new SqlCommand(statement, connection);
    command.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = id;
    command.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouse;
    command.ExecuteNonQuery();
}
int Count(string table, string id)
{
    using var connection = new SqlConnection(connectionString);
    connection.Open();
    using var command = new SqlCommand($"SELECT COUNT(*) FROM {table} WHERE SalesOrderId=@order AND WarehouseId=@warehouse", connection);
    command.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = id;
    command.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouse;
    return (int)command.ExecuteScalar()!;
}
PackingConfirmation Make(string id, string packingId, string pickId, int a, int b) =>
    new(packingId, id, warehouse, pickId, new[] { new PackedLine("P-Q253A", a), new PackedLine("P-Q253B", b) });
var ids = new[] { order, partialOrder, blockedOrder, unpickedOrder };
try
{
    foreach (var id in ids)
    {
        var status = id == blockedOrder ? "BlockedShort" : "Ready";
        Sql($"INSERT INTO voxware.PickPlan (SalesOrderId, WarehouseId, Status) VALUES (@order,@warehouse,'{status}')", id);
        if (id != blockedOrder)
            Sql("""
                INSERT INTO voxware.PickPlanLine (SalesOrderId, WarehouseId, ProductId, Quantity)
                VALUES (@order,@warehouse,'P-Q253A',4),(@order,@warehouse,'P-Q253B',2);
                """, id);
    }
    var picks = new SqlPickConfirmationStore(connectionString);
    Check("Seed full pick", picks.Confirm(new PickConfirmation("PICK-F", order, warehouse,
        new[] { new PickedLine("P-Q253A", 4), new PickedLine("P-Q253B", 2) })).Status == PickExecutionStatus.Completed);
    Check("Seed partial pick", picks.Confirm(new PickConfirmation("PICK-P", partialOrder, warehouse,
        new[] { new PickedLine("P-Q253A", 1), new PickedLine("P-Q253B", 0) })).Status == PickExecutionStatus.Partial);

    Check("Unknown plan rejected", store.Confirm(Make("MISSING" + suffix, "PACK-M", "PICK-M", 4, 2)).Status == PackingStatus.Rejected);
    Check("Blocked plan rejected", store.Confirm(Make(blockedOrder, "PACK-B", "PICK-B", 4, 2)).Status == PackingStatus.Rejected);
    Check("Unconfirmed pick rejected", store.Confirm(Make(unpickedOrder, "PACK-U", "PICK-U", 4, 2)).Status == PackingStatus.Rejected);
    Check("Wrong pick reference rejected", store.Confirm(Make(order, "PACK-F", "WRONG", 4, 2)).Status == PackingStatus.Rejected);
    Check("Overpack rejected", store.Confirm(Make(order, "PACK-F", "PICK-F", 5, 2)).Status == PackingStatus.Rejected);
    Check("Missing line rejected", store.Confirm(new PackingConfirmation("PACK-F", order, warehouse, "PICK-F",
        new[] { new PackedLine("P-Q253A", 4) })).Status == PackingStatus.Rejected);
    Check("Invalid pack creates no header", Count("voxware.PackingConfirmation", order) == 0);

    var full = store.Confirm(Make(order, "PACK-F", "PICK-F", 4, 2));
    Check("Full pack ready for shipment", full.Status == PackingStatus.ReadyForShipment);
    Check("Full packing identifiers preserved", full.PackingId == "PACK-F" && full.PickConfirmationId == "PICK-F");
    Check("Full packing header persisted", Count("voxware.PackingConfirmation", order) == 1);
    Check("Full packing lines persisted", Count("voxware.PackingConfirmationLine", order) == 2);
    Check("Cross-instance replay idempotent", new SqlPackingConfirmationStore(connectionString).Confirm(
        new PackingConfirmation("pack-f", order.ToLowerInvariant(), warehouse.ToLowerInvariant(), "pick-f",
            new[] { new PackedLine("p-q253b", 2), new PackedLine("p-q253a", 4) })).Status == PackingStatus.AlreadyRecorded);
    Check("Changed replay rejected", store.Confirm(Make(order, "PACK-F", "PICK-F", 3, 2)).Status == PackingStatus.Rejected);
    Check("Different packing ID rejected", store.Confirm(Make(order, "PACK-OTHER", "PICK-F", 4, 2)).Status == PackingStatus.Rejected);
    Check("Replay does not duplicate header", Count("voxware.PackingConfirmation", order) == 1);

    Check("Packing above partial picked quantity rejected", store.Confirm(
        Make(partialOrder, "PACK-P", "PICK-P", 2, 0)).Status == PackingStatus.Rejected);
    Check("Invalid partial attempt creates no row", Count("voxware.PackingConfirmation", partialOrder) == 0);
    var partial = store.Confirm(Make(partialOrder, "PACK-P", "PICK-P", 1, 0));
    Check("Partial pick cannot become shipment ready", partial.Status == PackingStatus.Incomplete);
    Check("Partial line zero quantity retained", partial.Lines.Single(x => x.ProductId == "P-Q253B").Quantity == 0);
    Check("Partial packing header persisted", Count("voxware.PackingConfirmation", partialOrder) == 1);
    Check("Partial packing lines persisted", Count("voxware.PackingConfirmationLine", partialOrder) == 2);
    Check("Partial replay idempotent", new SqlPackingConfirmationStore(connectionString).Confirm(
        Make(partialOrder, "PACK-P", "PICK-P", 1, 0)).Status == PackingStatus.AlreadyRecorded);

    var competing = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        new SqlPackingConfirmationStore(connectionString).Confirm(Make(unpickedOrder, "PACK-C", "PICK-C", 4, 2)))).ToArray();
    Task.WaitAll(competing);
    Check("Concurrent invalid confirmations rejected", competing.All(x => x.Result.Status == PackingStatus.Rejected));
    Check("No packing for unconfirmed pick", Count("voxware.PackingConfirmation", unpickedOrder) == 0);
}
finally
{
    foreach (var id in ids)
    {
        Sql("DELETE FROM voxware.PackingConfirmationLine WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
        Sql("DELETE FROM voxware.PackingConfirmation WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
        Sql("DELETE FROM voxware.PickConfirmationLine WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
        Sql("DELETE FROM voxware.PickConfirmation WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
        Sql("DELETE FROM voxware.PickPlanLine WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
        Sql("DELETE FROM voxware.PickPlan WHERE SalesOrderId=@order AND WarehouseId=@warehouse", id);
    }
    Console.WriteLine("CLEANUP: Q25.3 isolated Voxware fixtures removed");
}
Console.WriteLine($"RESULT: {total - failed}/{total} checks passed");
return failed == 0 ? 0 : 1;
