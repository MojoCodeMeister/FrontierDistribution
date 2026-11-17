using System.Data;
using System.Net.Http.Json;
using System.Text.Json;
using Frontier.Orders.SqlServer;
using Microsoft.Data.SqlClient;

// Q25.9: HTTP /status verification after an actual local ERP departure.
// Test owns an isolated warehouse and inventory; no Route4Me call.
var cs = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(cs)) { Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING required"); return 2; }
var baseUrl = (args.Length > 0 ? args[0] : "http://127.0.0.1:5187").TrimEnd('/');
using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
var warehouse = "Q259W" + suffix;
var departure = "Q259D" + suffix;
var request = "Q259R" + suffix;
var products = new[] { "P000142", "P000143" };
string? sessionId = null, orderId = null, external = null;
var checks = 0; var failures = 0; var warehouseCreated = false;
void Check(string label, bool ok)
{
    checks++;
    if (!ok) { failures++; throw new InvalidOperationException("FAIL: " + label); }
    Console.WriteLine("PASS: " + label);
}
object DbScalar(string sql, params (string Key, object Value)[] args)
{
    using var conn = new SqlConnection(cs); conn.Open();
    using var cmd = new SqlCommand(sql,conn);
    foreach (var (key,value) in args) cmd.Parameters.AddWithValue(key,value);
    return cmd.ExecuteScalar() ?? DBNull.Value;
}
void DbExec(string sql, params (string Key, object Value)[] args)
{
    using var conn = new SqlConnection(cs); conn.Open();
    using var cmd = new SqlCommand(sql,conn);
    foreach (var (key,value) in args) cmd.Parameters.AddWithValue(key,value);
    cmd.ExecuteNonQuery();
}
async Task<JsonElement> Post(string path, object body)
{
    using var result = await http.PostAsJsonAsync(path.TrimStart('/'),body);
    var content = await result.Content.ReadAsStringAsync();
    if (!result.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {result.StatusCode} {path}: {content}");
    using var json = JsonDocument.Parse(content);
    return json.RootElement.Clone();
}
async Task<JsonElement> Get(string path)
{
    using var result = await http.GetAsync(path.TrimStart('/'));
    var content = await result.Content.ReadAsStringAsync();
    if (!result.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {result.StatusCode} {path}: {content}");
    using var json = JsonDocument.Parse(content);
    return json.RootElement.Clone();
}
static bool Ok(JsonElement node) => node.GetProperty("ok").GetBoolean();
static string Str(JsonElement node, params string[] keys)
{
    foreach (var key in keys) node = node.GetProperty(key);
    return node.GetString() ?? "";
}
void Cleanup()
{
    // Only the order created by this test's Workbench session may be removed.
    using var conn = new SqlConnection(cs); conn.Open();
    using var tx = conn.BeginTransaction();
    void Exec(string text, params (string Key, object Value)[] args)
    {
        using var cmd = new SqlCommand(text,conn,tx);
        foreach (var (key,value) in args) cmd.Parameters.AddWithValue(key,value);
        cmd.ExecuteNonQuery();
    }
    try
    {
        if (orderId is not null)
        {
            using var ownership = new SqlCommand("SELECT COUNT(*) FROM erp.SalesOrder WHERE SalesOrderId=@id AND ExternalOrderId=@external AND SourceSystem='nopCommerce'",conn,tx);
            ownership.Parameters.AddWithValue("@id",orderId);
            ownership.Parameters.AddWithValue("@external",external!);
            if (Convert.ToInt32(ownership.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Cleanup refused: demo order ownership mismatch");
            Exec("DELETE di FROM erp.DispatchIntent di JOIN erp.WarehouseDeparture wd ON di.DepartureId=wd.DepartureId WHERE wd.SalesOrderId=@id",("@id",orderId));
            foreach (var table in new[] {
                "voxware.PackingConfirmationLine","voxware.PackingConfirmation",
                "voxware.PickConfirmationLine","voxware.PickConfirmation",
                "voxware.PickPlanLine","voxware.PickPlan",
                "erp.WarehouseDeparture","erp.ConsumedReservationLine","erp.FulfillmentConsumption",
                "erp.StockReservation","erp.OrderCommitment","erp.SalesOrderLine","erp.SalesOrder"})
                Exec($"DELETE FROM {table} WHERE SalesOrderId=@id",("@id",orderId));
        }
        if (warehouseCreated)
        {
            Exec("DELETE FROM erp.StockOnHand WHERE WarehouseId=@w",("@w",warehouse));
            Exec("DELETE FROM erp.InventoryPosition WHERE WarehouseId=@w",("@w",warehouse));
            Exec("DELETE FROM erp.Warehouse WHERE WarehouseId=@w",("@w",warehouse));
        }
        tx.Commit();
        Console.WriteLine("CLEANUP: isolated ERP/Voxware order, departure, dispatch and warehouse removed");
    }
    catch { tx.Rollback(); throw; }
}
try
{
    // Validate seeded product identities up front; no product/master-data changes.
    Check("Seed ERP products present",products.All(p => Convert.ToInt32(DbScalar("SELECT COUNT(*) FROM erp.Product WHERE ProductId=@p",("@p",p))) == 1));
    DbExec("INSERT erp.Warehouse (WarehouseId,Name) VALUES (@w,'Q25.9 isolated status test')",("@w",warehouse));
    warehouseCreated = true;
    foreach (var p in products)
    {
        DbExec("INSERT erp.InventoryPosition (WarehouseId,ProductId) VALUES (@w,@p)",("@w",warehouse),("@p",p));
        DbExec("INSERT erp.StockOnHand (WarehouseId,ProductId,OnHand) VALUES (@w,@p,10)",("@w",warehouse),("@p",p));
    }
    Check("Isolated warehouse inventory seeded",products.All(p => Convert.ToInt32(DbScalar("SELECT OnHand FROM erp.StockOnHand WHERE WarehouseId=@w AND ProductId=@p",("@w",warehouse),("@p",p))) == 10));
    var session = await Post("api/session",new {});
    sessionId = Str(session,"id");
    external = Str(session,"reference");
    Check("Workbench session created",Guid.TryParse(sessionId,out _));
    var root = $"api/session/{sessionId}";
    var order = await Post(root+"/order",new { customerId="C0001",shipToLocationId="LOC-01",lines=new[] {new {sku="FD-DOG-0142",quantity=1},new {sku="FD-DOG-0143",quantity=1}} });
    Check("ERP order received",Ok(order));
    orderId = Str(order,"session","order","salesOrderId");
    var committed = await Post(root+"/commit",new { warehouseId=warehouse });
    Check("ERP isolated inventory committed",Ok(committed));
    Check("ERP inventory assessed",Ok(await Post(root+"/inventory",new {warehouseId=warehouse})));
    Check("Voxware pick plan generated",Ok(await Post(root+"/plan",new {})));
    Check("Voxware pick plan persisted",Ok(await Post(root+"/persist",new {})));
    Check("Voxware picking confirmed",Ok(await Post(root+"/pick",new {})));
    Check("Voxware packing confirmed",Ok(await Post(root+"/pack",new {})));
    var before = await Get(root+"/status");
    Check("HTTP status Packed before departure",Str(before,"stage")=="Packed" && !before.GetProperty("erp").GetProperty("departed").GetBoolean());
    var store = new SqlFulfillmentConsumption(cs);
    var result = store.ConfirmAndQueueDeparture(orderId,warehouse,external+"-PACK",departure,"Q259-TEST",request);
    Check("ERP authorized departure consumed inventory",result.Status == ConsumptionStatus.Consumed && result.ConsumedUnits == 2);
    var after = await Get(root+"/status");
    Check("HTTP status Departed",Str(after,"stage")=="Departed" && after.GetProperty("erp").GetProperty("departed").GetBoolean());
    Check("HTTP status dispatch Pending",Str(after,"erp","dispatchStatus")=="Pending");
    Check("HTTP status does not claim delivery",!after.GetProperty("deliveryConfirmed").GetBoolean());
    Check("Isolated stock consumed once",products.All(p => Convert.ToInt32(DbScalar("SELECT OnHand FROM erp.StockOnHand WHERE WarehouseId=@w AND ProductId=@p",("@w",warehouse),("@p",p))) == 9));
    var replay = store.ConfirmAndQueueDeparture(orderId,warehouse,external+"-PACK",departure,"Q259-TEST",request);
    Check("ERP departure replay idempotent",replay.Status == ConsumptionStatus.AlreadyConsumed);
    Check("HTTP status unchanged after replay",Str(await Get(root+"/status"),"erp","dispatchStatus")=="Pending");
}
catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL: Q25.9 verification: " + ex.Message); }
finally
{
    try { if (warehouseCreated || orderId is not null) Cleanup(); }
    catch(Exception ex) { failures++; Console.Error.WriteLine("FAIL: cleanup: " + ex.Message); }
}
Console.WriteLine($"RESULT: {checks}/{checks} assertions reached; failures={failures}");
return failures == 0 ? 0 : 1;
