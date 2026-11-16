using System.Data;
using Microsoft.Data.SqlClient;

namespace Frontier.Orders.SqlServer;

public enum ConsumptionStatus { Consumed, AlreadyConsumed, NotFound, NotCommitted, NotReady, Conflict }
public sealed record ConsumptionResult(string SalesOrderId, string WarehouseId, string DepartureId,
    ConsumptionStatus Status, int ConsumedUnits, string Message);

/// <summary>
/// ERP inventory consumption following an explicit warehouse-custody departure attestation.
/// Voxware's completed packing is read as evidence; it does NOT itself trigger consumption.
/// Caller owns authenticity of the departure confirmation. Does not mutate Voxware or Route4Me.
/// Q25.4 supports one complete departure per order/warehouse, not partial shipments.
/// </summary>
public sealed class SqlFulfillmentConsumption
{
    private readonly string _cs;
    public SqlFulfillmentConsumption(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _cs = connectionString;
    }

    // Q25.4 entry point remains available and unchanged in behavior.
    public ConsumptionResult ConfirmDeparture(string salesOrderId, string warehouseId, string packingId, string departureId)
        => ConfirmCore(salesOrderId, warehouseId, packingId, departureId, null, null);

    /// <summary>
    /// Q25.5: ERP shipping workflow attests physical custody departure. Authenticated caller
    /// is responsible for checking the actor's authority and physical evidence; this method
    /// does not infer departure from Voxware packing or Route4Me dispatch.
    /// Consumes inventory, records the ERP departure, and queues the dispatch request atomically.
    /// It does not call Route4Me. Requires 090-erp-warehouse-departure.sql.
    /// </summary>
    public ConsumptionResult ConfirmAndQueueDeparture(string salesOrderId, string warehouseId,
        string packingId, string departureId, string attestedBy, string dispatchRequestId)
    {
        Validate(attestedBy, nameof(attestedBy));
        Validate(dispatchRequestId, nameof(dispatchRequestId));
        return ConfirmCore(salesOrderId, warehouseId, packingId, departureId, attestedBy, dispatchRequestId);
    }

    private ConsumptionResult ConfirmCore(string salesOrderId, string warehouseId,
        string packingId, string departureId, string? attestedBy, string? dispatchRequestId)
    {
        Validate(salesOrderId, nameof(salesOrderId));
        Validate(warehouseId, nameof(warehouseId));
        Validate(packingId, nameof(packingId));
        Validate(departureId, nameof(departureId));
        using var conn = new SqlConnection(_cs);
        conn.Open();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        using (var lockCmd = Cmd(conn, tx, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource=N'Frontier.ERP.Stock.Commitment',
                @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
            SELECT @result;
            """))
        {
            if ((int)lockCmd.ExecuteScalar()! < 0)
                throw new InvalidOperationException("ERP stock commitment lock unavailable");
        }
        ConsumptionResult Finish(ConsumptionStatus status, int units, string message)
        {
            tx.Commit();
            return new(salesOrderId, warehouseId, departureId, status, units, message);
        }
        var orderExists = Scalar("SELECT COUNT(*) FROM erp.SalesOrder WHERE SalesOrderId=@order") is int count && count == 1;
        if (!orderExists) return Finish(ConsumptionStatus.NotFound, 0, "Order not found");
        var committedWarehouse = Scalar("SELECT WarehouseId FROM erp.OrderCommitment WHERE SalesOrderId=@order") as string;
        if (committedWarehouse is null) return Finish(ConsumptionStatus.NotCommitted, 0, "Order has no commitment");
        if (!Same(committedWarehouse, warehouseId)) return Finish(ConsumptionStatus.Conflict, 0, "Warehouse differs from ERP commitment");
        using (var prior = Cmd(conn, tx, """
            SELECT WarehouseId, PackingId, DepartureId FROM erp.FulfillmentConsumption WHERE SalesOrderId=@order;
            """))
        {
            Bind(prior);
            using var reader = prior.ExecuteReader();
            if (reader.Read())
            {
                var exact = Same(reader.GetString(0), warehouseId) && Same(reader.GetString(1), packingId) &&
                            Same(reader.GetString(2), departureId);
                reader.Close();
                if (exact && dispatchRequestId is not null)
                {
                    using var replay = Cmd(conn, tx, """
                        SELECT d.AttestedBy, q.DispatchRequestId
                        FROM erp.WarehouseDeparture d
                        INNER JOIN erp.DispatchIntent q ON q.DepartureId=d.DepartureId
                        WHERE d.DepartureId=@departure AND d.SalesOrderId=@order;
                        """);
                    Bind(replay);
                    using var existing = replay.ExecuteReader();
                    exact = existing.Read() && Same(existing.GetString(0), attestedBy!) &&
                        Same(existing.GetString(1), dispatchRequestId);
                }
                return Finish(exact ? ConsumptionStatus.AlreadyConsumed : ConsumptionStatus.Conflict, 0,
                    exact ? "Departure already consumed" : "Different departure or dispatch intent already recorded");
            }
        }
        if ((int)Scalar("SELECT COUNT(*) FROM erp.OrderCancellation WHERE SalesOrderId=@order")! != 0)
            return Finish(ConsumptionStatus.Conflict, 0, "Order cancelled");
        if ((int)Scalar("SELECT COUNT(*) FROM erp.FulfillmentConsumption WHERE DepartureId=@departure")! != 0)
            return Finish(ConsumptionStatus.Conflict, 0, "Departure ID already used");
        string? existingPacking;
        using (var evidence = Cmd(conn, tx, """
            SELECT PackingId FROM voxware.PackingConfirmation WITH (UPDLOCK, HOLDLOCK)
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse AND Outcome='ReadyForShipment';
            """))
        {
            Bind(evidence);
            existingPacking = evidence.ExecuteScalar() as string;
        }
        if (existingPacking is null || !Same(existingPacking, packingId))
            return Finish(ConsumptionStatus.NotReady, 0, "Matching ready Voxware packing required");
        var reserved = Lines("SELECT ProductId, Quantity FROM erp.StockReservation WHERE SalesOrderId=@order AND WarehouseId=@warehouse");
        var packed = Lines("SELECT ProductId, Quantity FROM voxware.PackingConfirmationLine WHERE SalesOrderId=@order AND WarehouseId=@warehouse");
        if (reserved.Count == 0 || reserved.Count != packed.Count ||
            reserved.Any(x => !packed.TryGetValue(x.Key, out var q) || q != x.Value))
            return Finish(ConsumptionStatus.Conflict, 0, "Packed quantities differ from ERP reservation");
        foreach (var (product, qty) in reserved)
        {
            using var deduct = Cmd(conn, tx, """
                UPDATE erp.StockOnHand SET OnHand=OnHand-@qty
                WHERE WarehouseId=@warehouse AND ProductId=@product AND OnHand>=@qty;
                """);
            Bind(deduct);
            deduct.Parameters.Add("@product", SqlDbType.VarChar, 64).Value = product;
            deduct.Parameters.Add("@qty", SqlDbType.Int).Value = qty;
            if (deduct.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("ERP on-hand insufficient; consumption rolled back");
        }
        using (var header = Cmd(conn, tx, """
            INSERT erp.FulfillmentConsumption (SalesOrderId,WarehouseId,DepartureId,PackingId)
            VALUES (@order,@warehouse,@departure,@packing);
            """))
        {
            Bind(header);
            header.ExecuteNonQuery();
        }
        foreach (var (product, qty) in reserved)
        {
            using var ledger = Cmd(conn, tx, """
                INSERT erp.ConsumedReservationLine (SalesOrderId,WarehouseId,ProductId,Quantity)
                VALUES (@order,@warehouse,@product,@qty);
                """);
            Bind(ledger);
            ledger.Parameters.Add("@product", SqlDbType.VarChar, 64).Value = product;
            ledger.Parameters.Add("@qty", SqlDbType.Int).Value = qty;
            ledger.ExecuteNonQuery();
        }
        using (var delete = Cmd(conn, tx, "DELETE FROM erp.StockReservation WHERE SalesOrderId=@order AND WarehouseId=@warehouse"))
        {
            Bind(delete);
            if (delete.ExecuteNonQuery() != reserved.Count)
                throw new InvalidOperationException("ERP reservation count changed; consumption rolled back");
        }
        if (dispatchRequestId is not null)
        {
            using (var departure = Cmd(conn, tx, """
                INSERT erp.WarehouseDeparture (DepartureId, SalesOrderId, WarehouseId, PackingId, AttestedBy)
                VALUES (@departure,@order,@warehouse,@packing,@actor);
                """))
            {
                Bind(departure);
                departure.Parameters.Add("@actor", SqlDbType.VarChar, 64).Value = attestedBy!;
                departure.ExecuteNonQuery();
            }
            using (var intent = Cmd(conn, tx, """
                INSERT erp.DispatchIntent (DispatchRequestId, DepartureId, Status)
                VALUES (@request,@departure,'Pending');
                """))
            {
                Bind(intent);
                intent.Parameters.Add("@request", SqlDbType.VarChar, 64).Value = dispatchRequestId;
                intent.ExecuteNonQuery();
            }
        }
        return Finish(ConsumptionStatus.Consumed, checked(reserved.Values.Sum()),
            dispatchRequestId is null ? "ERP on-hand consumed; reservation retired" :
            "ERP departure recorded, inventory consumed, and dispatch intent queued");

        object? Scalar(string sql)
        {
            using var cmd = Cmd(conn, tx, sql); Bind(cmd); return cmd.ExecuteScalar();
        }
        Dictionary<string, int> Lines(string sql)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var cmd = Cmd(conn, tx, sql); Bind(cmd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (!map.TryAdd(reader.GetString(0), reader.GetInt32(1)))
                    throw new InvalidDataException("Duplicate product in stored quantity lines");
            return map;
        }
        void Bind(SqlCommand cmd)
        {
            cmd.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = salesOrderId;
            cmd.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouseId;
            cmd.Parameters.Add("@packing", SqlDbType.VarChar, 64).Value = packingId;
            cmd.Parameters.Add("@departure", SqlDbType.VarChar, 64).Value = departureId;
        }
    }
    private static void Validate(string? value, string param)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, param);
        if (value.Length > 64) throw new ArgumentOutOfRangeException(param, "Identifier cannot exceed 64 characters");
    }
    private static bool Same(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    private static SqlCommand Cmd(SqlConnection conn, SqlTransaction tx, string sql) => new(sql, conn, tx);
}
