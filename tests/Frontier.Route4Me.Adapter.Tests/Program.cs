using System.Text.Json;
using Frontier.Orders.SqlServer;

int checks = 0, failures = 0;
void Check(string title, bool ok) { checks++; if (!ok) failures++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {title}"); }
var request = new Route4MeDispatchRequest("REQ-123", "DEP-123", "SO-123", "WH-1", "PACK-1",
    new [] { new Route4MeDispatchLine("ITEM-1", 3) },
    new Route4MeDeliveryDestination("LOC-1", "100 Example Dr", null, "Oxford", "MI", "48371", "US", "Example Contact", "5551234567"));
using var json = JsonDocument.Parse(Route4MeV5Gateway.PreviewCreateOrderJson(request));
Check("v5 address mapping", json.RootElement.GetProperty("address_1").GetString() == "100 Example Dr");
Check("ERP stable correlation in alias", json.RootElement.GetProperty("address_alias").GetString() == "REQ-123");
Check("v5 documented ZIP field", json.RootElement.GetProperty("address_zip").GetString() == "48371");
Check("No unverified numeric country mapping", !json.RootElement.TryGetProperty("address_country_id", out _));
Check("No invented vendor line-item payload", !json.RootElement.TryGetProperty("lines", out _));
using var http = new HttpClient(new NoNetworkHandler());
var adapter = new Route4MeV5Gateway(http);
var status = await adapter.ReconcileAsync("REQ-123", CancellationToken.None);
Check("Remote lookup disabled by default", status.State == RemoteDispatchState.Uncertain);
var submit = await adapter.SubmitAsync(request, CancellationToken.None);
Check("Remote write disabled", submit.State == RemoteDispatchState.Uncertain);
using var found = JsonDocument.Parse("""{"data":[{"order_id":42,"address_alias":"REQ-123"}]}""");
Check("Exact match reconciled", Route4MeV5Gateway.InterpretLookup(found.RootElement, "REQ-123") is {State: RemoteDispatchState.Accepted, ExternalShipmentId: "42"});
using var mismatch = JsonDocument.Parse("""{"data":[{"order_id":42,"address_alias":"REQ-1234"}]}""");
Check("Substring search not accepted", Route4MeV5Gateway.InterpretLookup(mismatch.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
using var empty = JsonDocument.Parse("""{"data":[]}""");
Check("Empty search inconclusive", Route4MeV5Gateway.InterpretLookup(empty.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
using var duplicate = JsonDocument.Parse("""{"data":[{"order_id":42,"address_alias":"REQ-123"},{"order_id":43,"address_alias":"REQ-123"}]}""");
Check("Duplicate matches inconclusive", Route4MeV5Gateway.InterpretLookup(duplicate.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
using var malformed = JsonDocument.Parse("""{"error":"internal"}""");
Check("Malformed data inconclusive", Route4MeV5Gateway.InterpretLookup(malformed.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
Check("No network attempted", NoNetworkHandler.Calls == 0);
Console.WriteLine($"RESULT: {checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;
sealed class NoNetworkHandler : HttpMessageHandler
{
    public static int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; throw new InvalidOperationException("Network must remain disabled"); }
}
