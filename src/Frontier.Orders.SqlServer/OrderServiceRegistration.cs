using Microsoft.Extensions.DependencyInjection;

namespace Frontier.Orders.SqlServer;

/// <summary>ERP-owned order intake registration. Register IProductIdentityResolver separately.</summary>
public static class OrderServiceRegistration
{
    public static IServiceCollection AddSqlOrderIntake(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddScoped<SqlOrderIntake>(sp => new SqlOrderIntake(
            connectionString, sp.GetRequiredService<Frontier.Contracts.IProductIdentityResolver>()));
        return services;
    }
}
