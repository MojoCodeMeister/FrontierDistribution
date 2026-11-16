using System.Data;
using Frontier.Orders;
using Microsoft.Data.SqlClient;

namespace Frontier.Picking.SqlServer;

/// <summary>Voxware-owned durable packing. Reads the stored plan and pick confirmation;
/// delegates packing rules to the established Packing domain behavior.
/// Does not modify ERP reservations or Route4Me shipments.</summary>
public sealed class SqlPackingConfirmationStore
{
    private readonly string _connectionString;

    public SqlPackingConfirmationStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public PackingResult Confirm(PackingConfirmation? confirmation)
    {
        if (confirmation is null || !ValidId(confirmation.SalesOrderId) ||
            !ValidId(confirmation.WarehouseId) || !ValidId(confirmation.PackingId) ||
            !ValidId(confirmation.PickConfirmationId) || confirmation.Lines is null ||
            confirmation.Lines.Any(x => x is null || !ValidId(x.ProductId)))
            return Rejected(confirmation, "Invalid packing confirmation");

        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        using (var locking = Command(connection, tx, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource=N'Frontier.Voxware.PackingConfirmation.Confirm',
                @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
            SELECT @result;
            """))
        {
            if ((int)locking.ExecuteScalar()! < 0)
                throw new InvalidOperationException("Voxware packing lock unavailable");
        }

        var plan = LoadPlan(connection, tx, confirmation.SalesOrderId, confirmation.WarehouseId);
        if (plan is null)
        {
            tx.Commit();
            return Rejected(confirmation, "No stored Voxware pick plan");
        }
        var pick = LoadPick(connection, tx, plan);
        if (pick is null)
        {
            tx.Commit();
            return Rejected(confirmation, "No valid stored Voxware pick confirmation");
        }

        var prior = LoadPacking(connection, tx, confirmation.SalesOrderId, confirmation.WarehouseId);
        if (prior is not null)
        {
            var engine = new Packing();
            var recorded = engine.Confirm(plan, pick, prior.Value.Confirmation);
            if (recorded.Status is not (PackingStatus.ReadyForShipment or PackingStatus.Incomplete) ||
                recorded.Status.ToString() != prior.Value.Outcome)
                throw new InvalidDataException("Stored Voxware packing violates packing contract");
            var replay = engine.Confirm(plan, pick, confirmation);
            tx.Commit();
            return replay.Status == PackingStatus.AlreadyRecorded
                ? recorded with { Status = PackingStatus.AlreadyRecorded, Message = "Packing already recorded" }
                : Rejected(confirmation, "Different packing confirmation already recorded");
        }

        var result = new Packing().Confirm(plan, pick, confirmation);
        if (result.Status is not (PackingStatus.ReadyForShipment or PackingStatus.Incomplete))
        {
            tx.Commit();
            return result;
        }
        using (var cmd = Command(connection, tx, """
            INSERT INTO voxware.PackingConfirmation
                (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Outcome)
            VALUES (@order, @warehouse, @packing, @pick, @outcome);
            """))
        {
            BindKey(cmd, confirmation.SalesOrderId, confirmation.WarehouseId);
            cmd.Parameters.Add("@packing", SqlDbType.VarChar, 64).Value = confirmation.PackingId;
            cmd.Parameters.Add("@pick", SqlDbType.VarChar, 64).Value = confirmation.PickConfirmationId;
            cmd.Parameters.Add("@outcome", SqlDbType.VarChar, 20).Value = result.Status.ToString();
            cmd.ExecuteNonQuery();
        }
        foreach (var line in result.Lines)
        {
            using var cmd = Command(connection, tx, """
                INSERT INTO voxware.PackingConfirmationLine
                    (SalesOrderId, WarehouseId, ProductId, Quantity)
                VALUES (@order, @warehouse, @product, @quantity);
                """);
            BindKey(cmd, confirmation.SalesOrderId, confirmation.WarehouseId);
            cmd.Parameters.Add("@product", SqlDbType.VarChar, 64).Value = line.ProductId;
            cmd.Parameters.Add("@quantity", SqlDbType.Int).Value = line.Quantity;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return result;
    }

    private static PickPlan? LoadPlan(SqlConnection connection, SqlTransaction tx, string order, string warehouse)
    {
        string? status;
        using (var cmd = Command(connection, tx, """
            SELECT Status FROM voxware.PickPlan
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse;
            """))
        {
            BindKey(cmd, order, warehouse);
            status = cmd.ExecuteScalar() as string;
        }
        if (status is null) return null;
        if (!Enum.TryParse<PickPlanStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed))
            throw new InvalidDataException("Unknown stored pick-plan status");
        var lines = new List<PickLine>();
        using (var cmd = Command(connection, tx, """
            SELECT ProductId, Quantity FROM voxware.PickPlanLine
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse ORDER BY ProductId;
            """))
        {
            BindKey(cmd, order, warehouse);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) lines.Add(new PickLine(reader.GetString(0), reader.GetInt32(1)));
        }
        if ((parsed == PickPlanStatus.Ready && lines.Count == 0) ||
            (parsed != PickPlanStatus.Ready && lines.Count != 0))
            throw new InvalidDataException("Stored pick-plan shape is invalid");
        return new PickPlan(order, warehouse, parsed, lines.AsReadOnly(), "Stored Voxware pick plan");
    }

    private static PickExecutionResult? LoadPick(SqlConnection connection, SqlTransaction tx, PickPlan plan)
    {
        string? confirmationId;
        string? outcome;
        using (var cmd = Command(connection, tx, """
            SELECT ConfirmationId, Outcome FROM voxware.PickConfirmation
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse;
            """))
        {
            BindKey(cmd, plan.SalesOrderId, plan.WarehouseId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            confirmationId = reader.GetString(0);
            outcome = reader.GetString(1);
        }
        var lines = new List<PickedLine>();
        using (var cmd = Command(connection, tx, """
            SELECT ProductId, Quantity FROM voxware.PickConfirmationLine
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse ORDER BY ProductId;
            """))
        {
            BindKey(cmd, plan.SalesOrderId, plan.WarehouseId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) lines.Add(new PickedLine(reader.GetString(0), reader.GetInt32(1)));
        }
        var result = new PickExecution().Confirm(plan,
            new PickConfirmation(confirmationId!, plan.SalesOrderId, plan.WarehouseId, lines.AsReadOnly()));
        if (result.Status is not (PickExecutionStatus.Completed or PickExecutionStatus.Partial) ||
            !string.Equals(result.Status.ToString(), outcome, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stored pick confirmation violates pick contract");
        return result;
    }

    private static (PackingConfirmation Confirmation, string Outcome)? LoadPacking(
        SqlConnection connection, SqlTransaction tx, string order, string warehouse)
    {
        string packingId;
        string pickId;
        string outcome;
        using (var cmd = Command(connection, tx, """
            SELECT PackingId, PickConfirmationId, Outcome FROM voxware.PackingConfirmation
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse;
            """))
        {
            BindKey(cmd, order, warehouse);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            packingId = reader.GetString(0);
            pickId = reader.GetString(1);
            outcome = reader.GetString(2);
        }
        var lines = new List<PackedLine>();
        using (var cmd = Command(connection, tx, """
            SELECT ProductId, Quantity FROM voxware.PackingConfirmationLine
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse ORDER BY ProductId;
            """))
        {
            BindKey(cmd, order, warehouse);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) lines.Add(new PackedLine(reader.GetString(0), reader.GetInt32(1)));
        }
        return (new PackingConfirmation(packingId, order, warehouse, pickId, lines.AsReadOnly()), outcome);
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 64;
    private static PackingResult Rejected(PackingConfirmation? confirmation, string message) =>
        new(PackingStatus.Rejected, confirmation?.SalesOrderId ?? "", confirmation?.WarehouseId ?? "",
            null, null, Array.Empty<PackedLine>(), message);
    private static SqlCommand Command(SqlConnection connection, SqlTransaction tx, string sql) =>
        new(sql, connection, tx);
    private static void BindKey(SqlCommand cmd, string order, string warehouse)
    {
        cmd.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = order;
        cmd.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouse;
    }
}
