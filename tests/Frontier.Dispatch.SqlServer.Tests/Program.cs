using Frontier.Orders.SqlServer;
using Microsoft.Data.SqlClient;
using System.Data;

var cs = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(cs)) { Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING required"); return 2; }
var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
var warehouse = "Q255W" + suffix;
var product = "P000142";
var ready = "Q255R" + suffix;
var incomplete = "Q255I" + suffix;
var notPacked = "Q255N" + suffix;
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
    Sql("INSERT erp.SalesOrder (SalesOrderId,SourceSystem,ExternalOrderId,CustomerId,ShipToLocationId,Status) VALUES (@id,'Q255-TEST',@id,'C0001','LOC-01','Received')", ("@id",id));
    Sql("INSERT erp.SalesOrderLine (SalesOrderId,SourceLineId,LineSequence,SourceSku,ProductId,Quantity) VALUES (@id,'1',1,'Q255-SKU',@p,3)", ("@id",id),("@p",product));
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
        // DispatchIntent is keyed by DepartureId, not SalesOrderId.
        // Delete dependents before the associated WarehouseDeparture row.
        Sql("DELETE di FROM erp.DispatchIntent AS di INNER JOIN erp.WarehouseDeparture AS wd ON wd.DepartureId=di.DepartureId WHERE wd.SalesOrderId=@id", ("@id",id));
        foreach (var table in new[] {"voxware.PackingConfirmationLine", "voxware.PackingConfirmation",
                 "voxware.PickConfirmationLine", "voxware.PickConfirmation", "voxware.PickPlanLine", "voxware.PickPlan",
                 "erp.WarehouseDeparture", "erp.ConsumedReservationLine", "erp.FulfillmentConsumption", "erp.StockReservation", "erp.OrderCommitment",
                 "erp.SalesOrderLine", "erp.SalesOrder"})
            Sql($"DELETE FROM {table} WHERE SalesOrderId=@id",("@id",id));
    }
    Sql("DELETE FROM erp.StockOnHand WHERE WarehouseId=@w",("@w",warehouse));
    Sql("DELETE FROM erp.InventoryPosition WHERE WarehouseId=@w",("@w",warehouse));
    Sql("DELETE FROM erp.Warehouse WHERE WarehouseId=@w",("@w",warehouse));
}
int OnHand() => Count("SELECT OnHand FROM erp.StockOnHand WHERE WarehouseId=@w AND ProductId=@p",("@w",warehouse),("@p",product));
int Reservations(string id) => Count("SELECT COUNT(*) FROM erp.StockReservation WHERE SalesOrderId=@id",("@id",id));
bool Throws<T>(Action action) where T: Exception { try { action(); return false; } catch(T) { return true; } }
var store = new SqlFulfillmentConsumption(cs);
try
{
    Sql("INSERT erp.Warehouse (WarehouseId,Name) VALUES (@w,'Q255 isolated warehouse')",("@w",warehouse));
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
    var requestId = "REQUEST"+suffix;
    var result = store.ConfirmAndQueueDeparture(ready,warehouse,"PACK","DEPART-R"+suffix,"ERP-SHIPPING",requestId);
    Check("Confirmed warehouse departure consumes inventory",result.Status==ConsumptionStatus.Consumed && result.ConsumedUnits==3);
    Check("ERP departure row recorded", Count("SELECT COUNT(*) FROM erp.WarehouseDeparture WHERE SalesOrderId=@id AND AttestedBy='ERP-SHIPPING'",("@id",ready))==1);
    Check("Route4Me dispatch intent pending", Count("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DispatchRequestId=@id AND Status='Pending'",("@id",requestId))==1);
    Check("On hand reduced exactly once",OnHand()==9);
    Check("Reservation retired",Reservations(ready)==0);
    Check("Consumption header durable",Count("SELECT COUNT(*) FROM erp.FulfillmentConsumption WHERE SalesOrderId=@id",("@id",ready))==1);
    Check("Consumed line durable",Count("SELECT SUM(Quantity) FROM erp.ConsumedReservationLine WHERE SalesOrderId=@id",("@id",ready))==3);
    Check("Replay across instances idempotent",new SqlFulfillmentConsumption(cs).ConfirmAndQueueDeparture(ready.ToLowerInvariant(),warehouse.ToLowerInvariant(),"pack","depart-r"+suffix.ToLowerInvariant(),"erp-shipping",requestId.ToLowerInvariant()).Status==ConsumptionStatus.AlreadyConsumed);
    Check("Replay leaves stock unchanged",OnHand()==9);
    Check("Replay does not duplicate intent",Count("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DepartureId=@id",("@id","DEPART-R"+suffix))==1);
    Check("Changed dispatch request rejected",store.ConfirmAndQueueDeparture(ready,warehouse,"PACK","DEPART-R"+suffix,"ERP-SHIPPING","DIFFERENT"+suffix).Status==ConsumptionStatus.Conflict);
    Check("Changed attesting actor rejected",store.ConfirmAndQueueDeparture(ready,warehouse,"PACK","DEPART-R"+suffix,"OTHER",requestId).Status==ConsumptionStatus.Conflict);
    Check("Changed departure rejected",store.ConfirmDeparture(ready,warehouse,"PACK","DIFFERENT"+suffix).Status==ConsumptionStatus.Conflict);
    Check("Changed packing rejected",store.ConfirmDeparture(ready,warehouse,"OTHER","DEPART-R"+suffix).Status==ConsumptionStatus.Conflict);
    Check("Conflict leaves stock unchanged",OnHand()==9);
    Check("Reused departure ID rejected",store.ConfirmDeparture(incomplete,warehouse,"PACK","DEPART-R"+suffix).Status==ConsumptionStatus.Conflict);
    Check("Unconsumed orders retain reservation",Reservations(incomplete)==1 && Reservations(notPacked)==1);
    Check("Available equals on-hand minus remaining reservations",OnHand()-Count("SELECT COALESCE(SUM(Quantity),0) FROM erp.StockReservation WHERE WarehouseId=@w",("@w",warehouse))==3);

    // Q25.5c: the Q25.5b pending request is now claimed and transitioned.
    var delivery = new SqlDispatchIntentDelivery(cs);
    Check("Claim rejects invalid lease", Throws<ArgumentOutOfRangeException>(() => delivery.ClaimNext(TimeSpan.Zero)));
    Check("Unknown request cannot be claimed", delivery.ClaimRequest("ABSENT"+suffix, TimeSpan.FromMinutes(5)) is null);
    var firstClaim = delivery.ClaimRequest(requestId, TimeSpan.FromMinutes(5));
    Check("Pending request claimable", firstClaim is not null && firstClaim.DispatchRequestId == requestId && firstClaim.AttemptCount == 1);
    Check("Claim correlates ERP departure", firstClaim?.SalesOrderId == ready && firstClaim?.WarehouseId == warehouse && firstClaim?.PackingId == "PACK");
    Check("Second worker cannot claim active lease", delivery.ClaimRequest(requestId, TimeSpan.FromMinutes(5)) is null);
    Check("Wrong lease cannot acknowledge", !delivery.MarkAccepted(requestId, Guid.NewGuid(), "R4M-WRONG"));
    Check("Wrong lease cannot retry", !delivery.MarkRetryableFailure(requestId,Guid.NewGuid(),"temporary",TimeSpan.Zero));
    Check("Retry schedules request", delivery.MarkRetryableFailure(requestId,firstClaim!.LeaseToken,"temporary",TimeSpan.Zero));
    Check("Retry retains stable request identity", Count("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DispatchRequestId=@id AND Status='Pending' AND AttemptCount=1",("@id",requestId)) == 1);
    var secondClaim = delivery.ClaimRequest(requestId, TimeSpan.FromMinutes(5));
    Check("Retry can reclaim", secondClaim is not null && secondClaim.DispatchRequestId == requestId && secondClaim.AttemptCount == 2 && secondClaim.LeaseToken != firstClaim.LeaseToken);
    Check("Old token cannot acknowledge new claim", !delivery.MarkAccepted(requestId,firstClaim.LeaseToken, "R4M-OLD"));
    Check("Current token can accept", delivery.MarkAccepted(requestId,secondClaim!.LeaseToken, "R4M-TEST"));
    Check("Accepted request durable", Count("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DispatchRequestId=@id AND Status='Accepted' AND AcceptedUtc IS NOT NULL AND LeaseToken IS NULL",("@id",requestId)) == 1);
    Check("Accepted request no longer claimable", delivery.ClaimRequest(requestId, TimeSpan.FromMinutes(5)) is null);
    Check("No duplicate request created", Count("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DepartureId=@id",("@id","DEPART-R"+suffix)) == 1);
    // Independent terminal-failure fixture; no ERP inventory side effects.
    // Return the accepted test intent to Pending solely for testing terminal state.
    Sql("UPDATE erp.DispatchIntent SET Status='Pending',AcceptedUtc=NULL,NextAttemptUtc=SYSUTCDATETIME() WHERE DispatchRequestId=@id",("@id",requestId));
    var terminalClaim = delivery.ClaimRequest(requestId, TimeSpan.FromMinutes(5));
    Check("Terminal fixture claimable",terminalClaim is not null);
    Check("Terminal failure recorded",delivery.MarkTerminalFailure(requestId,terminalClaim!.LeaseToken,"permanent rejection"));
    Check("Terminal failure not claimable", delivery.ClaimRequest(requestId, TimeSpan.FromMinutes(5)) is null && Count("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DispatchRequestId=@id AND Status='Failed'",("@id",requestId))==1);
    Check("Inventory unaffected by delivery retries",OnHand()==9 && Reservations(ready)==0);

}
catch (Exception ex) { Console.Error.WriteLine("FAIL: Q25.5 SQL integration: " + ex); failed++; }
finally
{
    try { Cleanup(); Console.WriteLine("CLEANUP: Q25.5 isolated ERP/Voxware fixtures removed"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL: cleanup: "+ex); }
}
Console.WriteLine($"RESULT: {checks-failed}/{checks} checks passed");
return failed==0?0:1;
