using System.Data;
using Microsoft.Data.SqlClient;

namespace Frontier.Orders.SqlServer;

// ERP owns cancellation; warehouse eligibility is an explicit cross-application contract.
// The guard operates in this SQL transaction so a concurrent pick-plan insert cannot
// slip between the eligibility check and reservation release.
public interface IWarehouseCancellationGuard
{
    bool HasWarehouseActivity(SqlConnection connection, SqlTransaction transaction,
        string salesOrderId, string warehouseId);
}

public enum CancellationStatus { Cancelled, AlreadyCancelled, NotFound, NotCommitted, BlockedWarehouseActivity, Conflict }
public sealed record CancellationResult(string SalesOrderId, string WarehouseId,
    CancellationStatus Status, int ReleasedUnits, string Message);

public sealed class SqlOrderCancellation
{
    private readonly string _cs;
    private readonly IWarehouseCancellationGuard _guard;

    public SqlOrderCancellation(string connectionString, IWarehouseCancellationGuard guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _cs = connectionString;
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public CancellationResult Cancel(string salesOrderId, string warehouseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(salesOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(warehouseId);
        using var conn = new SqlConnection(_cs);
        conn.Open();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        using (var lockCmd = Cmd(conn, tx, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'Frontier.ERP.Stock.Commitment',
                @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
            SELECT @result;
            """))
        {
            if ((int)lockCmd.ExecuteScalar()! < 0)
                throw new InvalidOperationException("ERP stock commitment lock unavailable");
        }

        using (var order = Cmd(conn, tx, "SELECT Status FROM erp.SalesOrder WHERE SalesOrderId=@id"))
        {
            order.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = salesOrderId;
            if (order.ExecuteScalar() is not string)
                return Finish(tx, salesOrderId, warehouseId, CancellationStatus.NotFound, 0, "Order not found");
        }

        string? committedWarehouse;
        using (var existing = Cmd(conn, tx, "SELECT WarehouseId FROM erp.OrderCommitment WHERE SalesOrderId=@id"))
        {
            existing.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = salesOrderId;
            committedWarehouse = existing.ExecuteScalar() as string;
        }
        if (committedWarehouse is null)
            return Finish(tx, salesOrderId, warehouseId, CancellationStatus.NotCommitted, 0, "Order has no committed reservation");
        if (!string.Equals(committedWarehouse, warehouseId, StringComparison.OrdinalIgnoreCase))
            return Finish(tx, salesOrderId, warehouseId, CancellationStatus.Conflict, 0, "Warehouse differs from commitment");

        using (var prior = Cmd(conn, tx, "SELECT COUNT(*) FROM erp.OrderCancellation WHERE SalesOrderId=@id"))
        {
            prior.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = salesOrderId;
            if ((int)prior.ExecuteScalar()! > 0)
                return Finish(tx, salesOrderId, warehouseId, CancellationStatus.AlreadyCancelled, 0, "Cancellation already recorded");
        }

        if (_guard.HasWarehouseActivity(conn, tx, salesOrderId, warehouseId))
            return Finish(tx, salesOrderId, warehouseId, CancellationStatus.BlockedWarehouseActivity, 0,
                "Voxware has a pick plan; cancellation requires exception processing");

        using (var cancellation = Cmd(conn, tx, """
            INSERT erp.OrderCancellation (SalesOrderId, WarehouseId) VALUES (@id, @wh);
            """))
        {
            cancellation.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = salesOrderId;
            cancellation.Parameters.Add("@wh", SqlDbType.VarChar, 64).Value = warehouseId;
            cancellation.ExecuteNonQuery();
        }

        int released;
        using (var release = Cmd(conn, tx, """
            DECLARE @Released TABLE (
                SalesOrderId varchar(64),
                ProductId varchar(64),
                WarehouseId varchar(64),
                Quantity int
            );
            DELETE FROM erp.StockReservation
            OUTPUT DELETED.SalesOrderId, DELETED.ProductId, DELETED.WarehouseId, DELETED.Quantity
              INTO @Released (SalesOrderId, ProductId, WarehouseId, Quantity)
            WHERE SalesOrderId=@id;
            INSERT INTO erp.ReleasedReservationLine (SalesOrderId, ProductId, WarehouseId, Quantity)
            SELECT SalesOrderId, ProductId, WarehouseId, Quantity FROM @Released;
            SELECT COALESCE(SUM(Quantity), 0) FROM @Released;
            """))
        {
            release.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = salesOrderId;
            released = Convert.ToInt32(release.ExecuteScalar());
        }
        if (released <= 0)
            throw new InvalidOperationException("Commitment had no outstanding reservation; cancellation rolled back");
        using (var status = Cmd(conn, tx, "UPDATE erp.SalesOrder SET Status='Cancelled' WHERE SalesOrderId=@id AND Status='Received'"))
        {
            status.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = salesOrderId;
            if (status.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("ERP order is not in a cancellable state");
        }
        return Finish(tx, salesOrderId, warehouseId, CancellationStatus.Cancelled, released, "Reservation released; order cancelled");
    }

    private static CancellationResult Finish(SqlTransaction tx, string order, string wh,
        CancellationStatus status, int quantity, string message)
    {
        tx.Commit();
        return new(order, wh, status, quantity, message);
    }
    private static SqlCommand Cmd(SqlConnection connection, SqlTransaction transaction, string sql) => new(sql, connection, transaction);
}
