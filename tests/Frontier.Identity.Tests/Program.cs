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
Console.WriteLine($"RESULT: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
