FRONTIER ORDER-TO-WAREHOUSE DEMO | Increment 1 (2026-11-13)

Purpose
-------
Show one observable transaction using existing, independently verified components:
  nopCommerce-originated order -> ERP product identity and durable order intake
  -> ERP SQL stock availability -> Voxware PickPlanning -> Voxware SQL pick-plan persistence
  -> SQL row verification -> replay in a new DI scope.

No production source, existing tests, SQL schema, or Business Application ownership is modified.
This is a cross-application demonstration composition root, not a new business application.

Requirements
------------
.NET 8 SDK; existing SQL foundation/seed installed in FrontierModel;
FRONTIER_SQL_CONNECTION_STRING set as used by the SQL integration test runners.
Sample identifiers C0001, LOC-01, WH-01, FD-DOG-0142, FD-DOG-0143 must exist.

Build
-----
dotnet build 'demos/Frontier.OrderFlow.Demo/Frontier.OrderFlow.Demo.csproj'

Run
---
dotnet run --project 'demos/Frontier.OrderFlow.Demo/Frontier.OrderFlow.Demo.csproj' --no-build

Alternatives (use -- after the project path)
---------------------------------------------
dotnet run --project 'demos/Frontier.OrderFlow.Demo/Frontier.OrderFlow.Demo.csproj' --no-build -- --short
    Demonstrate shortage and a persisted BlockedShort Voxware plan.

dotnet run --project 'demos/Frontier.OrderFlow.Demo/Frontier.OrderFlow.Demo.csproj' --no-build -- --keep
    Retain a successful demo run so ERP and Voxware records can be inspected in SSMS.

By default, only rows created by that invocation are deleted afterward.
If --keep is selected, record the order ID printed by the runner for later manual cleanup.
No automatic changes to source inventory quantities are made.

Acceptance
----------
Build succeeds; normal scenario PASS exit 0; shortage scenario PASS exit 0;
ERP/Voxware SQL rows verified; fresh-scope replay idempotent; cleanup successful.
This package has not been built or run against FrontierModel in this environment.

Git is user-only through /GIT; no Git or CUB operations are included.
