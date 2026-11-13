using Microsoft.Extensions.DependencyInjection;

namespace Frontier.Picking.SqlServer;

/// <summary>Voxware-owned persistence registration; ERP availability is a separate dependency.</summary>
public static class PickingServiceRegistration
{
    public static IServiceCollection AddSqlPickPlanStore(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddScoped(_ => new SqlPickPlanStore(connectionString));
        return services;
    }
}
