using System.Data;
using Frontier.Contracts;
using Frontier.Orders;
using Microsoft.Data.SqlClient;

namespace Frontier.Orders.SqlServer;

/// <summary>
/// ERP-owned durable intake. Keeps the existing IncomingOrder/IntakeResult contracts;
/// in-memory OrderIntake and its regression harness are unchanged.
/// </summary>
public sealed class SqlOrderIntake
{
    private readonly string _connectionString;
    private readonly IProductIdentityResolver _resolver;

    public SqlOrderIntake(string connectionString, IProductIdentityResolver resolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public IntakeResult Receive(IncomingOrder? incoming)
    {
        if (incoming is null) return Reject("Order is required");
        if (string.IsNullOrWhiteSpace(incoming.SourceSystem) || string.IsNullOrWhiteSpace(incoming.ExternalOrderId))
            return Reject("Source system and external order ID are required");
        if (string.IsNullOrWhiteSpace(incoming.CustomerId))
            return Reject($"Unknown canonical customer: {incoming.CustomerId}");
        if (string.IsNullOrWhiteSpace(incoming.ShipToLocationId))
            return Reject("Shipping location does not belong to customer");
        if (incoming.Lines is null || incoming.Lines.Count == 0)
            return Reject("Order must contain lines");
        if (incoming.Lines.Any(l => l is null || string.IsNullOrWhiteSpace(l.SourceLineId) ||
            string.IsNullOrWhiteSpace(l.Sku) || l.Quantity <= 0))
            return Reject("Order line requires source line ID, SKU and positive quantity");
        if (incoming.Lines.Select(l => l.SourceLineId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != incoming.Lines.Count)
            return Reject("Duplicate source line IDs");

        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        // One transaction-scoped lock serializes allocation and competing source-key intake.
        // The lock is released by COMMIT or ROLLBACK, including process failure.
        using (var lockCommand = Cmd(connection, transaction, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'Frontier.ERP.SalesOrder.Intake',
                @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
            SELECT @result;
            """))
        {
            var lockResult = (int)lockCommand.ExecuteScalar()!;
            if (lockResult < 0) throw new InvalidOperationException($"ERP sales order lock failed: {lockResult}");
        }

        var existing = LoadExisting(connection, transaction, incoming.SourceSystem, incoming.ExternalOrderId);
        if (existing is not null)
        {
            var result = Equivalent(existing.Value, incoming)
                ? new IntakeResult(IntakeStatus.AlreadyReceived, existing.Value.Order, "Duplicate delivery: existing order returned")
                : Reject("External order reference already exists with different contents");
            transaction.Commit();
            return result;
        }

        using (var customer = Cmd(connection, transaction, """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM erp.Customer WHERE CustomerId = @customer) THEN 1 ELSE 0 END;
            """))
        {
            customer.Parameters.Add("@customer", SqlDbType.VarChar, 64).Value = incoming.CustomerId;
            if ((int)customer.ExecuteScalar()! == 0) return Reject("Unknown canonical customer: " + incoming.CustomerId);
        }
        using (var location = Cmd(connection, transaction, """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM erp.CustomerLocation
                WHERE LocationId = @location AND CustomerId = @customer) THEN 1 ELSE 0 END;
            """))
        {
            location.Parameters.Add("@location", SqlDbType.VarChar, 64).Value = incoming.ShipToLocationId;
            location.Parameters.Add("@customer", SqlDbType.VarChar, 64).Value = incoming.CustomerId;
            if ((int)location.ExecuteScalar()! == 0) return Reject("Shipping location does not belong to customer");
        }

        var lines = new List<SalesOrderLine>();
        foreach (var line in incoming.Lines)
        {
            var resolution = _resolver.Resolve(new ProductIdentityQuery(incoming.SourceSystem, "SKU", line.Sku));
            if (resolution.Status != ResolveStatus.Found || resolution.Product is null)
                return Reject($"SKU {line.Sku}: {resolution.Status}");
            lines.Add(new SalesOrderLine(line.SourceLineId, resolution.Product.ProductId, line.Quantity));
        }

        int number;
        using (var next = Cmd(connection, transaction, """
            SELECT ISNULL(MAX(TRY_CONVERT(int, SUBSTRING(SalesOrderId, 4, 64))), 0) + 1
            FROM erp.SalesOrder
            WHERE SalesOrderId LIKE 'SO-%';
            """))
            number = (int)next.ExecuteScalar()!;
        var id = $"SO-{number:0000}";
        using (var header = Cmd(connection, transaction, """
            INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId,
                CustomerId, ShipToLocationId, Status)
            VALUES (@id, @source, @external, @customer, @location, 'Received');
            """))
        {
            header.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = id;
            header.Parameters.Add("@source", SqlDbType.VarChar, 80).Value = incoming.SourceSystem;
            header.Parameters.Add("@external", SqlDbType.VarChar, 100).Value = incoming.ExternalOrderId;
            header.Parameters.Add("@customer", SqlDbType.VarChar, 64).Value = incoming.CustomerId;
            header.Parameters.Add("@location", SqlDbType.VarChar, 64).Value = incoming.ShipToLocationId;
            header.ExecuteNonQuery();
        }
        for (var i = 0; i < incoming.Lines.Count; i++)
        {
            var line = incoming.Lines[i];
            using var insert = Cmd(connection, transaction, """
                INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity)
                VALUES (@id, @line, @sequence, @sku, @product, @quantity);
                """);
            insert.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = id;
            insert.Parameters.Add("@line", SqlDbType.VarChar, 64).Value = line.SourceLineId;
            insert.Parameters.Add("@sequence", SqlDbType.Int).Value = i + 1;
            insert.Parameters.Add("@sku", SqlDbType.VarChar, 150).Value = line.Sku;
            insert.Parameters.Add("@product", SqlDbType.VarChar, 64).Value = lines[i].ProductId;
            insert.Parameters.Add("@quantity", SqlDbType.Int).Value = line.Quantity;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        var order = new SalesOrder(id, incoming.SourceSystem, incoming.ExternalOrderId,
            incoming.CustomerId, incoming.ShipToLocationId, "Received", lines.AsReadOnly());
        return new IntakeResult(IntakeStatus.Created, order, "Order received");
    }

    private static (SalesOrder Order, IReadOnlyList<IncomingOrderLine> Submitted)? LoadExisting(
        SqlConnection connection, SqlTransaction transaction, string source, string external)
    {
        string id, storedSource, storedExternal, customer, location, status;
        using (var header = Cmd(connection, transaction, """
            SELECT SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status
            FROM erp.SalesOrder WHERE SourceSystem = @source AND ExternalOrderId = @external;
            """))
        {
            header.Parameters.Add("@source", SqlDbType.VarChar, 80).Value = source;
            header.Parameters.Add("@external", SqlDbType.VarChar, 100).Value = external;
            using var reader = header.ExecuteReader();
            if (!reader.Read()) return null;
            id = reader.GetString(0);
            storedSource = reader.GetString(1);
            storedExternal = reader.GetString(2);
            customer = reader.GetString(3);
            location = reader.GetString(4);
            status = reader.GetString(5);
        }
        var submitted = new List<IncomingOrderLine>();
        var lines = new List<SalesOrderLine>();
        using (var detail = Cmd(connection, transaction, """
            SELECT SourceLineId, SourceSku, ProductId, Quantity
            FROM erp.SalesOrderLine WHERE SalesOrderId = @id ORDER BY LineSequence;
            """))
        {
            detail.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = id;
            using var reader = detail.ExecuteReader();
            while (reader.Read())
            {
                submitted.Add(new IncomingOrderLine(reader.GetString(0), reader.GetString(1), reader.GetInt32(3)));
                lines.Add(new SalesOrderLine(reader.GetString(0), reader.GetString(2), reader.GetInt32(3)));
            }
        }
        return (new SalesOrder(id, storedSource, storedExternal, customer, location, status, lines.AsReadOnly()),
            submitted.AsReadOnly());
    }

    private static bool Equivalent((SalesOrder Order, IReadOnlyList<IncomingOrderLine> Submitted) prior, IncomingOrder incoming) =>
        string.Equals(prior.Order.CustomerId, incoming.CustomerId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(prior.Order.ShipToLocationId, incoming.ShipToLocationId, StringComparison.OrdinalIgnoreCase) &&
        prior.Submitted.Count == incoming.Lines.Count &&
        prior.Submitted.Zip(incoming.Lines).All(pair =>
            string.Equals(pair.First.SourceLineId, pair.Second.SourceLineId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.Sku, pair.Second.Sku, StringComparison.OrdinalIgnoreCase) &&
            pair.First.Quantity == pair.Second.Quantity);

    private static SqlCommand Cmd(SqlConnection connection, SqlTransaction transaction, string sql) =>
        new(sql, connection, transaction);

    private static IntakeResult Reject(string reason) => new(IntakeStatus.Rejected, null, reason);
}
