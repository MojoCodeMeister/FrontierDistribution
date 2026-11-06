using Frontier.Contracts;
using Frontier.Domain;

namespace Frontier.Identity;

public sealed class ProductIdentityResolver : IProductIdentityResolver
{
    private readonly Product[] _products;
    private readonly ProductIdentity[] _identities;
    public ProductIdentityResolver(IdentityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var errors = catalog.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join("; ", errors));
        // Product and ProductIdentity are immutable records. Copying both collections
        // prevents caller-side catalog mutations from changing resolver behavior.
        _products = catalog.Products.ToArray();
        _identities = catalog.ProductIdentities.ToArray();
    }

    public ProductResolution Resolve(ProductIdentityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.System) || string.IsNullOrWhiteSpace(query.IdentifierType) || string.IsNullOrWhiteSpace(query.ExternalId))
            throw new ArgumentException("System, IdentifierType and ExternalId are required.", nameof(query));
        static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        // Context is optional for lookup. An omitted context intentionally may be ambiguous.
        var ids = _identities
            .Where(x => Eq(x.System, query.System) && Eq(x.IdentifierType, query.IdentifierType) && Eq(x.ExternalId, query.ExternalId))
            .Where(x => query.Context is null || Eq(x.Context, query.Context))
            .Select(x => x.ProductId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0) return ProductResolution.Missing();
        if (ids.Length > 1) return new ProductResolution(ResolveStatus.Ambiguous, null, ids);
        var product = _products.Single(x => Eq(x.ProductId, ids[0]));
        return new ProductResolution(ResolveStatus.Found, product, ids);
    }
}
