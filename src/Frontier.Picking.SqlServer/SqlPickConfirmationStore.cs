using System.Data;
using Frontier.Orders;
using Microsoft.Data.SqlClient;

namespace Frontier.Picking.SqlServer;

/// <summary>Voxware-owned, durable, one-confirmation-per-plan persistence.
/// A stored Voxware pick plan is the authority for planned quantities.
/// This component neither posts ERP stock nor modifies ERP reservations.</summary>
public sealed class SqlPickConfirmationStore
{
    private readonly string _connectionString;
    public SqlPickConfirmationStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public PickExecutionResult Confirm(PickConfirmation? confirmation)
    {
        if (confirmation is null || string.IsNullOrWhiteSpace(confirmation.SalesOrderId) ||
            string.IsNullOrWhiteSpace(confirmation.WarehouseId) ||
            confirmation.SalesOrderId.Length > 64 || confirmation.WarehouseId.Length > 64 ||
            string.IsNullOrWhiteSpace(confirmation.ConfirmationId) || confirmation.ConfirmationId.Length > 64 ||
            confirmation.Lines is null || confirmation.Lines.Any(x => x is null ||
                string.IsNullOrWhiteSpace(x.ProductId) || x.ProductId.Length > 64))
            return Rejected(confirmation, "Invalid pick confirmation");

        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        using (var locking = Command(connection, tx, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource=N'Frontier.Voxware.PickConfirmation.Confirm',
                @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
            SELECT @result;
            """))
        {
            if ((int)locking.ExecuteScalar()! < 0)
                throw new InvalidOperationException("Voxware confirmation lock unavailable");
        }

        var plan = LoadPlan(connection, tx, confirmation.SalesOrderId, confirmation.WarehouseId);
        if (plan is null)
        {
            tx.Commit();
            return Rejected(confirmation, "No stored Voxware pick plan");
        }
        var prior = LoadConfirmation(connection, tx, confirmation.SalesOrderId, confirmation.WarehouseId);
        if (prior is not null)
        {
            // The in-memory engine establishes the existing equivalence semantics.
            var engine = new PickExecution();
            var recorded = engine.Confirm(plan, prior);
            if (recorded.Status is not (PickExecutionStatus.Completed or PickExecutionStatus.Partial))
                throw new InvalidDataException("Stored pick confirmation violates plan contract");
            var replay = engine.Confirm(plan, confirmation);
            tx.Commit();
            return replay.Status == PickExecutionStatus.AlreadyRecorded
                ? recorded with { Status = PickExecutionStatus.AlreadyRecorded,
                    Message = "Confirmation already recorded" }
                : Rejected(confirmation, "Pick plan already has a different confirmation");
        }

        var result = new PickExecution().Confirm(plan, confirmation);
        if (result.Status is not (PickExecutionStatus.Completed or PickExecutionStatus.Partial))
        {
            tx.Commit();
            return result;
        }
        using (var cmd = Command(connection, tx, """
            INSERT INTO voxware.PickConfirmation
                (SalesOrderId, WarehouseId, ConfirmationId, Outcome)
            VALUES (@order, @warehouse, @confirmation, @outcome);
            """))
        {
            BindKey(cmd, confirmation.SalesOrderId, confirmation.WarehouseId);
            cmd.Parameters.Add("@confirmation", SqlDbType.VarChar, 64).Value = confirmation.ConfirmationId;
            cmd.Parameters.Add("@outcome", SqlDbType.VarChar, 20).Value = result.Status.ToString();
            cmd.ExecuteNonQuery();
        }
        foreach (var line in result.Lines)
        {
            using var cmd = Command(connection, tx, """
                INSERT INTO voxware.PickConfirmationLine
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
        string status;
        using (var cmd = Command(connection, tx, """
            SELECT Status FROM voxware.PickPlan
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse;
            """))
        {
            BindKey(cmd, order, warehouse);
            status = cmd.ExecuteScalar() as string ?? string.Empty;
        }
        if (string.IsNullOrEmpty(status)) return null;
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

    private static PickConfirmation? LoadConfirmation(SqlConnection connection, SqlTransaction tx,
        string order, string warehouse)
    {
        string? confirmationId;
        using (var cmd = Command(connection, tx, """
            SELECT ConfirmationId FROM voxware.PickConfirmation
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse;
            """))
        {
            BindKey(cmd, order, warehouse);
            confirmationId = cmd.ExecuteScalar() as string;
        }
        if (confirmationId is null) return null;
        var lines = new List<PickedLine>();
        using (var cmd = Command(connection, tx, """
            SELECT ProductId, Quantity FROM voxware.PickConfirmationLine
            WHERE SalesOrderId=@order AND WarehouseId=@warehouse ORDER BY ProductId;
            """))
        {
            BindKey(cmd, order, warehouse);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) lines.Add(new PickedLine(reader.GetString(0), reader.GetInt32(1)));
        }
        return new PickConfirmation(confirmationId, order, warehouse, lines.AsReadOnly());
    }

    private static PickExecutionResult Rejected(PickConfirmation? confirmation, string message) =>
        new(PickExecutionStatus.Rejected, confirmation?.SalesOrderId ?? "",
            confirmation?.WarehouseId ?? "", null, Array.Empty<PickedLine>(), message);

    private static SqlCommand Command(SqlConnection connection, SqlTransaction tx, string sql) =>
        new(sql, connection, tx);
    private static void BindKey(SqlCommand cmd, string order, string warehouse)
    {
        cmd.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = order;
        cmd.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouse;
    }
}
