using System.Reflection;

[assembly: AssemblyMetadata("Frontier.BusinessApplication", "Route4Me")]
[assembly: AssemblyMetadata("Frontier.BusinessSystem", "Transportation / Distribution")]
[assembly: AssemblyMetadata("Frontier.BusinessProcess", "Dispatch-to-Delivery")]
[assembly: AssemblyMetadata("Frontier.BusinessComponent", "Delivery Execution and Confirmation")]
[assembly: AssemblyMetadata("Frontier.ImplementationComponent", "Frontier.Delivery")]
[assembly: AssemblyMetadata("Frontier.UpstreamApplication", "ERP")]
[assembly: AssemblyMetadata("Frontier.Integration", "ERP/Shipping -> Route4Me/Delivery")]

namespace Frontier.Delivery;

public static class ArchitectureContext
{
    // Uses framework-provided assembly metadata attributes; no dependencies or scanning framework.
    public static IReadOnlyDictionary<string, string> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var attributes = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key.StartsWith("Frontier.", StringComparison.Ordinal))
            .ToArray();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            if (string.IsNullOrWhiteSpace(attribute.Value) || !result.TryAdd(attribute.Key, attribute.Value))
                throw new InvalidOperationException($"Missing or duplicate architecture metadata: {attribute.Key}");
        }
        return result;
    }

    public static void Write(TextWriter output, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(output);
        var info = Discover(assembly);
        foreach (var key in new[] {
            "BusinessApplication", "BusinessSystem", "BusinessProcess", "BusinessComponent",
            "ImplementationComponent", "UpstreamApplication", "Integration" })
        {
            if (!info.TryGetValue("Frontier." + key, out var value))
                throw new InvalidOperationException($"Required architecture metadata absent: Frontier.{key}");
            output.WriteLine($"ARCHITECTURE {key}: {value}");
        }
    }
}
