using Frontier.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Frontier.Identity.SqlServer;

/// <summary>Called by the application's composition root; no global service provider.</summary>
public static class IdentityServiceRegistration
{
    public static IServiceCollection AddSqlProductIdentityResolver(
        this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddScoped<IProductIdentityResolver>(_ => new SqlProductIdentityResolver(connectionString));
        return services;
    }
}
