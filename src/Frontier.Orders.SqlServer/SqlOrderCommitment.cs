using System.Data;
using Microsoft.Data.SqlClient;

namespace Frontier.Orders.SqlServer;

public enum CommitmentStatus { Accepted, AlreadyAccepted, Short, Unknown, NotFound, Conflict }
public sealed record CommitmentLine(string ProductId, int Requested, int? AvailableToPromise);
public sealed record CommitmentResult(string SalesOrderId, string WarehouseId, CommitmentStatus Status,
    IReadOnlyList<CommitmentLine> Lines, string Message);

/// <summary>
/// ERP authority for all-or-nothing inventory commitment. Requires migration 060.
/// Existing order receipt is intentionally independent of acceptance.
/// Reservations remain active until an explicit release/consumption lifecycle is implemented.
/// </summary>
public sealed class SqlOrderCommitment
{
    private readonly string _connectionString;
    public SqlOrderCommitment(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public CommitmentResult Accept(string salesOrderId, string warehouseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(salesOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(warehouseId);
        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted);

        // One ERP-wide transactional lock arbitrates simultaneous commitments, irrespective of
        // which order or warehouse requests them. Conservatively serializes the first increment.
        using (var command = Command(connection, tx, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'Frontier.ERP.Stock.Commitment',
                @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
            SELECT @result;
            """))
        {
            if ((int)command.ExecuteScalar()! < 0)
                throw new InvalidOperationException("ERP stock commitment lock unavailable");
        }
        if (!Exists(connection, tx, "SELECT COUNT(*) FROM erp.SalesOrder WHERE SalesOrderId = @id", "@id", salesOrderId))
            return Finish(tx, salesOrderId, warehouseId, CommitmentStatus.NotFound, [], "Order not found");
        if (!Exists(connection, tx, "SELECT COUNT(*) FROM erp.Warehouse WHERE WarehouseId = @id", "@id", warehouseId))
            return Finish(tx, salesOrderId, warehouseId, CommitmentStatus.Conflict, [], "Warehouse not found");

        using (var existing = Command(connection, tx, "SELECT WarehouseId FROM erp.OrderCommitment WHERE SalesOrderId = @order"))
        {
            existing.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = salesOrderId;
            var priorWarehouse = existing.ExecuteScalar() as string;
            if (priorWarehouse != null)
                return Finish(tx, salesOrderId, warehouseId,
                    string.Equals(priorWarehouse, warehouseId, StringComparison.OrdinalIgnoreCase)
                        ? CommitmentStatus.AlreadyAccepted : CommitmentStatus.Conflict,
                    [], priorWarehouse.Equals(warehouseId, StringComparison.OrdinalIgnoreCase)
                        ? "Order already committed" : "Order committed at another warehouse");
        }

        var demand = new List<(string ProductId, int Quantity)>();
        using (var lines = Command(connection, tx, """
            SELECT ProductId, SUM(CONVERT(bigint, Quantity)) AS Demand
            FROM erp.SalesOrderLine WHERE SalesOrderId = @order GROUP BY ProductId;
            """))
        {
            lines.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = salesOrderId;
            using var reader = lines.ExecuteReader();
            while (reader.Read())
                demand.Add((reader.GetString(0), checked((int)reader.GetInt64(1))));
        }
        if (demand.Count == 0)
            return Finish(tx, salesOrderId, warehouseId, CommitmentStatus.Conflict, [], "Order has no lines");

        var results = new List<CommitmentLine>();
        foreach (var (productId, requested) in demand)
        {
            using var stock = Command(connection, tx, """
                SELECT CONVERT(bigint, soh.OnHand) - COALESCE((
                    SELECT SUM(CONVERT(bigint, r.Quantity)) FROM erp.StockReservation AS r
                    WHERE r.WarehouseId = soh.WarehouseId AND r.ProductId = soh.ProductId
                ), 0)
                FROM erp.StockOnHand AS soh
                WHERE soh.WarehouseId = @warehouse AND soh.ProductId = @product;
                """);
            stock.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouseId;
            stock.Parameters.Add("@product", SqlDbType.VarChar, 64).Value = productId;
            object? scalar = stock.ExecuteScalar();
            results.Add(new CommitmentLine(productId, requested,
                scalar is null or DBNull ? null : checked((int)(long)scalar)));
        }
        var outcome = results.Any(x => x.AvailableToPromise is null) ? CommitmentStatus.Unknown :
            results.Any(x => x.AvailableToPromise < x.Requested) ? CommitmentStatus.Short : CommitmentStatus.Accepted;
        if (outcome != CommitmentStatus.Accepted)
            return Finish(tx, salesOrderId, warehouseId, outcome, results,
                outcome == CommitmentStatus.Short ? "Insufficient uncommitted stock" : "Stock position unknown");

        using (var header = Command(connection, tx, """
            INSERT erp.OrderCommitment (SalesOrderId, WarehouseId) VALUES (@order, @warehouse);
            """))
        {
            header.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = salesOrderId;
            header.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouseId;
            header.ExecuteNonQuery();
        }
        foreach (var line in results)
        {
            using var insert = Command(connection, tx, """
                INSERT erp.StockReservation (SalesOrderId, WarehouseId, ProductId, Quantity)
                VALUES (@order, @warehouse, @product, @quantity);
                """);
            insert.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = salesOrderId;
            insert.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouseId;
            insert.Parameters.Add("@product", SqlDbType.VarChar, 64).Value = line.ProductId;
            insert.Parameters.Add("@quantity", SqlDbType.Int).Value = line.Requested;
            insert.ExecuteNonQuery();
        }
        return Finish(tx, salesOrderId, warehouseId, CommitmentStatus.Accepted, results, "Stock reserved; order accepted");
    }

    private static CommitmentResult Finish(SqlTransaction tx, string orderId, string warehouseId,
        CommitmentStatus status, IReadOnlyList<CommitmentLine> lines, string message)
    {
        tx.Commit();
        return new(orderId, warehouseId, status, lines, message);
    }

    private static bool Exists(SqlConnection conn, SqlTransaction tx, string query, string name, string value)
    {
        using var cmd = Command(conn, tx, query);
        cmd.Parameters.Add(name, SqlDbType.VarChar, 64).Value = value;
        return (int)cmd.ExecuteScalar()! == 1;
    }
    private static SqlCommand Command(SqlConnection conn, SqlTransaction tx, string query) => new(query, conn, tx);
}
