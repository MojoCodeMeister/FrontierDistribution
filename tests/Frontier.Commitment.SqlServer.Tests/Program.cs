using System.Data;
using Frontier.Orders.SqlServer;
using Microsoft.Data.SqlClient;

var cs = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING must be provided; no database operations performed.");
    return 2;
}

// Every fixture uses a unique, nonproduction namespace. Cleanup removes only these fixtures.
var suffix = Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
var warehouse = "Q24-W-" + suffix;
var otherWarehouse = "Q24-U-" + suffix;
var productA = "P000142";
var productB = "P000143";
var orders = new List<string>();
var passes = 0; var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (ok) passes++; else failures++;
}
SqlOrderCommitment NewService() => new(cs);
int Count(string sql, params (string Name, object Value)[] parameters)
{
    using var conn = new SqlConnection(cs);
    conn.Open();
    using var cmd = new SqlCommand(sql, conn);
    foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
    return Convert.ToInt32(cmd.ExecuteScalar());
}
int Reservations(string order) => Count("SELECT COUNT(*) FROM erp.StockReservation WHERE SalesOrderId = @id", ("@id",order));
int CommittedUnits(string product, string atWarehouse) => Count("SELECT COALESCE(SUM(Quantity),0) FROM erp.StockReservation WHERE WarehouseId = @w AND ProductId = @p", ("@w",atWarehouse),("@p",product));
string MakeOrder(params (string Product, int Quantity)[] lines)
{
    var id = "Q24-O-" + suffix + "-" + orders.Count.ToString("D2");
    using var conn = new SqlConnection(cs);
    conn.Open();
    using var tx = conn.BeginTransaction();
    Exec(conn,tx,"INSERT erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status) VALUES (@id,'Q24-TEST',@id,'C0001','LOC-01','Received')",("@id",id));
    for(var i=0;i<lines.Length;i++)
        Exec(conn,tx,"INSERT erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (@id,@line,@seq,@sku,@product,@qty)",
            ("@id",id),("@line",(i+1).ToString()),("@seq",i+1),("@sku", "Q24-"+(i+1)),("@product",lines[i].Product),("@qty",lines[i].Quantity));
    tx.Commit();
    orders.Add(id);
    return id;
}
static void Exec(SqlConnection conn, SqlTransaction tx, string sql, params (string Name,object Value)[] pars)
{
    using var cmd = new SqlCommand(sql,conn,tx);
    foreach(var (name,val) in pars) cmd.Parameters.AddWithValue(name,val);
    cmd.ExecuteNonQuery();
}
void Prepare()
{
    using var conn=new SqlConnection(cs);
    conn.Open();
    using var tx=conn.BeginTransaction();
    foreach(var w in new[]{warehouse,otherWarehouse})
        Exec(conn,tx,"INSERT erp.Warehouse (WarehouseId,Name) VALUES (@w,'Q24 temporary test warehouse')",("@w",w));
    foreach(var product in new[]{productA,productB})
    {
        Exec(conn,tx,"INSERT erp.InventoryPosition (WarehouseId,ProductId) VALUES (@w,@p)",("@w",warehouse),("@p",product));
        Exec(conn,tx,"INSERT erp.StockOnHand (WarehouseId,ProductId,OnHand) VALUES (@w,@p,@q)",("@w",warehouse),("@p",product),("@q",product==productA ? 10:2));
    }
    // The other warehouse has a valid inventory position but no stock snapshot (Unknown).
    Exec(conn,tx,"INSERT erp.InventoryPosition (WarehouseId,ProductId) VALUES (@w,@p)",("@w",otherWarehouse),("@p",productA));
    tx.Commit();
}
void Cleanup()
{
    using var conn=new SqlConnection(cs);
    conn.Open();
    using var tx=conn.BeginTransaction();
    // Restrict all deletes to the unique test warehouse/order namespace.
    foreach(var order in orders)
    {
        Exec(conn,tx,"DELETE FROM erp.StockReservation WHERE SalesOrderId=@id",("@id",order));
        Exec(conn,tx,"DELETE FROM erp.OrderCommitment WHERE SalesOrderId=@id",("@id",order));
        Exec(conn,tx,"DELETE FROM erp.SalesOrderLine WHERE SalesOrderId=@id",("@id",order));
        Exec(conn,tx,"DELETE FROM erp.SalesOrder WHERE SalesOrderId=@id AND SourceSystem='Q24-TEST'",("@id",order));
    }
    foreach(var w in new[]{warehouse,otherWarehouse})
    {
        Exec(conn,tx,"DELETE FROM erp.StockOnHand WHERE WarehouseId=@w",("@w",w));
        Exec(conn,tx,"DELETE FROM erp.InventoryPosition WHERE WarehouseId=@w",("@w",w));
        Exec(conn,tx,"DELETE FROM erp.Warehouse WHERE WarehouseId=@w AND Name='Q24 temporary test warehouse'",("@w",w));
    }
    tx.Commit();
}

try
{
    // Verify seed assumptions before writing. This suite intentionally depends on the existing ERP foundation.
    Check("ERP seed products and customer/location exist", Count("SELECT COUNT(*) FROM erp.Product WHERE ProductId IN ('P000142','P000143')") == 2 &&
        Count("SELECT COUNT(*) FROM erp.CustomerLocation WHERE LocationId='LOC-01' AND CustomerId='C0001'")==1);
    if(failures>0) return 1;
    Prepare();
    var missing=NewService().Accept("Q24-NO-ORDER-"+suffix,warehouse);
    Check("Unknown sales order is NotFound",missing.Status==CommitmentStatus.NotFound);
    var accepted=MakeOrder((productA,4));
    var result=NewService().Accept(accepted,warehouse);
    Check("Sufficient stock accepted",result.Status==CommitmentStatus.Accepted);
    Check("Reservation has correct quantity",Reservations(accepted)==1 && CommittedUnits(productA,warehouse)==4);
    Check("On-hand unchanged by reservation",Count("SELECT OnHand FROM erp.StockOnHand WHERE WarehouseId=@w AND ProductId=@p",("@w",warehouse),("@p",productA))==10);
    Check("Header created exactly once",Count("SELECT COUNT(*) FROM erp.OrderCommitment WHERE SalesOrderId=@id",("@id",accepted))==1);
    var replay=NewService().Accept(accepted,warehouse);
    Check("Repeated acceptance is idempotent",replay.Status==CommitmentStatus.AlreadyAccepted && CommittedUnits(productA,warehouse)==4);
    Check("Different warehouse conflicts on accepted order",NewService().Accept(accepted,otherWarehouse).Status==CommitmentStatus.Conflict);
    var tooLarge=MakeOrder((productA,7));
    var shortResult=NewService().Accept(tooLarge,warehouse);
    Check("Shortage blocks acceptance",shortResult.Status==CommitmentStatus.Short && shortResult.Lines.Single().AvailableToPromise==6);
    Check("Short order leaves no header or reservation",Reservations(tooLarge)==0 && Count("SELECT COUNT(*) FROM erp.OrderCommitment WHERE SalesOrderId=@id",("@id",tooLarge))==0);
    var multi=MakeOrder((productA,2),(productB,3));
    Check("Multi-line shortage rejects entire order",NewService().Accept(multi,warehouse).Status==CommitmentStatus.Short && Reservations(multi)==0);
    Check("Multi-line rejection leaves ATP unchanged",CommittedUnits(productA,warehouse)==4 && CommittedUnits(productB,warehouse)==0);
    var unknown=MakeOrder((productA,1));
    Check("Missing stock snapshot is Unknown",NewService().Accept(unknown,otherWarehouse).Status==CommitmentStatus.Unknown && Reservations(unknown)==0);
    // One successful multi-line order exercises the atomic header + multiple-line write path.
    var complete=MakeOrder((productA,1),(productB,2));
    Check("Multi-line order accepted as a unit",NewService().Accept(complete,warehouse).Status==CommitmentStatus.Accepted && Reservations(complete)==2);
    Check("Multi-line reserved quantities correct",CommittedUnits(productA,warehouse)==5 && CommittedUnits(productB,warehouse)==2);
    // Concurrency isolation: use a second temporary warehouse with 10 units of the same product.
    using(var conn=new SqlConnection(cs))
    {
        conn.Open(); using var tx=conn.BeginTransaction();
        Exec(conn,tx,"INSERT erp.StockOnHand (WarehouseId,ProductId,OnHand) VALUES (@w,@p,10)",("@w",otherWarehouse),("@p",productA));
        tx.Commit();
    }
    var racer1=MakeOrder((productA,7));
    var racer2=MakeOrder((productA,7));
    using var gate=new ManualResetEventSlim(false);
    var first=Task.Run(()=>{gate.Wait();return NewService().Accept(racer1,otherWarehouse);});
    var second=Task.Run(()=>{gate.Wait();return NewService().Accept(racer2,otherWarehouse);});
    gate.Set();
    var races=await Task.WhenAll(first,second);
    Check("Concurrent orders yield exactly one acceptance",races.Count(x=>x.Status==CommitmentStatus.Accepted)==1 && races.Count(x=>x.Status==CommitmentStatus.Short)==1);
    Check("Concurrent commitments do not oversell",CommittedUnits(productA,otherWarehouse)==7 && Reservations(racer1)+Reservations(racer2)==1);
    Check("Replay of winning order does not reserve again",NewService().Accept(races[0].Status==CommitmentStatus.Accepted?racer1:racer2,otherWarehouse).Status==CommitmentStatus.AlreadyAccepted && CommittedUnits(productA,otherWarehouse)==7);
    // Explicit SQL transaction rollback proving schema writes are rolled back as a unit.
    var rollback=MakeOrder((productA,1));
    using(var conn=new SqlConnection(cs))
    {
        conn.Open(); using var tx=conn.BeginTransaction();
        Exec(conn,tx,"INSERT erp.OrderCommitment (SalesOrderId,WarehouseId) VALUES (@o,@w)",("@o",rollback),("@w",warehouse));
        Exec(conn,tx,"INSERT erp.StockReservation (SalesOrderId,WarehouseId,ProductId,Quantity) VALUES (@o,@w,@p,1)",("@o",rollback),("@w",warehouse),("@p",productA));
        tx.Rollback();
    }
    Check("Rolled-back header and reservation are absent",Reservations(rollback)==0 && Count("SELECT COUNT(*) FROM erp.OrderCommitment WHERE SalesOrderId=@o",("@o",rollback))==0);
    Check("After rollback, acceptance succeeds",NewService().Accept(rollback,warehouse).Status==CommitmentStatus.Accepted);
}
catch(Exception ex)
{
    Console.Error.WriteLine("FAIL: SQL integration execution: " + ex);
    failures++;
}
finally
{
    try { Cleanup(); Console.WriteLine("CLEANUP: isolated Q24 fixtures removed"); }
    catch(Exception ex){Console.Error.WriteLine("FAIL: Q24 fixture cleanup: " + ex);failures++;}
}
Console.WriteLine($"RESULT: {passes}/{passes+failures} checks passed");
return failures==0?0:1;
