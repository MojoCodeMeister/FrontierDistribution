# Stage 2 — Order intake demonstrator (candidate, v0.1.0)

**Purpose:** One small, executable, synthetic webstore-to-internal-order handoff. No claim about Frontier production architecture or interfaces.

## Run on Freya

```powershell
dotnet restore .\FrontierDistribution.sln
dotnet build .\FrontierDistribution.sln --no-restore
dotnet run --project .\tests\Frontier.Identity.Tests
dotnet run --project .\tests\Frontier.Orders.Tests
```

The Stage 2 runner prints an incoming synthetic web order and the resulting internal order, then executes **11 checks**. The Stage 1 tests remain separate.

## Behavior demonstrated

- Source `nopCommerce` / `WEB-10001`; two SKU lines map to canonical `P000142` / `P000143` using the Stage 1 product resolver.
- `CustomerId=C0001` is already a **canonical** model customer ID; it is checked against the catalog, **not translated** from an external customer identity.
- `LOC-01` is validated as belonging to `C0001`.
- Accepted orders receive local IDs and `Received` status; an in-memory repository retains them for the process lifetime.
- Duplicate source order IDs return their existing order when payloads are identical; mismatched duplicate payloads are rejected.
- Unknown customers, locations, SKUs, invalid quantities and duplicate line numbers are rejected without partial storage.

## Deliberately not implemented

Durable persistence, authenticated API, customer cross-references, pricing, tax, stock availability, allocation/reservations, payment, shipping, ERP/WMS integration, concurrency and order state transitions. In-memory order intake is **single-process demonstration only**; no restart, reliability or concurrency guarantees.

Source input and all identifiers are invented. Existing Q24 inventory authority scenarios remain unresolved.

## Verification

Generated in an environment without the `dotnet` CLI. **Compilation and execution are not claimed** until the commands above are run. Review the console output for actual pass/fail evidence before any SB CUB.
