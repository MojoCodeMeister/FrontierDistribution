using Frontier.Orders.SqlServer;
using Microsoft.Data.SqlClient;

var cs = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(cs)) { Console.Error.WriteLine("FRONTIER_SQL_CONNECTION_STRING required"); return 2; }
var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
var warehouse = "Q255D" + suffix;
var product = "P000142";
var orders = Enumerable.Range(1, 5).Select(n => "D" + n + suffix).ToArray();
int checks=0, failed=0;
void Check(string label, bool good) { checks++; if (!good) failed++; Console.WriteLine($"{(good ? "PASS" : "FAIL")} {label}"); }
void Sql(string sql, params (string Name,object Value)[] args)
{
    using var conn = new SqlConnection(cs); conn.Open();
    using var cmd = new SqlCommand(sql,conn);
    foreach (var (name,value) in args) cmd.Parameters.AddWithValue(name,value);
    cmd.ExecuteNonQuery();
}
int Scalar(string sql, params (string Name,object Value)[] args)
{
    using var conn = new SqlConnection(cs); conn.Open();
    using var cmd = new SqlCommand(sql,conn);
    foreach (var (name,value) in args) cmd.Parameters.AddWithValue(name,value);
    return Convert.ToInt32(cmd.ExecuteScalar());
}
void SeedOrder(string id)
{
    Sql("INSERT erp.SalesOrder (SalesOrderId,SourceSystem,ExternalOrderId,CustomerId,ShipToLocationId,Status) VALUES (@id,'Q255D-TEST',@id,'C0001','LOC-01','Received')",("@id",id));
    Sql("INSERT erp.SalesOrderLine (SalesOrderId,SourceLineId,LineSequence,SourceSku,ProductId,Quantity) VALUES (@id,'1',1,'Q255D-SKU',@p,3)",("@id",id),("@p",product));
    Sql("INSERT erp.OrderCommitment (SalesOrderId,WarehouseId) VALUES (@id,@w)",("@id",id),("@w",warehouse));
    Sql("INSERT erp.StockReservation (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@id,@w,@p,3)",("@id",id),("@w",warehouse),("@p",product));
    Sql("INSERT voxware.PickPlan (SalesOrderId,WarehouseId,Status) VALUES (@id,@w,'Ready')",("@id",id),("@w",warehouse));
    Sql("INSERT voxware.PickPlanLine (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@id,@w,@p,3)",("@id",id),("@w",warehouse),("@p",product));
    Sql("INSERT voxware.PickConfirmation (SalesOrderId,WarehouseId,ConfirmationId,Outcome) VALUES (@id,@w,'PICK','Completed')",("@id",id),("@w",warehouse));
    Sql("INSERT voxware.PickConfirmationLine (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@id,@w,@p,3)",("@id",id),("@w",warehouse),("@p",product));
    Sql("INSERT voxware.PackingConfirmation (SalesOrderId,WarehouseId,PackingId,PickConfirmationId,Outcome) VALUES (@id,@w,'PACK','PICK','ReadyForShipment')",("@id",id),("@w",warehouse));
    Sql("INSERT voxware.PackingConfirmationLine (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@id,@w,@p,3)",("@id",id),("@w",warehouse),("@p",product));
    var result = new SqlFulfillmentConsumption(cs).ConfirmAndQueueDeparture(id,warehouse,"PACK","DEP"+id,"ERP-SHIPPING","REQ"+id);
    Check("Departure queued for " + id, result.Status==ConsumptionStatus.Consumed);
}
void Cleanup()
{
    foreach(var id in orders)
    {
        Sql("DELETE di FROM erp.DispatchIntent di JOIN erp.WarehouseDeparture wd ON wd.DepartureId=di.DepartureId WHERE wd.SalesOrderId=@id",("@id",id));
        foreach(var table in new[]{"voxware.PackingConfirmationLine","voxware.PackingConfirmation","voxware.PickConfirmationLine","voxware.PickConfirmation","voxware.PickPlanLine","voxware.PickPlan","erp.WarehouseDeparture","erp.ConsumedReservationLine","erp.FulfillmentConsumption","erp.StockReservation","erp.OrderCommitment","erp.SalesOrderLine","erp.SalesOrder"})
            Sql($"DELETE FROM {table} WHERE SalesOrderId=@id",("@id",id));
    }
    Sql("DELETE FROM erp.StockOnHand WHERE WarehouseId=@w",("@w",warehouse));
    Sql("DELETE FROM erp.InventoryPosition WHERE WarehouseId=@w",("@w",warehouse));
    Sql("DELETE FROM erp.Warehouse WHERE WarehouseId=@w",("@w",warehouse));
}
var gateway = new FakeGateway();
var handoff = new Route4MeDispatchHandoff(cs, gateway);
try
{
    Sql("INSERT erp.Warehouse (WarehouseId,Name) VALUES (@w,'Q255d isolated warehouse')",("@w",warehouse));
    Sql("INSERT erp.InventoryPosition (WarehouseId,ProductId) VALUES (@w,@p)",("@w",warehouse),("@p",product));
    Sql("INSERT erp.StockOnHand (WarehouseId,ProductId,OnHand) VALUES (@w,@p,30)",("@w",warehouse),("@p",product));
    foreach(var id in orders) SeedOrder(id);
    var stableStock = Scalar("SELECT OnHand FROM erp.StockOnHand WHERE WarehouseId=@w AND ProductId=@p",("@w",warehouse),("@p",product));
    Check("Departure inventory consumed once for five orders",stableStock==15);

    gateway.SubmitBehavior = req => new(RemoteDispatchState.Accepted,req.DispatchRequestId,"R4M-"+req.DispatchRequestId);
    var accepted = await handoff.ProcessRequestAsync("REQ"+orders[0],TimeSpan.FromMinutes(5),TimeSpan.Zero);
    Check("Submit accepted",accepted.State==HandoffState.Accepted && gateway.SubmitCount==1);
    Check("Manifest contains ERP consumed line",gateway.LastRequest?.Lines.Count==1 && gateway.LastRequest.Lines[0].ProductId==product && gateway.LastRequest.Lines[0].Quantity==3);
    Check("Manifest contains ERP ship-to",gateway.LastRequest?.Destination.LocationId=="LOC-01"
        && gateway.LastRequest.Destination.Address1=="100 Example Drive"
        && gateway.LastRequest.Destination.City=="Oxford");
    Check("Manifest preserves departure identity",gateway.LastRequest?.DepartureId=="DEP"+orders[0] && gateway.LastRequest?.PackingId=="PACK");
    Check("Remote reference durable",Scalar("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DispatchRequestId=@id AND RemoteShipmentId=@remote",("@id","REQ"+orders[0]),("@remote","R4M-REQ"+orders[0]))==1);
    Check("Acknowledgment durable",Scalar("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DispatchRequestId=@id AND Status='Accepted'",("@id","REQ"+orders[0]))==1);
    Check("Accepted not reclaimed",(await handoff.ProcessRequestAsync("REQ"+orders[0],TimeSpan.FromMinutes(5),TimeSpan.Zero)).State==HandoffState.NoClaim);

    gateway.ReconcileBehavior = req => new(RemoteDispatchState.Accepted,req,"R4M-EXISTING");
    var remote = await handoff.ProcessRequestAsync("REQ"+orders[1],TimeSpan.FromMinutes(5),TimeSpan.Zero);
    Check("Existing remote acceptance reconciled",remote.State==HandoffState.Accepted);
    Check("Reconciled reference durable",Scalar("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DispatchRequestId=@id AND RemoteShipmentId='R4M-EXISTING'",("@id","REQ"+orders[1]))==1);
    Check("Reconciled request was not resubmitted",gateway.SubmitCount==1);

    gateway.ReconcileBehavior = req => new(RemoteDispatchState.Uncertain,Error:"lookup unavailable");
    var uncertain = await handoff.ProcessRequestAsync("REQ"+orders[2],TimeSpan.FromMinutes(5),TimeSpan.Zero);
    Check("Uncertain remote status is retryable",uncertain.State==HandoffState.RetryScheduled);
    Check("Uncertain remote status prevents submission",gateway.SubmitCount==1);
    gateway.ReconcileBehavior = req => new(RemoteDispatchState.NotFound);
    gateway.SubmitBehavior = req => throw new InvalidOperationException("response lost");
    var lost = await handoff.ProcessRequestAsync("REQ"+orders[2],TimeSpan.FromMinutes(5),TimeSpan.Zero);
    Check("Lost response becomes retryable",lost.State==HandoffState.RetryScheduled);
    gateway.ReconcileBehavior = req => new(RemoteDispatchState.Accepted,req,"R4M-FOUND-AFTER-LOSS");
    var recovered = await handoff.ProcessRequestAsync("REQ"+orders[2],TimeSpan.FromMinutes(5),TimeSpan.Zero);
    Check("Reconciliation recovers after lost response",recovered.State==HandoffState.Accepted);
    Check("Recovery does not resubmit",gateway.SubmitCount==2);
    Check("Recovered reference durable",Scalar("SELECT COUNT(*) FROM erp.DispatchIntent WHERE DispatchRequestId=@id AND RemoteShipmentId='R4M-FOUND-AFTER-LOSS'",("@id","REQ"+orders[2]))==1);

    // Verify no automatic resubmission after an uncertain first transport attempt.
    gateway.ReconcileBehavior = req => new(RemoteDispatchState.NotFound);
    gateway.SubmitBehavior = req => throw new InvalidOperationException("first response lost");
    var blockedFirst = await handoff.ProcessRequestAsync("REQ"+orders[4],TimeSpan.FromMinutes(5),TimeSpan.Zero);
    Check("Uncertain first submission retained",blockedFirst.State==HandoffState.RetryScheduled);
    var submitCountAtLoss = gateway.SubmitCount;
    var blockedSecond = await handoff.ProcessRequestAsync("REQ"+orders[4],TimeSpan.FromMinutes(5),TimeSpan.Zero);
    Check("Lookup NotFound after loss blocks duplicate submit",blockedSecond.State==HandoffState.RetryScheduled && gateway.SubmitCount==submitCountAtLoss);
    gateway.ReconcileBehavior = req => new(RemoteDispatchState.Accepted,req,"R4M-LATE-ACK");
    var lateAccepted = await handoff.ProcessRequestAsync("REQ"+orders[4],TimeSpan.FromMinutes(5),TimeSpan.Zero);
    Check("Late remote acknowledgment recovered",lateAccepted.State==HandoffState.Accepted && gateway.SubmitCount==submitCountAtLoss);

    gateway.ReconcileBehavior = req => new(RemoteDispatchState.Rejected,Error:"business rejection");
    var rejected = await handoff.ProcessRequestAsync("REQ"+orders[3],TimeSpan.FromMinutes(5),TimeSpan.Zero);
    Check("Remote rejection terminal",rejected.State==HandoffState.Failed);
    Check("Terminal request not claimed again",(await handoff.ProcessRequestAsync("REQ"+orders[3],TimeSpan.FromMinutes(5),TimeSpan.Zero)).State==HandoffState.NoClaim);
    Check("Inventory unaffected by all handoffs",Scalar("SELECT OnHand FROM erp.StockOnHand WHERE WarehouseId=@w AND ProductId=@p",("@w",warehouse),("@p",product))==stableStock);
    Check("One ERP intent per departure",Scalar("SELECT COUNT(*) FROM erp.DispatchIntent di JOIN erp.WarehouseDeparture wd ON wd.DepartureId=di.DepartureId WHERE wd.WarehouseId=@w",("@w",warehouse))==5);
}
catch(Exception ex){ failed++; Console.Error.WriteLine("FAIL: Q25.5d1 handoff: " + ex); }
finally
{
    try { Cleanup(); Console.WriteLine("CLEANUP: Q25.5d1 isolated fixtures removed"); }
    catch(Exception ex){ failed++; Console.Error.WriteLine("FAIL: cleanup: " + ex); }
}
Console.WriteLine($"RESULT: {checks-failed}/{checks} checks passed");
return failed==0?0:1;

sealed class FakeGateway : IRoute4MeDispatchGateway
{
    public int SubmitCount {get;private set;}
    public Route4MeDispatchRequest? LastRequest {get;private set;}
    public Func<string,RemoteDispatchResult> ReconcileBehavior {get;set;} = _ => new(RemoteDispatchState.NotFound);
    public Func<Route4MeDispatchRequest,RemoteDispatchResult> SubmitBehavior {get;set;} = r => new(RemoteDispatchState.Accepted,r.DispatchRequestId,"R4M-FAKE");
    public Task<RemoteDispatchResult> ReconcileAsync(string id,CancellationToken ct) => Task.FromResult(ReconcileBehavior(id));
    public Task<RemoteDispatchResult> SubmitAsync(Route4MeDispatchRequest r,CancellationToken ct)
    {
        SubmitCount++; LastRequest=r;
        return Task.FromResult(SubmitBehavior(r));
    }
}
