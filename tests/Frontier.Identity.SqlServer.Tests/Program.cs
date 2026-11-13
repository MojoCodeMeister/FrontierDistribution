using Frontier.Contracts;
using Frontier.Identity.SqlServer;
using Microsoft.Extensions.DependencyInjection;

var connectionString = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("FAIL: FRONTIER_SQL_CONNECTION_STRING is required; no SQL verification was performed.");
    return 2;
}

var services = new ServiceCollection();
services.AddSqlProductIdentityResolver(connectionString);
using var provider = services.BuildServiceProvider(validateScopes: true);
using var scope = provider.CreateScope();
var resolver = scope.ServiceProvider.GetRequiredService<IProductIdentityResolver>();
var checks = 0;
var failures = 0;
void Check(string name, bool passed)
{
    checks++;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
    if (!passed) failures++;
}
static bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
}

Check("DI resolves SQL implementation", resolver is SqlProductIdentityResolver);
Check("Null query rejected", Throws<ArgumentNullException>(() => resolver.Resolve(null!)));
Check("Blank system rejected", Throws<ArgumentException>(() => resolver.Resolve(new(" ", "ItemNumber", "ITEM-000142"))));
Check("Blank type rejected", Throws<ArgumentException>(() => resolver.Resolve(new("ERP", " ", "ITEM-000142"))));
Check("Blank identifier rejected", Throws<ArgumentException>(() => resolver.Resolve(new("ERP", "ItemNumber", " "))));

try
{
    var erp = resolver.Resolve(new("ERP", "ItemNumber", "ITEM-000142"));
    var web = resolver.Resolve(new("nopCommerce", "SKU", "FD-DOG-0142"));
    Check("ERP identity Found", erp.Status == ResolveStatus.Found && erp.Product?.ProductId == "P000142");
    Check("Webstore identity resolves same ERP product", web.Status == ResolveStatus.Found && web.Product?.ProductId == erp.Product?.ProductId);
    Check("Product has description and base unit", erp.Product is { Description.Length: > 0, BaseUnit.Length: > 0 });
    Check("Case-insensitive lookup", resolver.Resolve(new("erp", "itemnumber", "item-000142")).Product?.ProductId == "P000142");
    var missing = resolver.Resolve(new("ERP", "ItemNumber", "DOES-NOT-EXIST"));
    Check("Unknown identity NotFound", missing.Status == ResolveStatus.NotFound && missing.Product is null && missing.CandidateProductIds.Count == 0);
    var ambiguous = resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01"));
    Check("Omitted context Ambiguous", ambiguous.Status == ResolveStatus.Ambiguous && ambiguous.Product is null && ambiguous.CandidateProductIds.SequenceEqual(new[] { "P000142", "P000143" }));
    Check("Context SUP-1 selects product", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01", "SUP-1")).Product?.ProductId == "P000142");
    Check("Context SUP-2 selects product", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01", "SUP-2")).Product?.ProductId == "P000143");
    Check("Context comparison is case-insensitive", resolver.Resolve(new("supplier", "itemnumber", "shared-01", "sup-2")).Product?.ProductId == "P000143");
    Check("Wrong context returns NotFound", resolver.Resolve(new("Supplier", "ItemNumber", "SHARED-01", "SUP-UNKNOWN")).Status == ResolveStatus.NotFound);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL SQL execution: {exception}");
    return 1;
}

Console.WriteLine($"RESULT: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
