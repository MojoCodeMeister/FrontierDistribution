using System.Collections.Concurrent;
using System.Data;
using Frontier.Identity.SqlServer;
using Frontier.Inventory.SqlServer;
using Frontier.Orders;
using Frontier.Orders.SqlServer;
using Frontier.Picking.SqlServer;
using Microsoft.Data.SqlClient;

var connectionString = Environment.GetEnvironmentVariable("FRONTIER_SQL_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("FRONTIER_SQL_CONNECTION_STRING is required.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
// Intentionally local-only. This demonstration has no authentication.
builder.WebHost.UseUrls("http://127.0.0.1:5187");
builder.Services.AddSqlProductIdentityResolver(connectionString);
builder.Services.AddSqlOrderIntake(connectionString);
builder.Services.AddSqlInventoryAvailability(connectionString);
builder.Services.AddSqlPickPlanStore(connectionString);
builder.Services.AddSingleton<ConcurrentDictionary<Guid, DemoSession>>();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/session", (ConcurrentDictionary<Guid, DemoSession> sessions) =>
{
    var state = new DemoSession(Guid.NewGuid(), "WORKBENCH-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant());
    sessions[state.Id] = state;
    return Results.Ok(View(state));
});

app.MapGet("/api/session/{id:guid}", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions) =>
    sessions.TryGetValue(id, out var state) ? Results.Ok(View(state)) : Results.NotFound(new { error = "Session not found; start a new demonstration" }));

app.MapPost("/api/session/{id:guid}/order", (Guid id, NewOrderRequest input, ConcurrentDictionary<Guid, DemoSession> sessions,
    SqlOrderIntake intake) => Execute(id, sessions, state =>
{
    if (state.Order is not null) return new("Order is already accepted; create another session for a new order", false);
    if (input.Lines is null || input.Lines.Length is < 1 or > 12 ||
        input.Lines.Any(x => x is null || string.IsNullOrWhiteSpace(x.Sku) || x.Quantity < 1 || x.Quantity > 100000) ||
        string.IsNullOrWhiteSpace(input.CustomerId) || string.IsNullOrWhiteSpace(input.ShipToLocationId))
        return new("Provide customer, ship-to, and 1–12 lines with positive quantities", false);
    var incoming = new IncomingOrder("nopCommerce", state.ExternalReference, input.CustomerId.Trim(),
        input.ShipToLocationId.Trim(), input.Lines.Select((x, i) =>
            new IncomingOrderLine((i + 1).ToString(), x.Sku.Trim(), x.Quantity)).ToArray());
    var result = intake.Receive(incoming);
    state.Events.Add($"ERP intake: {result.Status} — {result.Message}");
    if (result.Status != IntakeStatus.Created || result.Order is null) return new(result.Message, false);
    state.Incoming = incoming;
    state.Order = result.Order;
    state.CreatedBySession = true;
    return new($"ERP order {result.Order.SalesOrderId} persisted", true);
}));

app.MapPost("/api/session/{id:guid}/inventory", (Guid id, WarehouseRequest input,
    ConcurrentDictionary<Guid, DemoSession> sessions, SqlInventoryAvailability inventory) => Execute(id, sessions, state =>
{
    if (state.Order is null) return new("Accept an order first", false);
    if (state.Plan is not null) return new("Pick plan already generated; inventory assessment is frozen", false);
    if (string.IsNullOrWhiteSpace(input.WarehouseId)) return new("Warehouse is required", false);
    state.Availability = inventory.Assess(state.Order, input.WarehouseId.Trim());
    state.Events.Add($"ERP inventory: {state.Availability.Status} at {state.Availability.WarehouseId}");
    return new($"Inventory: {state.Availability.Status}", true);
}));

app.MapPost("/api/session/{id:guid}/plan", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions) => Execute(id, sessions, state =>
{
    if (state.Order is null || state.Availability is null) return new("Accept an order and assess inventory first", false);
    if (state.Plan is not null) return new("Pick plan already generated", false);
    state.Plan = new PickPlanning().Create(state.Order, state.Availability);
    state.Events.Add($"Voxware plan: {state.Plan.Status} (not yet persisted)");
    return new($"Pick plan: {state.Plan.Status}", true);
}));

app.MapPost("/api/session/{id:guid}/persist", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions,
    SqlPickPlanStore store) => Execute(id, sessions, state =>
{
    if (state.Plan is null) return new("Generate a pick plan first", false);
    var result = store.Save(state.Plan);
    state.Events.Add($"Voxware persistence: {result.Status} — {result.Message}");
    if (result.Status == PickPlanStoreStatus.Rejected) return new(result.Message, false);
    state.Persisted = true;
    return new($"Pick plan: {result.Status}", true);
}));

app.MapPost("/api/session/{id:guid}/replay", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions,
    SqlOrderIntake intake, SqlPickPlanStore store) => Execute(id, sessions, state =>
{
    if (state.Incoming is null || state.Plan is null || !state.Persisted) return new("Persist a pick plan first", false);
    var orderReplay = intake.Receive(state.Incoming);
    var planReplay = store.Save(state.Plan);
    var valid = orderReplay.Status == IntakeStatus.AlreadyReceived &&
        orderReplay.Order?.SalesOrderId == state.Order?.SalesOrderId &&
        planReplay.Status == PickPlanStoreStatus.AlreadyRecorded;
    state.Events.Add($"Replay: order={orderReplay.Status}, plan={planReplay.Status}");
    return new(valid ? "Both ERP and Voxware replay without duplicates" : "Replay mismatch", valid);
}));

app.MapGet("/api/session/{id:guid}/records", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions) =>
{
    if (!sessions.TryGetValue(id, out var state)) return Results.NotFound(new { error = "Session not found" });
    lock (state.Sync)
    {
        if (state.Order is null) return Results.BadRequest(new { error = "Accept an order first" });
        try { return Results.Ok(new { session = View(state), records = ReadRecords(connectionString, state.Order.SalesOrderId) }); }
        catch (Exception e) { return Results.Problem(e.Message, statusCode: 500); }
    }
});

app.MapPost("/api/session/{id:guid}/cleanup", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions) => Execute(id, sessions, state =>
{
    if (!state.CreatedBySession || state.Order is null) return new("No session-owned order to remove", false);
    var orderId = state.Order.SalesOrderId;
    using var sql = new SqlConnection(connectionString);
    sql.Open();
    using var tx = sql.BeginTransaction();
    using var cmd = new SqlCommand("""
        IF NOT EXISTS (SELECT 1 FROM erp.SalesOrder
            WHERE SalesOrderId=@id AND ExternalOrderId=@external AND SourceSystem='nopCommerce')
            THROW 50010, 'Demo order ownership does not match; cleanup refused', 1;
        DELETE FROM voxware.PickPlanLine WHERE SalesOrderId=@id;
        DELETE FROM voxware.PickPlan WHERE SalesOrderId=@id;
        DELETE FROM erp.SalesOrderLine WHERE SalesOrderId=@id;
        DELETE FROM erp.SalesOrder WHERE SalesOrderId=@id AND ExternalOrderId=@external;
        """, sql, tx);
    cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = orderId;
    cmd.Parameters.Add("@external", SqlDbType.VarChar, 100).Value = state.ExternalReference;
    cmd.ExecuteNonQuery();
    tx.Commit();
    state.Events.Add($"Removed demo-owned SQL rows for {orderId}");
    state.Order = null;
    state.Incoming = null;
    state.Availability = null;
    state.Plan = null;
    state.Persisted = false;
    state.CreatedBySession = false;
    return new("Demo records removed; seed records unchanged", true);
}));

app.Run();

static IResult Execute(Guid id, ConcurrentDictionary<Guid, DemoSession> sessions, Func<DemoSession, Outcome> operation)
{
    if (!sessions.TryGetValue(id, out var state)) return Results.NotFound(new { error = "Session not found; create a new demonstration" });
    lock (state.Sync)
    {
        try
        {
            var outcome = operation(state);
            return Results.Ok(new { ok = outcome.Ok, message = outcome.Message, session = View(state) });
        }
        catch (Exception e)
        {
            state.Events.Add("Error: " + e.Message);
            return Results.Json(new { ok = false, message = e.Message, session = View(state) }, statusCode: 500);
        }
    }
}

static object View(DemoSession s) => new
{
    id = s.Id, reference = s.ExternalReference, order = s.Order,
    availability = s.Availability, plan = s.Plan, persisted = s.Persisted,
    events = s.Events.ToArray()
};

static object ReadRecords(string connectionString, string orderId)
{
    using var sql = new SqlConnection(connectionString);
    sql.Open();
    var orders = new List<object>();
    var orderLines = new List<object>();
    var plans = new List<object>();
    var planLines = new List<object>();
    using (var cmd = new SqlCommand("SELECT SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status FROM erp.SalesOrder WHERE SalesOrderId=@id", sql))
    {
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = orderId;
        using var r = cmd.ExecuteReader();
        while (r.Read()) orders.Add(new { salesOrderId = r.GetString(0), sourceSystem = r.GetString(1), externalOrderId = r.GetString(2), customerId = r.GetString(3), shipTo = r.GetString(4), status = r.GetString(5) });
    }
    using (var cmd = new SqlCommand("SELECT SourceLineId, SourceSku, ProductId, Quantity FROM erp.SalesOrderLine WHERE SalesOrderId=@id ORDER BY LineSequence", sql))
    {
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = orderId;
        using var r = cmd.ExecuteReader();
        while (r.Read()) orderLines.Add(new { sourceLineId = r.GetString(0), sku = r.GetString(1), productId = r.GetString(2), quantity = r.GetInt32(3) });
    }
    using (var cmd = new SqlCommand("SELECT WarehouseId, Status FROM voxware.PickPlan WHERE SalesOrderId=@id", sql))
    {
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = orderId;
        using var r = cmd.ExecuteReader();
        while (r.Read()) plans.Add(new { warehouseId = r.GetString(0), status = r.GetString(1) });
    }
    using (var cmd = new SqlCommand("SELECT WarehouseId, ProductId, Quantity FROM voxware.PickPlanLine WHERE SalesOrderId=@id ORDER BY ProductId", sql))
    {
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = orderId;
        using var r = cmd.ExecuteReader();
        while (r.Read()) planLines.Add(new { warehouseId = r.GetString(0), productId = r.GetString(1), quantity = r.GetInt32(2) });
    }
    return new { erp = new { orders, orderLines }, voxware = new { plans, planLines } };
}

sealed record OrderLineRequest(string Sku, int Quantity);
sealed record NewOrderRequest(string CustomerId, string ShipToLocationId, OrderLineRequest[] Lines);
sealed record WarehouseRequest(string WarehouseId);
sealed record Outcome(string Message, bool Ok);
sealed class DemoSession(Guid id, string externalReference)
{
    public Guid Id { get; } = id;
    public string ExternalReference { get; } = externalReference;
    public object Sync { get; } = new();
    public IncomingOrder? Incoming { get; set; }
    public SalesOrder? Order { get; set; }
    public AvailabilityResult? Availability { get; set; }
    public PickPlan? Plan { get; set; }
    public bool Persisted { get; set; }
    public bool CreatedBySession { get; set; }
    public List<string> Events { get; } = new();
}
