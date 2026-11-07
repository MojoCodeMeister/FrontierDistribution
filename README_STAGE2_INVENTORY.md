# Stage 2 increment: synthetic warehouse availability (v0.1.0)

Receives the same two-line synthetic order via Stage 2 `OrderIntake`, and evaluates it against an invented warehouse stock snapshot.

- Read-only: no allocation, reservation, quantity decrement, warehouse command, or order state transition.
- Missing quantity is **Unknown** rather than assumed zero.
- Repeated lines of the same product are aggregated before comparison.
- Stock figures are synthetic; inventory authority remains unresolved under Q24.
- Adds the catalog inventory position `WH-01 / P000143` only when absent and validates it against catalog Products/Warehouses.

Run `dotnet run --project .\tests\Frontier.Inventory.Tests` after the installer. This runner performs ten checks in addition to the existing Stage 1/Stage 2 harnesses.
