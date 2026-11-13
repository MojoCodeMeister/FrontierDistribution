using System.Data;
using Frontier.Orders;
using Microsoft.Data.SqlClient;

namespace Frontier.Inventory.SqlServer;

/// <summary>Read-only ERP stock assessment. Does not reserve or mutate inventory.</summary>
public sealed class SqlInventoryAvailability
{
    private readonly string _connectionString;

    public SqlInventoryAvailability(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public AvailabilityResult Assess(SalesOrder order, string warehouseId)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (string.IsNullOrWhiteSpace(warehouseId))
            throw new ArgumentException("Warehouse is required", nameof(warehouseId));
        if (order.Lines is null || order.Lines.Count == 0 ||
            order.Lines.Any(line => line is null || string.IsNullOrWhiteSpace(line.ProductId) || line.Quantity <= 0))
            throw new ArgumentException("Order lines must identify products and positive quantities", nameof(order));

        // Match in-memory aggregation and overflow behavior without changing its contract.
        var demand = order.Lines.GroupBy(line => line.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(group => (ProductId: group.Key, Requested: checked((int)group.Sum(line => (long)line.Quantity))))
            .ToArray();

        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using (var warehouse = new SqlCommand(
            "SELECT COUNT(*) FROM erp.Warehouse WHERE WarehouseId = @warehouse", connection))
        {
            warehouse.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouseId;
            if ((int)warehouse.ExecuteScalar()! != 1)
                throw new ArgumentException("Unknown warehouse", nameof(warehouseId));
        }

        // An absent StockOnHand row is unknown, not zero. This is a read-only snapshot.
        const string sql = "SELECT ProductId, OnHand FROM erp.StockOnHand WHERE WarehouseId = @warehouse";
        var stock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouseId;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!stock.TryAdd(reader.GetString(0), reader.GetInt32(1)))
                    throw new InvalidDataException("Duplicate stock position");
            }
        }

        var lines = demand.Select(item =>
        {
            int? onHand = stock.TryGetValue(item.ProductId, out var quantity) ? quantity : null;
            int? shortBy = onHand is null ? null : Math.Max(0, item.Requested - onHand.Value);
            return new AvailabilityLine(item.ProductId, item.Requested, onHand, shortBy);
        }).ToArray();
        var status = lines.Any(line => line.OnHand is null) ? AvailabilityStatus.Unknown :
            lines.Any(line => line.ShortBy > 0) ? AvailabilityStatus.Short : AvailabilityStatus.Available;
        return new AvailabilityResult(order.SalesOrderId, warehouseId, status, lines);
    }
}
