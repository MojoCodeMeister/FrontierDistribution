# Stage 2 — Synthetic pick execution v0.1.0

Chronological sequence: webstore intake → canonical product identity → internal order → read-only warehouse availability → proposed pick plan → **synthetic pick confirmation**.

A confirmation specifies its ID, order, warehouse and one picked quantity per planned product. The new `PickExecution` component records a full or partial confirmation in memory and rejects excess, negative, duplicate, missing or unknown products. Duplicate delivery of the same confirmation returns `AlreadyRecorded` without a second execution; conflicting retries are rejected. A rejected confirmation does not occupy the plan.

**Boundary:** exactly one confirmation per ready pick plan in this increment. There is no continued picking after partial confirmation, durable persistence, authoritative WMS/Voxware integration, reservation, stock decrement, shipment, or change to sales-order status. No claim is made about Frontier's actual APIs or business rules.

The installer adds one C# source file, a separate executable test project and this README. It does not overwrite existing solution sources or data. Verify with `dotnet build .\FrontierDistribution.sln` and five existing/new executable test harnesses.
