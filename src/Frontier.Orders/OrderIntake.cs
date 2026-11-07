using Frontier.Contracts;
using Frontier.Domain;
using Frontier.Identity;

namespace Frontier.Orders;

// Candidate synthetic workflow, NOT a discovered Frontier integration contract.
// CustomerId is the model's canonical ID; external customer translation is not implemented.
public sealed record IncomingOrder(string SourceSystem, string ExternalOrderId, string CustomerId,
    string ShipToLocationId, IReadOnlyList<IncomingOrderLine> Lines);
public sealed record IncomingOrderLine(string SourceLineId, string Sku, int Quantity);
public sealed record SalesOrderLine(string SourceLineId, string ProductId, int Quantity);
public sealed record SalesOrder(string SalesOrderId, string SourceSystem, string ExternalOrderId,
    string CustomerId, string ShipToLocationId, string Status, IReadOnlyList<SalesOrderLine> Lines);

public enum IntakeStatus { Created, AlreadyReceived, Rejected }
public sealed record IntakeResult(IntakeStatus Status, SalesOrder? Order, string Message);

/// <summary>In-memory sample. The repository is intentionally ephemeral; no durable order processing is implied.</summary>
public sealed class OrderIntake
{
    private readonly IProductIdentityResolver _resolver;
    private readonly HashSet<string> _customers;
    private readonly Dictionary<string, string> _locationCustomers;
    private readonly Dictionary<(string Source, string ExternalId), (IncomingOrder Request, SalesOrder Order)> _orders =
        new(new OrderKeyComparer());

    public OrderIntake(IdentityCatalog catalog, IProductIdentityResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        var errors = catalog.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join("; ", errors));
        _customers = catalog.Customers.Select(c => c.CustomerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _locationCustomers = catalog.CustomerLocations.ToDictionary(l => l.LocationId, l => l.CustomerId,
            StringComparer.OrdinalIgnoreCase);
    }

    public IntakeResult Receive(IncomingOrder? incoming)
    {
        if (incoming is null) return Reject("Order is required");
        if (string.IsNullOrWhiteSpace(incoming.SourceSystem) || string.IsNullOrWhiteSpace(incoming.ExternalOrderId))
            return Reject("Source system and external order ID are required");
        var key = (incoming.SourceSystem, incoming.ExternalOrderId);
        if (_orders.TryGetValue(key, out var previous))
            return Equivalent(previous.Request, incoming)
                ? new(IntakeStatus.AlreadyReceived, previous.Order, "Duplicate delivery: existing order returned")
                : Reject("External order reference already exists with different contents");
        if (string.IsNullOrWhiteSpace(incoming.CustomerId) || !_customers.Contains(incoming.CustomerId))
            return Reject($"Unknown canonical customer: {incoming.CustomerId}");
        if (string.IsNullOrWhiteSpace(incoming.ShipToLocationId) ||
            !_locationCustomers.TryGetValue(incoming.ShipToLocationId, out var locationCustomer) ||
            !string.Equals(locationCustomer, incoming.CustomerId, StringComparison.OrdinalIgnoreCase))
            return Reject("Shipping location does not belong to customer");
        if (incoming.Lines is null || incoming.Lines.Count == 0)
            return Reject("Order must contain lines");
        if (incoming.Lines.Any(l => l is null || string.IsNullOrWhiteSpace(l.SourceLineId) ||
            string.IsNullOrWhiteSpace(l.Sku) || l.Quantity <= 0))
            return Reject("Order line requires source line ID, SKU and positive quantity");
        if (incoming.Lines.Select(l => l.SourceLineId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != incoming.Lines.Count)
            return Reject("Duplicate source line IDs");

        var lines = new List<SalesOrderLine>();
        foreach (var line in incoming.Lines)
        {
            // The sample webstore uses a SKU. No ERP, pricing, availability or inventory inference.
            var product = _resolver.Resolve(new ProductIdentityQuery(incoming.SourceSystem, "SKU", line.Sku));
            if (product.Status != ResolveStatus.Found || product.Product is null)
                return Reject($"SKU {line.Sku}: {product.Status}");
            lines.Add(new SalesOrderLine(line.SourceLineId, product.Product.ProductId, line.Quantity));
        }
        var order = new SalesOrder($"SO-{_orders.Count + 1:0000}", incoming.SourceSystem,
            incoming.ExternalOrderId, incoming.CustomerId, incoming.ShipToLocationId,
            "Received", lines.AsReadOnly());
        // Store only after ALL checks succeed: no partially admitted orders.
        _orders.Add(key, (Snapshot(incoming), order));
        return new(IntakeStatus.Created, order, "Order received");
    }

    private static IntakeResult Reject(string reason) => new(IntakeStatus.Rejected, null, reason);

    private static IncomingOrder Snapshot(IncomingOrder x) => x with { Lines = x.Lines.Select(l => l with { }).ToArray() };

    private static bool Equivalent(IncomingOrder a, IncomingOrder b) =>
        string.Equals(a.CustomerId, b.CustomerId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ShipToLocationId, b.ShipToLocationId, StringComparison.OrdinalIgnoreCase) &&
        a.Lines is not null && b.Lines is not null && a.Lines.Count == b.Lines.Count &&
        a.Lines.Zip(b.Lines).All(p => p.First is not null && p.Second is not null &&
            string.Equals(p.First.SourceLineId, p.Second.SourceLineId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.First.Sku, p.Second.Sku, StringComparison.OrdinalIgnoreCase) &&
            p.First.Quantity == p.Second.Quantity);

    private sealed class OrderKeyComparer : IEqualityComparer<(string Source, string ExternalId)>
    {
        public bool Equals((string Source, string ExternalId) a, (string Source, string ExternalId) b) =>
            string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.ExternalId, b.ExternalId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Source, string ExternalId) x) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(x.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(x.ExternalId));
    }
}
