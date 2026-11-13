using Microsoft.Extensions.DependencyInjection;

namespace Frontier.Inventory.SqlServer;

/// <summary>ERP-owned stock reader registration at the application's composition root.</summary>
public static class InventoryServiceRegistration
{
    public static IServiceCollection AddSqlInventoryAvailability(
        this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddScoped(_ => new SqlInventoryAvailability(connectionString));
        return services;
    }
}
