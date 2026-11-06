using System.Text.Json;
using Frontier.Domain;

namespace Frontier.Identity;

public sealed class IdentityCatalog
{
    public List<Product> Products { get; set; } = new();
    public List<Supplier> Suppliers { get; set; } = new();
    public List<SupplierProduct> SupplierProducts { get; set; } = new();
    public List<ProductIdentity> ProductIdentities { get; set; } = new();
    public List<Customer> Customers { get; set; } = new();
    public List<CustomerLocation> CustomerLocations { get; set; } = new();
    public List<Warehouse> Warehouses { get; set; } = new();
    public List<InventoryPosition> InventoryPositions { get; set; } = new();

    public static IdentityCatalog Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<IdentityCatalog>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("Empty identity catalog");
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        static void Unique(IEnumerable<string> ids, string kind, List<string> issues)
        {
            if (ids.Any(string.IsNullOrWhiteSpace)) issues.Add($"Blank {kind} identifier");
            foreach (var id in ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                issues.Add($"Duplicate {kind}: {id.Key}");
        }
        Unique(Products.Select(x => x.ProductId), "product", errors);
        Unique(Suppliers.Select(x => x.SupplierId), "supplier", errors);
        Unique(Customers.Select(x => x.CustomerId), "customer", errors);
        Unique(CustomerLocations.Select(x => x.LocationId), "customer location", errors);
        Unique(Warehouses.Select(x => x.WarehouseId), "warehouse", errors);
        var products = Products.Select(x => x.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suppliers = Suppliers.Select(x => x.SupplierId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customers = Customers.Select(x => x.CustomerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warehouses = Warehouses.Select(x => x.WarehouseId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var x in ProductIdentities)
        {
            if (!products.Contains(x.ProductId)) errors.Add($"Identity references unknown product {x.ProductId}");
            if (string.IsNullOrWhiteSpace(x.System) || string.IsNullOrWhiteSpace(x.IdentifierType) || string.IsNullOrWhiteSpace(x.ExternalId))
                errors.Add("Blank identity namespace/type/value");
        }
        // An external identity is unique only within its complete scope. The same
        // identifier without supplied context may legitimately be ambiguous.
        foreach (var group in ProductIdentities.GroupBy(
            x => (x.System, x.IdentifierType, x.ExternalId, x.Context),
            ProductIdentityScopeComparer.Instance).Where(g => g.Count() > 1))
        {
            errors.Add($"Duplicate scoped product identity: {group.Key.System}/{group.Key.IdentifierType}/{group.Key.ExternalId} (context: {group.Key.Context ?? "<none>"})");
        }
        foreach (var x in SupplierProducts)
        {
            if (!products.Contains(x.ProductId) || !suppliers.Contains(x.SupplierId)) errors.Add($"Orphan supplier product {x.SupplierId}/{x.ProductId}");
            if (x.BaseUnitsPerPurchaseUnit <= 0) errors.Add($"Invalid purchasing conversion for {x.ProductId}");
        }
        foreach (var x in CustomerLocations)
            if (!customers.Contains(x.CustomerId)) errors.Add($"Orphan customer location {x.LocationId}");
        foreach (var x in InventoryPositions)
            if (!warehouses.Contains(x.WarehouseId) || !products.Contains(x.ProductId)) errors.Add($"Orphan inventory position {x.WarehouseId}/{x.ProductId}");
        Unique(InventoryPositions.Select(x => $"{x.WarehouseId}|{x.ProductId}"), "inventory position", errors);
        return errors;
    }
}

// Scope is compared case-insensitively, matching resolver lookup semantics.
// Null context is distinct from an explicitly supplied context.
internal sealed class ProductIdentityScopeComparer : IEqualityComparer<(string System, string IdentifierType, string ExternalId, string? Context)>
{
    public static ProductIdentityScopeComparer Instance { get; } = new();
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public bool Equals(
        (string System, string IdentifierType, string ExternalId, string? Context) x,
        (string System, string IdentifierType, string ExternalId, string? Context) y) =>
        Comparer.Equals(x.System, y.System) &&
        Comparer.Equals(x.IdentifierType, y.IdentifierType) &&
        Comparer.Equals(x.ExternalId, y.ExternalId) &&
        Comparer.Equals(x.Context, y.Context);

    public int GetHashCode((string System, string IdentifierType, string ExternalId, string? Context) value)
    {
        var hash = new HashCode();
        hash.Add(value.System, Comparer);
        hash.Add(value.IdentifierType, Comparer);
        hash.Add(value.ExternalId, Comparer);
        hash.Add(value.Context, Comparer);
        return hash.ToHashCode();
    }
}
