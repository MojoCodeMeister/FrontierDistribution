# Stage 2 — Warehouse pick planning v0.1.0

Synthetic, executable workflow: nopCommerce order -> canonical product identity -> internal order -> warehouse inventory availability -> proposed pick plan.

A ready pick plan identifies a warehouse, order and aggregated product quantities. A shortage or unknown stock produces a blocked plan with no lines. This is an all-or-nothing **proposal** based on an ephemeral snapshot; it does not allocate or reserve stock, mutate the order, deduct inventory, confirm picking or invoke Voxware.

Stage 1 remains complete. This is the next Stage 2 chronological increment. The installation leaves prior code and data untouched except adding the pick-planning source/test project to the solution.

To verify: `dotnet build .\FrontierDistribution.sln`; `dotnet run --project .\tests\Frontier.Picking.Tests` (along with the three preceding runners). Live Frontier platform design and authoritative inventory sources remain unknown.
