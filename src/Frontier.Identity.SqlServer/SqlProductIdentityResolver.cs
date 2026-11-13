using System.Data;
using Frontier.Contracts;
using Frontier.Domain;
using Microsoft.Data.SqlClient;

namespace Frontier.Identity.SqlServer;

/// <summary>ERP-owned persistence implementation of the existing identity contract.</summary>
public sealed class SqlProductIdentityResolver : IProductIdentityResolver
{
    private readonly string _connectionString;

    public SqlProductIdentityResolver(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public ProductResolution Resolve(ProductIdentityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.System) ||
            string.IsNullOrWhiteSpace(query.IdentifierType) ||
            string.IsNullOrWhiteSpace(query.ExternalId))
            throw new ArgumentException("System, IdentifierType and ExternalId are required.", nameof(query));

        // A null Context means ANY context. A supplied Context restricts matching.
        // DISTINCT ProductId is essential: multiple scoped identities may point at
        // the same canonical product without making the result ambiguous.
        const string sql = """
            SELECT DISTINCT p.ProductId, p.Description, p.BaseUnit
            FROM erp.ProductIdentity AS i
            INNER JOIN erp.Product AS p ON p.ProductId = i.ProductId
            WHERE i.[System] = @system
              AND i.IdentifierType = @type
              AND i.ExternalId = @externalId
              AND (@context IS NULL OR i.[Context] = @context)
            ORDER BY p.ProductId;
            """;

        var matches = new List<Product>();
        using var connection = new SqlConnection(_connectionString);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@system", SqlDbType.VarChar, 80).Value = query.System;
        command.Parameters.Add("@type", SqlDbType.VarChar, 80).Value = query.IdentifierType;
        command.Parameters.Add("@externalId", SqlDbType.VarChar, 150).Value = query.ExternalId;
        command.Parameters.Add("@context", SqlDbType.VarChar, 150).Value = (object?)query.Context ?? DBNull.Value;
        connection.Open();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                matches.Add(new Product(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var ids = matches.Select(p => p.ProductId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ids.Length switch
        {
            0 => ProductResolution.Missing(),
            1 => new ProductResolution(ResolveStatus.Found, matches.Single(), ids),
            _ => new ProductResolution(ResolveStatus.Ambiguous, null, ids)
        };
    }
}
