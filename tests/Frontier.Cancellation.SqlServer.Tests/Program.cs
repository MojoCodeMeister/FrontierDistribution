using Frontier.Orders.SqlServer;
using Frontier.Picking.SqlServer;
using Microsoft.Data.SqlClient;

var cs = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING required; no SQL changes made");
    return 2;
}
var suffix = Guid.NewGuid().ToString("N")[..14].ToUpperInvariant();
var wh = "Q25-W-" + suffix;
var product = "P000142";
var orders = new List<string>();
var passes = 0;
var failures = 0;
void Check(string label, bool okay)
{
    Console.WriteLine($"{(okay ? "PASS" : "FAIL")} {label}");
    if (okay) passes++; else failures++;
}
void Exec(SqlConnection conn, SqlTransaction tx, string sql, params (string,object)[] args)
{
    using var cmd = new SqlCommand(sql,conn,tx);
    foreach (var (name,val) in args) cmd.Parameters.AddWithValue(name,val);
    cmd.ExecuteNonQuery();
}
int Number(string sql, params (string,object)[] args)
{
    using var conn = new SqlConnection(cs); conn.Open();
    using var cmd = new SqlCommand(sql,conn);
    foreach(var (name,val) in args) cmd.Parameters.AddWithValue(name,val);
    return Convert.ToInt32(cmd.ExecuteScalar());
}
int Res(string order) => Number("SELECT COALESCE(SUM(Quantity),0) FROM erp.StockReservation WHERE SalesOrderId=@id", ("@id",order));
string Status(string order)
{
    using var conn=new SqlConnection(cs);conn.Open();
    using var cmd=new SqlCommand("SELECT Status FROM erp.SalesOrder WHERE SalesOrderId=@id",conn);
    cmd.Parameters.AddWithValue("@id",order);
    return (string)cmd.ExecuteScalar()!;
}
string NewOrder(int quantity)
{
    var id="Q25-O-"+suffix+"-"+orders.Count.ToString("D2");
    using var conn=new SqlConnection(cs);conn.Open();using var tx=conn.BeginTransaction();
    Exec(conn,tx,"INSERT erp.SalesOrder (SalesOrderId,SourceSystem,ExternalOrderId,CustomerId,ShipToLocationId,Status) VALUES (@id,'Q25-TEST',@id,'C0001','LOC-01','Received')",("@id",id));
    Exec(conn,tx,"INSERT erp.SalesOrderLine (SalesOrderId,SourceLineId,LineSequence,SourceSku,ProductId,Quantity) VALUES (@id,'1',1,'Q25-SKU',@p,@qty)",("@id",id),("@p",product),("@qty",quantity));
    tx.Commit();orders.Add(id);return id;
}
void Prepare()
{
    using var conn=new SqlConnection(cs);conn.Open();using var tx=conn.BeginTransaction();
    Exec(conn,tx,"INSERT erp.Warehouse (WarehouseId,Name) VALUES (@w,'Q25 test warehouse')",("@w",wh));
    Exec(conn,tx,"INSERT erp.InventoryPosition (WarehouseId,ProductId) VALUES (@w,@p)",("@w",wh),("@p",product));
    Exec(conn,tx,"INSERT erp.StockOnHand (WarehouseId,ProductId,OnHand) VALUES (@w,@p,10)",("@w",wh),("@p",product));
    tx.Commit();
}
void Cleanup()
{
    using var conn=new SqlConnection(cs);conn.Open();using var tx=conn.BeginTransaction();
    foreach(var id in orders)
    {
        Exec(conn,tx,"DELETE FROM voxware.PickPlanLine WHERE SalesOrderId=@id",("@id",id));
        Exec(conn,tx,"DELETE FROM voxware.PickPlan WHERE SalesOrderId=@id",("@id",id));
        Exec(conn,tx,"DELETE FROM erp.ReleasedReservationLine WHERE SalesOrderId=@id",("@id",id));
        Exec(conn,tx,"DELETE FROM erp.OrderCancellation WHERE SalesOrderId=@id",("@id",id));
        Exec(conn,tx,"DELETE FROM erp.StockReservation WHERE SalesOrderId=@id",("@id",id));
        Exec(conn,tx,"DELETE FROM erp.OrderCommitment WHERE SalesOrderId=@id",("@id",id));
        Exec(conn,tx,"DELETE FROM erp.SalesOrderLine WHERE SalesOrderId=@id",("@id",id));
        Exec(conn,tx,"DELETE FROM erp.SalesOrder WHERE SalesOrderId=@id AND SourceSystem='Q25-TEST'",("@id",id));
    }
    Exec(conn,tx,"DELETE FROM erp.StockOnHand WHERE WarehouseId=@w",("@w",wh));
    Exec(conn,tx,"DELETE FROM erp.InventoryPosition WHERE WarehouseId=@w",("@w",wh));
    Exec(conn,tx,"DELETE FROM erp.Warehouse WHERE WarehouseId=@w AND Name='Q25 test warehouse'",("@w",wh));
    tx.Commit();
}
try
{
    Check("Required ERP seed data exists",Number("SELECT COUNT(*) FROM erp.Product WHERE ProductId=@p",("@p",product))==1 &&
        Number("SELECT COUNT(*) FROM erp.CustomerLocation WHERE CustomerId='C0001' AND LocationId='LOC-01'")==1);
    if(failures>0) return 1;
    Prepare();
    var service = new SqlOrderCancellation(cs,new SqlWarehouseCancellationGuard());
    var commitment = new SqlOrderCommitment(cs);
    Check("Unknown order cannot cancel",service.Cancel("Q25-UNKNOWN-"+suffix,wh).Status==CancellationStatus.NotFound);
    var uncommitted=NewOrder(1);
    Check("Received uncommitted order not eligible",service.Cancel(uncommitted,wh).Status==CancellationStatus.NotCommitted);
    var first=NewOrder(4);
    Check("ERP commits first order",commitment.Accept(first,wh).Status==CommitmentStatus.Accepted && Res(first)==4);
    Check("Wrong warehouse cannot cancel",service.Cancel(first,"WH-01").Status==CancellationStatus.Conflict && Res(first)==4);
    var cancel=service.Cancel(first,wh);
    Check("ERP cancellation releases four units",cancel.Status==CancellationStatus.Cancelled && cancel.ReleasedUnits==4 && Res(first)==0);
    Check("Cancellation status and audit persisted",Status(first)=="Cancelled" &&
        Number("SELECT COUNT(*) FROM erp.OrderCancellation WHERE SalesOrderId=@id",("@id",first))==1 &&
        Number("SELECT SUM(Quantity) FROM erp.ReleasedReservationLine WHERE SalesOrderId=@id",("@id",first))==4);
    Check("Physical on-hand unchanged",Number("SELECT OnHand FROM erp.StockOnHand WHERE WarehouseId=@w AND ProductId=@p",("@w",wh),("@p",product))==10);
    Check("Repeat cancellation is idempotent",service.Cancel(first,wh).Status==CancellationStatus.AlreadyCancelled && Res(first)==0);
    Check("Cancelled order cannot be reaccepted",commitment.Accept(first,wh).Status==CommitmentStatus.Conflict && Res(first)==0);
    var second=NewOrder(8);
    Check("Released stock is available to another order",commitment.Accept(second,wh).Status==CommitmentStatus.Accepted && Res(second)==8);
    using(var conn=new SqlConnection(cs))
    {
        conn.Open();using var tx=conn.BeginTransaction();
        Exec(conn,tx,"INSERT voxware.PickPlan (SalesOrderId,WarehouseId,Status) VALUES (@id,@w,'Ready')",("@id",second),("@w",wh));
        Exec(conn,tx,"INSERT voxware.PickPlanLine (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@id,@w,@p,8)",("@id",second),("@w",wh),("@p",product));
        tx.Commit();
    }
    Check("Voxware plan blocks ordinary cancellation",service.Cancel(second,wh).Status==CancellationStatus.BlockedWarehouseActivity && Res(second)==8);
    Check("Blocked cancellation changes neither audit nor status",Status(second)=="Received" &&
        Number("SELECT COUNT(*) FROM erp.OrderCancellation WHERE SalesOrderId=@id",("@id",second))==0);
}
catch(Exception ex)
{
    Console.Error.WriteLine("FAIL Q25.1 integration execution: "+ex);
    failures++;
}
finally
{
    try {Cleanup();Console.WriteLine("CLEANUP: Q25.1 isolated fixtures removed");}
    catch(Exception ex){Console.Error.WriteLine("FAIL cleanup: "+ex); failures++;}
}
Console.WriteLine($"RESULT: {passes}/{passes+failures} checks passed");
return failures==0?0:1;
