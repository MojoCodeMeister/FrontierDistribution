using System.Collections.Concurrent;
using System.Data;
using System.Text.Json.Serialization;
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
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Intentionally local-only. This demonstration has no authentication.
builder.WebHost.UseUrls("http://127.0.0.1:5187");
builder.Services.AddSqlProductIdentityResolver(connectionString);
builder.Services.AddSqlOrderIntake(connectionString);
builder.Services.AddSqlInventoryAvailability(connectionString);
builder.Services.AddSqlPickPlanStore(connectionString);
builder.Services.AddScoped(_ => new SqlPickConfirmationStore(connectionString));
builder.Services.AddScoped(_ => new SqlPackingConfirmationStore(connectionString));
builder.Services.AddScoped(_ => new SqlOrderCommitment(connectionString));
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
    if (state.Order is not null) return new("Order is already received; create another session for a new order", false);
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
    return new($"ERP order {result.Order.SalesOrderId} received (not yet accepted)", true);
}));

// Q24: ERP alone owns the acceptance decision and atomic stock reservations.
app.MapPost("/api/session/{id:guid}/commit", (Guid id, WarehouseRequest input,
    ConcurrentDictionary<Guid, DemoSession> sessions, SqlOrderCommitment commitment) => Execute(id, sessions, state =>
{
    if (state.Order is null) return new("Receive an ERP order first", false);
    if (state.Plan is not null) return new("Pick plan already generated", false);
    if (string.IsNullOrWhiteSpace(input.WarehouseId)) return new("Warehouse is required", false);
    var result = commitment.Accept(state.Order.SalesOrderId, input.WarehouseId.Trim());
    state.Commitment = result;
    state.Events.Add($"ERP commitment: {result.Status} — {result.Message}");
    state.Atp = ReadAtp(connectionString, state.Order.SalesOrderId, input.WarehouseId.Trim());
    return new($"ERP acceptance: {result.Status} — {result.Message}",
        result.Status is CommitmentStatus.Accepted or CommitmentStatus.AlreadyAccepted);
}));

app.MapPost("/api/session/{id:guid}/atp", (Guid id, WarehouseRequest input,
    ConcurrentDictionary<Guid, DemoSession> sessions) => Execute(id, sessions, state =>
{
    if (state.Order is null) return new("Receive an ERP order first", false);
    if (string.IsNullOrWhiteSpace(input.WarehouseId)) return new("Warehouse is required", false);
    state.Atp = ReadAtp(connectionString, state.Order.SalesOrderId, input.WarehouseId.Trim());
    state.Events.Add($"ERP ATP refreshed at {input.WarehouseId.Trim()}");
    return new("ERP available-to-promise refreshed", true);
}));

app.MapPost("/api/session/{id:guid}/inventory", (Guid id, WarehouseRequest input,
    ConcurrentDictionary<Guid, DemoSession> sessions, SqlInventoryAvailability inventory) => Execute(id, sessions, state =>
{
    if (state.Order is null) return new("Receive an order first", false);
    if (state.Plan is not null) return new("Pick plan already generated; inventory assessment is frozen", false);
    if (string.IsNullOrWhiteSpace(input.WarehouseId)) return new("Warehouse is required", false);
    if (state.Commitment?.Status is not (CommitmentStatus.Accepted or CommitmentStatus.AlreadyAccepted) ||
        !string.Equals(state.Commitment.WarehouseId, input.WarehouseId.Trim(), StringComparison.OrdinalIgnoreCase))
        return new("ERP must accept and reserve this order at the selected warehouse first", false);
    state.Availability = inventory.Assess(state.Order, input.WarehouseId.Trim());
    state.Atp = ReadAtp(connectionString, state.Order.SalesOrderId, input.WarehouseId.Trim());
    state.Events.Add($"ERP inventory: {state.Availability.Status} at {state.Availability.WarehouseId}");
    return new($"Physical on-hand snapshot: {state.Availability.Status}; see separate ATP", true);
}));

app.MapPost("/api/session/{id:guid}/plan", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions) => Execute(id, sessions, state =>
{
    if (state.Order is null || state.Availability is null) return new("Receive an order and assess inventory first", false);
    if (state.Commitment?.Status is not (CommitmentStatus.Accepted or CommitmentStatus.AlreadyAccepted) ||
        !string.Equals(state.Commitment.WarehouseId, state.Availability.WarehouseId, StringComparison.OrdinalIgnoreCase))
        return new("ERP inventory commitment required before Voxware picking", false);
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

// Voxware: report actual picking against the persisted pick plan.
app.MapPost("/api/session/{id:guid}/pick", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions,
    SqlPickConfirmationStore store) => Execute(id, sessions, state =>
{
    if (!state.Persisted || state.Plan is null || state.Order is null)
        return new("Persist a ready Voxware pick plan first", false);
    if (state.Plan.Status != PickPlanStatus.Ready)
        return new("Only ready pick plans may be confirmed", false);
    var report = new PickConfirmation(state.ExternalReference + "-PICK", state.Order.SalesOrderId,
        state.Plan.WarehouseId, state.Plan.Lines.Select(line => new PickedLine(line.ProductId, line.Quantity)).ToArray());
    var result = store.Confirm(report);
    state.Events.Add($"Voxware pick confirmation: {result.Status} — {result.Message}");
    if (result.Status is not (PickExecutionStatus.Completed or PickExecutionStatus.AlreadyRecorded))
        return new(result.Message, false);
    state.Pick = result;
    return new($"Voxware pick: {result.Status}", true);
}));

// Voxware: report packing against the completed pick. This does not move inventory.
app.MapPost("/api/session/{id:guid}/pack", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions,
    SqlPackingConfirmationStore store) => Execute(id, sessions, state =>
{
    if (state.Pick is null || state.Plan is null || state.Order is null ||
        state.Pick.Status is not (PickExecutionStatus.Completed or PickExecutionStatus.AlreadyRecorded))
        return new("Complete Voxware picking first", false);
    var report = new PackingConfirmation(state.ExternalReference + "-PACK", state.Order.SalesOrderId,
        state.Plan.WarehouseId, state.ExternalReference + "-PICK",
        state.Plan.Lines.Select(line => new PackedLine(line.ProductId, line.Quantity)).ToArray());
    var result = store.Confirm(report);
    state.Events.Add($"Voxware packing confirmation: {result.Status} — {result.Message}");
    if (result.Status is not (PackingStatus.ReadyForShipment or PackingStatus.AlreadyRecorded))
        return new(result.Message, false);
    state.Packing = result;
    return new($"Voxware packing: {result.Status}", true);
}));

// Read-only ERP departure readiness. Packing alone is not physical departure evidence.
// No inventory consumption, warehouse-departure attestation, or dispatch intent is created.
app.MapGet("/api/session/{id:guid}/departure-readiness", (Guid id, ConcurrentDictionary<Guid, DemoSession> sessions) =>
{
    if (!sessions.TryGetValue(id, out var state)) return Results.NotFound(new { error = "Session not found" });
    lock (state.Sync)
    {
        if (state.Order is null) return Results.BadRequest(new { error = "Receive an ERP order first" });
        try { return Results.Ok(ReadDepartureReadiness(connectionString, state.Order.SalesOrderId)); }
        catch (Exception e) { return Results.Problem(e.Message, statusCode: 500); }
    }
});

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
        -- Remove dependent Voxware confirmations before their plan.
        DELETE FROM voxware.PackingConfirmationLine WHERE SalesOrderId=@id;
        DELETE FROM voxware.PackingConfirmation WHERE SalesOrderId=@id;
        DELETE FROM voxware.PickConfirmationLine WHERE SalesOrderId=@id;
        DELETE FROM voxware.PickConfirmation WHERE SalesOrderId=@id;
        DELETE FROM voxware.PickPlanLine WHERE SalesOrderId=@id;
        DELETE FROM voxware.PickPlan WHERE SalesOrderId=@id;
        DELETE FROM erp.StockReservation WHERE SalesOrderId=@id;
        DELETE FROM erp.OrderCommitment WHERE SalesOrderId=@id;
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
    state.Commitment = null;
    state.Atp = null;
    state.Plan = null;
    state.Persisted = false;
    state.Pick = null;
    state.Packing = null;
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
    availability = s.Availability, commitment = s.Commitment, atp = s.Atp, plan = s.Plan, persisted = s.Persisted,
    pick = s.Pick, packing = s.Packing,
    events = s.Events.ToArray()
};

static object ReadRecords(string connectionString, string orderId)
{
    using var sql = new SqlConnection(connectionString);
    sql.Open();
    var orders = new List<object>();
    var orderLines = new List<object>();
    var plans = new List<object>();
    var commitments = new List<object>();
    var reservations = new List<object>();
    var planLines = new List<object>();
    var picks = new List<object>();
    var pickLines = new List<object>();
    var packings = new List<object>();
    var packingLines = new List<object>();
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
    using (var cmd = new SqlCommand("SELECT SalesOrderId, WarehouseId, CommittedUtc FROM erp.OrderCommitment WHERE SalesOrderId=@id", sql))
    {
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = orderId;
        using var r = cmd.ExecuteReader();
        while (r.Read()) commitments.Add(new { salesOrderId = r.GetString(0), warehouseId = r.GetString(1), committedUtc = r.GetDateTime(2) });
    }
    using (var cmd = new SqlCommand("SELECT WarehouseId, ProductId, Quantity FROM erp.StockReservation WHERE SalesOrderId=@id ORDER BY ProductId", sql))
    {
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = orderId;
        using var r = cmd.ExecuteReader();
        while (r.Read()) reservations.Add(new { warehouseId = r.GetString(0), productId = r.GetString(1), quantity = r.GetInt32(2) });
    }
    // Read only the session order; each row belongs to Voxware's SQL schema.
    void ReadConfirmation(string query, Action<SqlDataReader> add)
    {
        using var cmd = new SqlCommand(query, sql);
        cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = orderId;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) add(reader);
    }
    ReadConfirmation("SELECT ConfirmationId, WarehouseId, Outcome FROM voxware.PickConfirmation WHERE SalesOrderId=@id",
        r => picks.Add(new { confirmationId = r.GetString(0), warehouseId = r.GetString(1), outcome = r.GetString(2) }));
    ReadConfirmation("SELECT ProductId, Quantity FROM voxware.PickConfirmationLine WHERE SalesOrderId=@id ORDER BY ProductId",
        r => pickLines.Add(new { productId = r.GetString(0), quantity = r.GetInt32(1) }));
    ReadConfirmation("SELECT PackingId, PickConfirmationId, WarehouseId, Outcome FROM voxware.PackingConfirmation WHERE SalesOrderId=@id",
        r => packings.Add(new { packingId = r.GetString(0), pickConfirmationId = r.GetString(1), warehouseId = r.GetString(2), outcome = r.GetString(3) }));
    ReadConfirmation("SELECT ProductId, Quantity FROM voxware.PackingConfirmationLine WHERE SalesOrderId=@id ORDER BY ProductId",
        r => packingLines.Add(new { productId = r.GetString(0), quantity = r.GetInt32(1) }));
    return new { erp = new { orders, orderLines, commitments, reservations },
        voxware = new { plans, planLines, picks, pickLines, packings, packingLines } };
}

// Snapshot of existing ERP/Voxware evidence, not an authorization to depart.
static object ReadDepartureReadiness(string cs, string salesOrderId)
{
    using var conn = new SqlConnection(cs);
    conn.Open();
    using var cmd = new SqlCommand("""
        SELECT c.WarehouseId,
            (SELECT COUNT(*) FROM voxware.PackingConfirmation p
             WHERE p.SalesOrderId=c.SalesOrderId AND p.WarehouseId=c.WarehouseId
               AND p.Outcome='ReadyForShipment') AS ReadyPackings,
            (SELECT COUNT(*) FROM erp.StockReservation r WHERE r.SalesOrderId=c.SalesOrderId AND r.WarehouseId=c.WarehouseId) AS ReservationLines,
            (SELECT COUNT(*) FROM erp.OrderCancellation x WHERE x.SalesOrderId=c.SalesOrderId) AS Cancellations,
            (SELECT COUNT(*) FROM erp.FulfillmentConsumption f WHERE f.SalesOrderId=c.SalesOrderId) AS Consumptions,
            (SELECT COUNT(*) FROM erp.WarehouseDeparture d WHERE d.SalesOrderId=c.SalesOrderId) AS Departures,
            (SELECT COUNT(*) FROM erp.DispatchIntent di JOIN erp.WarehouseDeparture d ON d.DepartureId=di.DepartureId
             WHERE d.SalesOrderId=c.SalesOrderId) AS DispatchIntents
        FROM erp.OrderCommitment c WHERE c.SalesOrderId=@id;
        """, conn);
    cmd.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = salesOrderId;
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return new { salesOrderId, eligibleForAttestation = false, reason = "No ERP commitment" };
    var warehouseId = reader.GetString(0);
    var readyPackings = reader.GetInt32(1);
    var reservations = reader.GetInt32(2);
    var cancellations = reader.GetInt32(3);
    var consumptions = reader.GetInt32(4);
    var departures = reader.GetInt32(5);
    var dispatchIntents = reader.GetInt32(6);
    var eligible = readyPackings == 1 && reservations > 0 && cancellations == 0 && consumptions == 0 && departures == 0;
    return new { salesOrderId, warehouseId, readyPackings, reservationLines = reservations,
        cancellations, consumptions, departures, dispatchIntents, eligibleForAttestation = eligible,
        reason = eligible ? "Packing and reservation evidence present; separate authorized physical departure attestation required" :
            "ERP departure prerequisites incomplete or already consumed" };
}

// ERP ATP is a read-only observation, separate from physical on-hand and the atomic acceptance operation.
static object ReadAtp(string connectionString, string salesOrderId, string warehouseId)
{
    using var sql = new SqlConnection(connectionString);
    sql.Open();
    using var cmd = new SqlCommand("""
        SELECT d.ProductId, d.Requested, soh.OnHand,
            COALESCE(res.Reserved, CONVERT(bigint, 0)) AS Reserved,
            CASE WHEN soh.OnHand IS NULL THEN NULL
                 ELSE CONVERT(bigint, soh.OnHand) - COALESCE(res.Reserved, CONVERT(bigint, 0)) END AS ATP
        FROM (
            SELECT ProductId, SUM(CONVERT(bigint, Quantity)) AS Requested
            FROM erp.SalesOrderLine WHERE SalesOrderId=@order GROUP BY ProductId
        ) d
        LEFT JOIN erp.StockOnHand soh ON soh.WarehouseId=@warehouse AND soh.ProductId=d.ProductId
        OUTER APPLY (
            SELECT SUM(CONVERT(bigint, r.Quantity)) AS Reserved FROM erp.StockReservation r
            WHERE r.WarehouseId=@warehouse AND r.ProductId=d.ProductId
        ) res ORDER BY d.ProductId;
        """, sql);
    cmd.Parameters.Add("@order", SqlDbType.VarChar, 64).Value = salesOrderId;
    cmd.Parameters.Add("@warehouse", SqlDbType.VarChar, 64).Value = warehouseId;
    var lines = new List<object>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) lines.Add(new {
        productId = reader.GetString(0), requested = reader.GetInt64(1),
        onHand = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
        reserved = reader.GetInt64(3),
        availableToPromise = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4)
    });
    return new { warehouseId, lines };
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
    public CommitmentResult? Commitment { get; set; }
    public object? Atp { get; set; }
    public PickPlan? Plan { get; set; }
    public bool Persisted { get; set; }
    public PickExecutionResult? Pick { get; set; }
    public PackingResult? Packing { get; set; }
    public bool CreatedBySession { get; set; }
    public List<string> Events { get; } = new();
}
