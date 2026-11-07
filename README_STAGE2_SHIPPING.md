# Frontier Stage 2 — Shipment Creation and Synthetic Dispatch v0.1.0

Chronological next increment after packing/ready-for-shipment. `Shipping.Create` accepts a ready `PackingResult`, snapshots product quantities and references, and creates an in-memory prepared shipment. `Shipping.Dispatch` requires a separate dispatch confirmation and marks that shipment dispatched in the shipping component only. Replays are idempotent; conflicting identifiers and requests reject.

The system makes **no** claim about Frontier's actual ERP/WMS, carriers or logistics. No external posting, carrier booking, tracking, inventory reservation/deduction, invoicing, or mutation of sales order/pick/packing state. One shipment per order/warehouse, no split shipping. In-memory only.

Expected on Freya: existing 101 tests plus 27 shipping checks = 128 total. Run script restores/builds then runs seven existing executable harnesses. Build/runtime not verified in authoring environment (no dotnet SDK).
