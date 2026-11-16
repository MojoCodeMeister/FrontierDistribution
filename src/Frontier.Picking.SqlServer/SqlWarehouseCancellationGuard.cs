using System.Data;
using Frontier.Orders.SqlServer;
using Microsoft.Data.SqlClient;

namespace Frontier.Picking.SqlServer;

/// <summary>
/// Voxware-owned eligibility evidence for ERP cancellation. A pick plan, even if not yet
/// confirmed, blocks simple cancellation. UPDLOCK/HOLDLOCK protects the empty-key range
/// from concurrent pick-plan inserts until the enclosing ERP transaction commits.
/// </summary>
public sealed class SqlWarehouseCancellationGuard : IWarehouseCancellationGuard
{
    public bool HasWarehouseActivity(SqlConnection connection, SqlTransaction transaction,
        string salesOrderId, string warehouseId)
    {
        using var command = new SqlCommand("""
            SELECT COUNT(*) FROM voxware.PickPlan WITH (UPDLOCK, HOLDLOCK)
            WHERE SalesOrderId=@order AND WarehouseId=@wh;
            """, connection, transaction);
        command.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = salesOrderId;
        command.Parameters.Add("@wh", SqlDbType.VarChar, 64).Value = warehouseId;
        return (int)command.ExecuteScalar()! > 0;
    }
}
