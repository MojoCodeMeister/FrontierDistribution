using System.Data;
using Frontier.Orders;
using Microsoft.Data.SqlClient;

namespace Frontier.Picking.SqlServer;

public enum PickPlanStoreStatus { Created, AlreadyRecorded, Rejected }
public sealed record PickPlanStoreResult(PickPlanStoreStatus Status, PickPlan? Plan, string Message);

/// <summary>
/// Voxware-owned durable pick-plan snapshot. Receives the existing PickPlanning output;
/// does not read ERP tables, allocate inventory, confirm picking or modify stock.
/// </summary>
public sealed class SqlPickPlanStore
{
    private readonly string _connectionString;

    public SqlPickPlanStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public PickPlanStoreResult Save(PickPlan? plan)
    {
        if (!Valid(plan)) return new(PickPlanStoreStatus.Rejected, null, "Invalid pick plan");
        // Snapshots isolate this operation from caller-owned mutable lists.
        var proposed = plan! with { Lines = Array.AsReadOnly(plan.Lines.Select(line => line with { }).ToArray()) };
        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        using (var locker = Cmd(connection, transaction, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'Frontier.Voxware.PickPlan.Save',
                @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
            SELECT @result;
            """))
        {
            var result = (int)locker.ExecuteScalar()!;
            if (result < 0) throw new InvalidOperationException($"Voxware pick-plan lock failed: {result}");
        }

        var existing = Load(connection, transaction, proposed.SalesOrderId, proposed.WarehouseId);
        if (existing is not null)
        {
            var same = Equivalent(existing, proposed);
            transaction.Commit();
            return same
                ? new(PickPlanStoreStatus.AlreadyRecorded, existing, "Pick plan already recorded")
                : new(PickPlanStoreStatus.Rejected, existing, "Pick plan key already exists with different contents");
        }
        using (var header = Cmd(connection, transaction, """
            INSERT INTO voxware.PickPlan (SalesOrderId, WarehouseId, Status)
            VALUES (@order, @warehouse, @status);
            """))
        {
            header.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = proposed.SalesOrderId;
            header.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = proposed.WarehouseId;
            header.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = proposed.Status.ToString();
            header.ExecuteNonQuery();
        }
        foreach (var line in proposed.Lines)
        {
            using var item = Cmd(connection, transaction, """
                INSERT INTO voxware.PickPlanLine (SalesOrderId, WarehouseId, ProductId, Quantity)
                VALUES (@order, @warehouse, @product, @quantity);
                """);
            item.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = proposed.SalesOrderId;
            item.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = proposed.WarehouseId;
            item.Parameters.Add("@product", SqlDbType.VarChar, 64).Value = line.ProductId;
            item.Parameters.Add("@quantity", SqlDbType.Int).Value = line.Quantity;
            item.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(PickPlanStoreStatus.Created, proposed, "Pick plan persisted");
    }

    private static PickPlan? Load(SqlConnection connection, SqlTransaction transaction, string order, string warehouse)
    {
        string storedOrder, storedWarehouse;
        PickPlanStatus status;
        using (var header = Cmd(connection, transaction, """
            SELECT SalesOrderId, WarehouseId, Status FROM voxware.PickPlan
            WHERE SalesOrderId = @order AND WarehouseId = @warehouse;
            """))
        {
            header.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = order;
            header.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouse;
            using var reader = header.ExecuteReader();
            if (!reader.Read()) return null;
            storedOrder = reader.GetString(0);
            storedWarehouse = reader.GetString(1);
            if (!Enum.TryParse(reader.GetString(2), true, out status) ||
                !Enum.IsDefined(status))
                throw new InvalidDataException("Unknown stored pick plan status");
        }
        var lines = new List<PickLine>();
        using (var detail = Cmd(connection, transaction, """
            SELECT ProductId, Quantity FROM voxware.PickPlanLine
            WHERE SalesOrderId = @order AND WarehouseId = @warehouse ORDER BY ProductId;
            """))
        {
            detail.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = storedOrder;
            detail.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = storedWarehouse;
            using var reader = detail.ExecuteReader();
            while (reader.Read()) lines.Add(new PickLine(reader.GetString(0), reader.GetInt32(1)));
        }
        if ((status == PickPlanStatus.Ready && lines.Count == 0) ||
            (status != PickPlanStatus.Ready && lines.Count != 0))
            throw new InvalidDataException("Stored pick plan violates status/line invariant");
        var message = status switch
        {
            PickPlanStatus.Ready => "Proposed pick list; no inventory committed",
            PickPlanStatus.BlockedShort => "Insufficient stock; no pick list generated",
            _ => "Inventory not known for every product; no pick list generated"
        };
        return new PickPlan(storedOrder, storedWarehouse, status, lines.AsReadOnly(), message);
    }

    private static bool Valid(PickPlan? plan) => plan is not null &&
        !string.IsNullOrWhiteSpace(plan.SalesOrderId) && plan.SalesOrderId.Length <= 64 &&
        !string.IsNullOrWhiteSpace(plan.WarehouseId) && plan.WarehouseId.Length <= 64 &&
        Enum.IsDefined(plan.Status) && plan.Lines is not null &&
        (plan.Status == PickPlanStatus.Ready ? plan.Lines.Count > 0 : plan.Lines.Count == 0) &&
        plan.Lines.All(line => line is not null && !string.IsNullOrWhiteSpace(line.ProductId) &&
            line.ProductId.Length <= 64 && line.Quantity > 0) &&
        plan.Lines.Select(line => line.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.Lines.Count;

    private static bool Equivalent(PickPlan a, PickPlan b) =>
        a.Status == b.Status && a.Lines.Count == b.Lines.Count &&
        a.Lines.All(x => b.Lines.Count(y => string.Equals(x.ProductId, y.ProductId,
            StringComparison.OrdinalIgnoreCase) && x.Quantity == y.Quantity) == 1);

    private static SqlCommand Cmd(SqlConnection connection, SqlTransaction transaction, string sql) =>
        new(sql, connection, transaction);
}
