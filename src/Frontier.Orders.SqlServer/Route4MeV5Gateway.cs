using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Frontier.Orders.SqlServer;

/// <summary>
/// Q25.5d3: Route4Me v5 order API integration boundary. Remote creation is deliberately disabled.
/// An order create API is not evidence of server-side idempotency or uniqueness.
/// </summary>
public sealed class Route4MeV5Gateway : IRoute4MeDispatchGateway
{
    public const string OrdersEndpoint = "https://wh.route4me.com/modules/api/v5.0/orders";
    public const string ListEndpoint = "https://wh.route4me.com/modules/api/v5.0/orders/list";

    private readonly HttpClient _client;
    private readonly string? _apiKey;
    private readonly bool _enableReadOnlyLookup;

    public Route4MeV5Gateway(HttpClient client, string? apiKey = null, bool enableReadOnlyLookup = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _apiKey = apiKey;
        _enableReadOnlyLookup = enableReadOnlyLookup;
    }

    /// <summary>
    /// Preview the vendor-documented address and correlation mapping without an HTTP request.
    /// The alias is a search aid, NOT a verified remote uniqueness constraint.
    /// </summary>
    public static string PreviewCreateOrderJson(Route4MeDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DispatchRequestId) ||
            string.IsNullOrWhiteSpace(request.Destination.Address1) ||
            string.IsNullOrWhiteSpace(request.Destination.City) ||
            string.IsNullOrWhiteSpace(request.Destination.Region) ||
            string.IsNullOrWhiteSpace(request.Destination.PostalCode) ||
            request.Lines.Count == 0 || request.Lines.Any(x => string.IsNullOrWhiteSpace(x.ProductId) || x.Quantity <= 0))
            throw new ArgumentException("Complete ERP shipment identity, destination and positive quantities required", nameof(request));

        // Only documented v5 Create Order parameters. CountryCode and individual line items
        // remain ERP-owned; v5 address_country_id expects a vendor numeric ID, not ISO code.
        var destination = request.Destination;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["address_1"] = destination.Address1,
            ["address_2"] = destination.Address2,
            ["address_alias"] = request.DispatchRequestId,
            ["address_city"] = destination.City,
            ["address_state_id"] = destination.Region,
            ["address_zip"] = destination.PostalCode,
            ["EXT_FIELD_first_name"] = destination.ContactName,
            ["EXT_FIELD_phone"] = destination.ContactPhone
        });
    }

    /// <summary>
    /// Read-only lookup must not claim NotFound without authoritative completeness.
    /// Search results can be partial or eventually consistent; zero matches means Uncertain.
    /// </summary>
    public async Task<RemoteDispatchResult> ReconcileAsync(string dispatchRequestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchRequestId);
        if (!_enableReadOnlyLookup)
            return new(RemoteDispatchState.Uncertain, Error: "Route4Me v5 lookup disabled; no remote calls made");
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new(RemoteDispatchState.Uncertain, Error: "Route4Me API credential unavailable");

        try
        {
            var uri = ListEndpoint + "?api_key=" + Uri.EscapeDataString(_apiKey);
            using var message = new HttpRequestMessage(HttpMethod.Post, uri);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Content = new StringContent(JsonSerializer.Serialize(new
            {
                page = 1, per_page = 100, search_query = dispatchRequestId
            }), Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(RemoteDispatchState.Uncertain, Error: "Route4Me lookup HTTP " + (int)response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return InterpretCompleteLookup(document.RootElement, dispatchRequestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return new(RemoteDispatchState.Uncertain, Error: "Route4Me lookup inconclusive: " + ex.GetType().Name);
        }
    }

    public Task<RemoteDispatchResult> SubmitAsync(Route4MeDispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // Do not perform a POST. The source contract does not establish atomic create idempotency.
        return Task.FromResult(new RemoteDispatchResult(RemoteDispatchState.Uncertain,
            Error: "Live Route4Me order creation disabled until unique external correlation and recovery semantics are verified"));
    }

    /// <summary>
    /// Q25.5d4: Accept a v5 lookup only when its pagination metadata proves a complete
    /// one-page result. Search is not a uniqueness guarantee, even for exact matches.
    /// Missing, inconsistent or multi-page metadata is inconclusive; never submit based on it.
    /// </summary>
    public static RemoteDispatchResult InterpretCompleteLookup(JsonElement root, string dispatchRequestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchRequestId);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !TryInteger(meta, "current_page", out var currentPage) ||
            !TryInteger(meta, "last_page", out var lastPage) ||
            !TryInteger(meta, "total", out var total) ||
            !TryInteger(meta, "per_page", out var perPage) ||
            currentPage != 1 || lastPage != 1 || perPage <= 0 ||
            total < 0 || total != data.GetArrayLength() || total > perPage)
            return new(RemoteDispatchState.Uncertain,
                Error: "Route4Me lookup pagination is absent, incomplete or inconsistent");
        return InterpretLookup(root, dispatchRequestId);
    }

    private static bool TryInteger(JsonElement obj, string property, out int value)
    {
        value = default;
        return obj.TryGetProperty(property, out var field) &&
               field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value);
    }

    /// <summary>Analyze an already retrieved v5 List Orders response; suitable for offline contract tests.</summary>
    public static RemoteDispatchResult InterpretLookup(JsonElement root, string dispatchRequestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchRequestId);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new(RemoteDispatchState.Uncertain, Error: "Unexpected Route4Me list response");
        var matches = new List<string>();
        foreach (var order in data.EnumerateArray())
        {
            if (order.ValueKind != JsonValueKind.Object) continue;
            // Search matches are broad; accept only exact ERP correlation in returned fields.
            // If both correlation fields are supplied, they must agree. A matching alias
            // cannot override a contradictory external-system identifier (or vice versa).
            bool hasExternal = TryCorrelation(order, "external_connection_object_id", out var external);
            bool hasAlias = TryCorrelation(order, "address_alias", out var alias);
            bool externalExact = hasExternal && string.Equals(external, dispatchRequestId, StringComparison.OrdinalIgnoreCase);
            bool aliasExact = hasAlias && string.Equals(alias, dispatchRequestId, StringComparison.OrdinalIgnoreCase);
            if ((externalExact || aliasExact) && hasExternal && hasAlias &&
                !string.Equals(external, alias, StringComparison.OrdinalIgnoreCase))
                return new(RemoteDispatchState.Uncertain, Error: "Route4Me order has conflicting ERP correlation fields");
            bool exact = externalExact || aliasExact;
            if (!exact) continue;
            if (!order.TryGetProperty("order_id", out var id) ||
                (id.ValueKind != JsonValueKind.Number && id.ValueKind != JsonValueKind.String))
                return new(RemoteDispatchState.Uncertain, Error: "Matched order lacks usable vendor order ID");
            var remote = id.ToString();
            if (string.IsNullOrWhiteSpace(remote))
                return new(RemoteDispatchState.Uncertain, Error: "Matched order has empty vendor order ID");
            matches.Add(remote);
        }
        if (matches.Count > 1)
            return new(RemoteDispatchState.Uncertain, Error: "Multiple exact Route4Me orders found; manual reconciliation required");
        if (matches.Count == 1)
            return new(RemoteDispatchState.Accepted, dispatchRequestId, matches[0]);
        return new(RemoteDispatchState.Uncertain,
            Error: "No exact match in Route4Me search; completeness and consistency not established");
    }

    private static bool TryCorrelation(JsonElement obj, string property, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(property, out var field) || field.ValueKind == JsonValueKind.Null)
            return false;
        if (field.ValueKind != JsonValueKind.String)
            return false;
        value = field.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
}
