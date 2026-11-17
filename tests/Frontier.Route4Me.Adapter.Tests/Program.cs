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
using var complete = JsonDocument.Parse("""{"data":[{"order_id":42,"address_alias":"REQ-123"}],"meta":{"current_page":1,"last_page":1,"total":1,"per_page":100}}""");
Check("Complete one-page exact match accepted", Route4MeV5Gateway.InterpretCompleteLookup(complete.RootElement, "REQ-123") is { State: RemoteDispatchState.Accepted, ExternalShipmentId: "42" });
Check("Missing pagination metadata inconclusive", Route4MeV5Gateway.InterpretCompleteLookup(found.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
using var multipage = JsonDocument.Parse("""{"data":[{"order_id":42,"address_alias":"REQ-123"}],"meta":{"current_page":1,"last_page":2,"total":2,"per_page":1}}""");
Check("Additional unseen page inconclusive", Route4MeV5Gateway.InterpretCompleteLookup(multipage.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
using var inconsistent = JsonDocument.Parse("""{"data":[{"order_id":42,"address_alias":"REQ-123"}],"meta":{"current_page":1,"last_page":1,"total":2,"per_page":100}}""");
Check("Inconsistent search count inconclusive", Route4MeV5Gateway.InterpretCompleteLookup(inconsistent.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
using var completeDuplicate = JsonDocument.Parse("""{"data":[{"order_id":42,"address_alias":"REQ-123"},{"order_id":43,"address_alias":"REQ-123"}],"meta":{"current_page":1,"last_page":1,"total":2,"per_page":100}}""");
Check("Complete duplicate matches inconclusive", Route4MeV5Gateway.InterpretCompleteLookup(completeDuplicate.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
using var completeEmpty = JsonDocument.Parse("""{"data":[],"meta":{"current_page":1,"last_page":1,"total":0,"per_page":100}}""");
Check("Complete zero matches remain inconclusive", Route4MeV5Gateway.InterpretCompleteLookup(completeEmpty.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
using var conflicting = JsonDocument.Parse("""{"data":[{"order_id":42,"address_alias":"REQ-123","external_connection_object_id":"REQ-OTHER"}],"meta":{"current_page":1,"last_page":1,"total":1,"per_page":100}}""");
Check("Conflicting external identity blocks acceptance", Route4MeV5Gateway.InterpretCompleteLookup(conflicting.RootElement, "REQ-123").State == RemoteDispatchState.Uncertain);
using var agreeing = JsonDocument.Parse("""{"data":[{"order_id":42,"address_alias":"REQ-123","external_connection_object_id":"req-123"}],"meta":{"current_page":1,"last_page":1,"total":1,"per_page":100}}""");
Check("Agreeing remote identifiers accepted", Route4MeV5Gateway.InterpretCompleteLookup(agreeing.RootElement, "REQ-123").State == RemoteDispatchState.Accepted);
var fake = new RecordingHandler("""{"data":[{"order_id":88,"address_alias":"REQ-123"}],"meta":{"current_page":1,"last_page":1,"total":1,"per_page":100}}""");
using var fakeHttp = new HttpClient(fake);
var enabled = new Route4MeV5Gateway(fakeHttp, apiKey: "test-credential", enableReadOnlyLookup: true);
var fetched = await enabled.ReconcileAsync("REQ-123", CancellationToken.None);
Check("Enabled read-only lookup uses POST", fake.Method == HttpMethod.Post && fake.Uri?.AbsolutePath == "/modules/api/v5.0/orders/list");
Check("Enabled lookup sends stable search identity", fake.RequestBody?.Contains("REQ-123", StringComparison.Ordinal) == true);
Check("Enabled lookup accepts one complete exact result", fetched is { State: RemoteDispatchState.Accepted, ExternalShipmentId: "88" });
var denied = new Route4MeV5Gateway(fakeHttp, enableReadOnlyLookup: true);
var noCredential = await denied.ReconcileAsync("REQ-123", CancellationToken.None);
Check("Enabled lookup without credential remains inconclusive", noCredential.State == RemoteDispatchState.Uncertain && fake.Calls == 1);
var failedHttp = new RecordingHandler("""{"error":"service unavailable"}""", System.Net.HttpStatusCode.ServiceUnavailable);
using var failedClient = new HttpClient(failedHttp);
var failedResult = await new Route4MeV5Gateway(failedClient, "test-credential", true).ReconcileAsync("REQ-123", CancellationToken.None);
Check("HTTP failure remains inconclusive", failedResult.State == RemoteDispatchState.Uncertain);
var malformedHttp = new RecordingHandler("not-json");
using var malformedClient = new HttpClient(malformedHttp);
var malformedResult = await new Route4MeV5Gateway(malformedClient, "test-credential", true).ReconcileAsync("REQ-123", CancellationToken.None);
Check("Malformed HTTP response remains inconclusive", malformedResult.State == RemoteDispatchState.Uncertain);
Check("No network attempted", NoNetworkHandler.Calls == 0);
Console.WriteLine($"RESULT: {checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;
sealed class NoNetworkHandler : HttpMessageHandler
{
    public static int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; throw new InvalidOperationException("Network must remain disabled"); }
}

sealed class RecordingHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly System.Net.HttpStatusCode _status;
    public int Calls { get; private set; }
    public HttpMethod? Method { get; private set; }
    public Uri? Uri { get; private set; }
    public string? RequestBody { get; private set; }
    public RecordingHandler(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    { _body = body; _status = status; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Method = request.Method;
        Uri = request.RequestUri;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
    }
}
