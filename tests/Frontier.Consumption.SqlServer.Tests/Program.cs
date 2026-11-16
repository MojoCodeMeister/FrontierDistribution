using Frontier.Orders.SqlServer;
using Microsoft.Data.SqlClient;
using System.Data;

var cs = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(cs)) { Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING required"); return 2; }
var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
var warehouse = "Q254W" + suffix;
var product = "P000142";
var ready = "Q254R" + suffix;
var incomplete = "Q254I" + suffix;
var notPacked = "Q254N" + suffix;
var ids = new[] {ready, incomplete, notPacked};
var checks = 0; var failed = 0;
void Check(string label, bool ok) { checks++; if (!ok) failed++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label}"); }
void Sql(string text, params (string Name,object Value)[] args)
{
    using var conn = new SqlConnection(cs); conn.Open();
    using var cmd = new SqlCommand(text,conn);
    foreach (var (name,value) in args) cmd.Parameters.AddWithValue(name,value);
    cmd.ExecuteNonQuery();
}
int Count(string sql, params (string Name,object Value)[] args)
{
    using var conn = new SqlConnection(cs); conn.Open(); using var cmd = new SqlCommand(sql,conn);
    foreach (var (name,value) in args) cmd.Parameters.AddWithValue(name,value);
    return Convert.ToInt32(cmd.ExecuteScalar());
}
void Order(string id, string? outcome)
{
    Sql("INSERT erp.SalesOrder (SalesOrderId,SourceSystem,ExternalOrderId,CustomerId,ShipToLocationId,Status) VALUES (@id,'Q254-TEST',@id,'C0001','LOC-01','Received')", ("@id",id));
    Sql("INSERT erp.SalesOrderLine (SalesOrderId,SourceLineId,LineSequence,SourceSku,ProductId,Quantity) VALUES (@id,'1',1,'Q254-SKU',@p,3)", ("@id",id),("@p",product));
    Sql("INSERT erp.OrderCommitment (SalesOrderId,WarehouseId) VALUES (@id,@w)",("@id",id),("@w",warehouse));
    Sql("INSERT erp.StockReservation (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@id,@w,@p,3)",("@id",id),("@w",warehouse),("@p",product));
    if (outcome is null) return;
    Sql("INSERT voxware.PickPlan (SalesOrderId,WarehouseId,Status) VALUES (@id,@w,'Ready')",("@id",id),("@w",warehouse));
    Sql("INSERT voxware.PickPlanLine (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@id,@w,@p,3)",("@id",id),("@w",warehouse),("@p",product));
    Sql("INSERT voxware.PickConfirmation (SalesOrderId,WarehouseId,ConfirmationId,Outcome) VALUES (@id,@w,'PICK',@outcome)",("@id",id),("@w",warehouse),("@outcome",outcome=="ReadyForShipment"?"Completed":"Partial"));
    Sql("INSERT voxware.PickConfirmationLine (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@id,@w,@p,@q)",("@id",id),("@w",warehouse),("@p",product),("@q",outcome=="ReadyForShipment"?3:1));
    Sql("INSERT voxware.PackingConfirmation (SalesOrderId,WarehouseId,PackingId,PickConfirmationId,Outcome) VALUES (@id,@w,'PACK','PICK',@outcome)",("@id",id),("@w",warehouse),("@outcome",outcome));
    Sql("INSERT voxware.PackingConfirmationLine (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@id,@w,@p,@q)",("@id",id),("@w",warehouse),("@p",product),("@q",outcome=="ReadyForShipment"?3:1));
}
void Cleanup()
{
    foreach (var id in ids)
    {
        foreach (var table in new[] {"voxware.PackingConfirmationLine", "voxware.PackingConfirmation",
                 "voxware.PickConfirmationLine", "voxware.PickConfirmation", "voxware.PickPlanLine", "voxware.PickPlan",
                 "erp.ConsumedReservationLine", "erp.FulfillmentConsumption", "erp.StockReservation", "erp.OrderCommitment",
                 "erp.SalesOrderLine", "erp.SalesOrder"})
            Sql($"DELETE FROM {table} WHERE SalesOrderId=@id",("@id",id));
    }
    Sql("DELETE FROM erp.StockOnHand WHERE WarehouseId=@w",("@w",warehouse));
    Sql("DELETE FROM erp.InventoryPosition WHERE WarehouseId=@w",("@w",warehouse));
    Sql("DELETE FROM erp.Warehouse WHERE WarehouseId=@w",("@w",warehouse));
}
int OnHand() => Count("SELECT OnHand FROM erp.StockOnHand WHERE WarehouseId=@w AND ProductId=@p",("@w",warehouse),("@p",product));
int Reservations(string id) => Count("SELECT COUNT(*) FROM erp.StockReservation WHERE SalesOrderId=@id",("@id",id));
var store = new SqlFulfillmentConsumption(cs);
try
{
    Sql("INSERT erp.Warehouse (WarehouseId,Name) VALUES (@w,'Q254 isolated warehouse')",("@w",warehouse));
    Sql("INSERT erp.InventoryPosition (WarehouseId,ProductId) VALUES (@w,@p)",("@w",warehouse),("@p",product));
    Sql("INSERT erp.StockOnHand (WarehouseId,ProductId,OnHand) VALUES (@w,@p,12)",("@w",warehouse),("@p",product));
    Order(ready,"ReadyForShipment"); Order(incomplete,"Incomplete"); Order(notPacked,null);
    Check("Reservation does not deduct on hand",OnHand()==12);
    Check("Unknown order rejected",store.ConfirmDeparture("UNKNOWN"+suffix,warehouse,"PACK","DEPART-X"+suffix).Status==ConsumptionStatus.NotFound);
    Check("Incomplete packing rejected",store.ConfirmDeparture(incomplete,warehouse,"PACK","DEPART-I"+suffix).Status==ConsumptionStatus.NotReady);
    Check("Missing packing rejected",store.ConfirmDeparture(notPacked,warehouse,"PACK","DEPART-N"+suffix).Status==ConsumptionStatus.NotReady);
    Check("Wrong packing reference rejected",store.ConfirmDeparture(ready,warehouse,"OTHER","DEPART-R"+suffix).Status==ConsumptionStatus.NotReady);
    Check("Invalid attempts preserve reservations",Reservations(ready)==1 && Reservations(incomplete)==1 && Reservations(notPacked)==1);
    Check("Invalid attempts preserve on hand",OnHand()==12);
    var result = store.ConfirmDeparture(ready,warehouse,"PACK","DEPART-R"+suffix);
    Check("Confirmed warehouse departure consumes inventory",result.Status==ConsumptionStatus.Consumed && result.ConsumedUnits==3);
    Check("On hand reduced exactly once",OnHand()==9);
    Check("Reservation retired",Reservations(ready)==0);
    Check("Consumption header durable",Count("SELECT COUNT(*) FROM erp.FulfillmentConsumption WHERE SalesOrderId=@id",("@id",ready))==1);
    Check("Consumed line durable",Count("SELECT SUM(Quantity) FROM erp.ConsumedReservationLine WHERE SalesOrderId=@id",("@id",ready))==3);
    Check("Replay across instances idempotent",new SqlFulfillmentConsumption(cs).ConfirmDeparture(ready.ToLowerInvariant(),warehouse.ToLowerInvariant(),"pack","depart-r"+suffix.ToLowerInvariant()).Status==ConsumptionStatus.AlreadyConsumed);
    Check("Replay leaves stock unchanged",OnHand()==9);
    Check("Changed departure rejected",store.ConfirmDeparture(ready,warehouse,"PACK","DIFFERENT"+suffix).Status==ConsumptionStatus.Conflict);
    Check("Changed packing rejected",store.ConfirmDeparture(ready,warehouse,"OTHER","DEPART-R"+suffix).Status==ConsumptionStatus.Conflict);
    Check("Conflict leaves stock unchanged",OnHand()==9);
    Check("Reused departure ID rejected",store.ConfirmDeparture(incomplete,warehouse,"PACK","DEPART-R"+suffix).Status==ConsumptionStatus.Conflict);
    Check("Unconsumed orders retain reservation",Reservations(incomplete)==1 && Reservations(notPacked)==1);
    Check("Available equals on-hand minus remaining reservations",OnHand()-Count("SELECT COALESCE(SUM(Quantity),0) FROM erp.StockReservation WHERE WarehouseId=@w",("@w",warehouse))==3);
}
catch (Exception ex) { Console.Error.WriteLine("FAIL: Q25.4 SQL integration: " + ex); failed++; }
finally
{
    try { Cleanup(); Console.WriteLine("CLEANUP: Q25.4 isolated ERP/Voxware fixtures removed"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL: cleanup: "+ex); }
}
Console.WriteLine($"RESULT: {checks-failed}/{checks} checks passed");
return failed==0?0:1;
