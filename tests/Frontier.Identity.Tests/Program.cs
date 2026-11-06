using Frontier.Contracts;
using Frontier.Domain;
using Frontier.Identity;

var catalog = IdentityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "synthetic-identities.json"));
var failures = 0;
var checks = 0;
void Check(string name, bool passed)
{
    checks++;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
    if (!passed) failures++;
}
Check("No invalid references", catalog.Validate().Count == 0);
var resolver = new ProductIdentityResolver(catalog);
var erp = resolver.Resolve(new("ERP", "ItemNumber", "ITEM-000142"));
var web = resolver.Resolve(new("nopCommerce", "SKU", "FD-DOG-0142"));
Check("ERP identifier resolves", erp.Status == ResolveStatus.Found && erp.Product?.ProductId == "P000142");
Check("Webstore identifier resolves same canonical product", web.Status == ResolveStatus.Found && web.Product?.ProductId == erp.Product?.ProductId);
Check("Unknown identifier is NotFound", resolver.Resolve(new("ERP", "ItemNumber", "BAD")).Status == ResolveStatus.NotFound);
Check("Supplier-context ambiguity surfaced", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01")).Status == ResolveStatus.Ambiguous);
Check("Context disambiguates", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01", "SUP-1")).Product?.ProductId == "P000142");
Check("Multiple suppliers for one product", catalog.SupplierProducts.Count(x => x.ProductId == "P000142") == 2);
Check("Multiple locations for one customer", catalog.CustomerLocations.Count(x => x.CustomerId == "C0001") == 2);
Check("Warehouse-product references intact", catalog.InventoryPositions.Count > 0 && catalog.Validate().Count == 0);
var bad = IdentityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "synthetic-identities.json"));
bad.CustomerLocations.Add(new("BAD-LOC", "NO-CUSTOMER", "Invalid"));
Check("Orphan references rejected", bad.Validate().Any(x => x.Contains("Orphan customer location")));

// Stage 1 hardening: tests for previously unasserted contract invariants.
static bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
}

var duplicateScope = IdentityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "synthetic-identities.json"));
duplicateScope.ProductIdentities.Add(new("P000142", "ERP", "ItemNumber", "ITEM-000142"));
Check("Duplicate fully scoped identity rejected", duplicateScope.Validate().Any(e => e.Contains("Duplicate scoped product identity")));

var conflictingScope = IdentityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "synthetic-identities.json"));
conflictingScope.ProductIdentities.Add(new("P000142", "Supplier", "ItemNumber", "SHARED-01", "SUP-2"));
Check("Conflicting fully scoped identity rejected", conflictingScope.Validate().Any(e => e.Contains("Duplicate scoped product identity")));
Check("Conflicting catalog cannot construct resolver", Throws<InvalidDataException>(() => new ProductIdentityResolver(conflictingScope)));

var mutableCatalog = IdentityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "synthetic-identities.json"));
var snapshotResolver = new ProductIdentityResolver(mutableCatalog);
mutableCatalog.ProductIdentities.Clear();
mutableCatalog.Products.Clear();
Check("Resolver retains validated snapshot", snapshotResolver.Resolve(new("ERP", "ItemNumber", "ITEM-000142")).Product?.ProductId == "P000142");

Check("Malformed empty system throws", Throws<ArgumentException>(() => resolver.Resolve(new("", "ItemNumber", "ITEM-000142"))));
Check("Malformed empty type throws", Throws<ArgumentException>(() => resolver.Resolve(new("ERP", " ", "ITEM-000142"))));
Check("Malformed empty identifier throws", Throws<ArgumentException>(() => resolver.Resolve(new("ERP", "ItemNumber", ""))));
Check("Null query throws", Throws<ArgumentNullException>(() => resolver.Resolve(null!)));
Check("Partially scoped supplier lookup remains ambiguous", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01")).CandidateProductIds.Count == 2);
Check("Context lookup remains selective", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01", "SUP-2")).Status == ResolveStatus.Found);

Console.WriteLine($"RESULT: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
