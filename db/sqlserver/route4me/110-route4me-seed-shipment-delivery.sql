/*
    Frontier Distribution working model - Route4Me Business Application
    Increment 4: Route4Me shipment, dispatch and delivery
    Script 110: Synthetic shipment, dispatch and delivery data

    The records the C# components produce for ERP order SO-0001 at warehouse
    WH-01, continuing the Voxware fulfillment of Increment 3:
      - Shipping.Create takes the ReadyForShipment packing PACK-0001 (pick
        confirmation PICK-0001) and prepares SHIP-0001 with the packed lines
        P000142 x3, P000143 x2; Shipping.Dispatch confirms DISP-0001, so the
        stored shipment is Dispatched (Shipping harness).
      - The DISPATCH handoff registers SHIP-0001 with Delivery, unchanged
        (samples/stage2-dispatch.json; Delivery integration harness).
      - Delivery records DEL-0001: Delivered with proof POD-0001 at
        2026-11-09 12:00 -05:00 (Frontier.Delivery harness).
    SO-0001, WH-01, P000142 and P000143 are ERP identifiers; PACK-0001 and
    PICK-0001 are Voxware references. All are carried as values. No table in
    another model is read or referenced.

    Inserts only rows that are missing, in one transaction. Rerunnable.
    Synthetic data only. Not Frontier's production data.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'route4me.ShipmentLine', N'U') IS NULL OR OBJECT_ID(N'route4me.DeliveryEvent', N'U') IS NULL
    THROW 50001, N'Route4Me tables are missing. Run scripts 010 to 030 first.', 1;

BEGIN TRANSACTION;

INSERT INTO route4me.Shipment (ShipmentId, SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Status, DispatchId)
SELECT v.ShipmentId, v.SalesOrderId, v.WarehouseId, v.PackingId, v.PickConfirmationId, v.Status, v.DispatchId
FROM (VALUES
    ('SHIP-0001', 'SO-0001', 'WH-01', 'PACK-0001', 'PICK-0001', 'Dispatched', 'DISP-0001')
) AS v (ShipmentId, SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Status, DispatchId)
WHERE NOT EXISTS (SELECT 1 FROM route4me.Shipment AS t WHERE t.ShipmentId = v.ShipmentId);

INSERT INTO route4me.ShipmentLine (ShipmentId, ProductId, Quantity)
SELECT v.ShipmentId, v.ProductId, v.Quantity
FROM (VALUES
    ('SHIP-0001', 'P000142', 3),
    ('SHIP-0001', 'P000143', 2)
) AS v (ShipmentId, ProductId, Quantity)
WHERE NOT EXISTS (SELECT 1 FROM route4me.ShipmentLine AS t
                  WHERE t.ShipmentId = v.ShipmentId AND t.ProductId = v.ProductId);

INSERT INTO route4me.DeliveryShipment (ShipmentId, SalesOrderId, WarehouseId, Status)
SELECT v.ShipmentId, v.SalesOrderId, v.WarehouseId, v.Status
FROM (VALUES
    ('SHIP-0001', 'SO-0001', 'WH-01', 'Dispatched')
) AS v (ShipmentId, SalesOrderId, WarehouseId, Status)
WHERE NOT EXISTS (SELECT 1 FROM route4me.DeliveryShipment AS t WHERE t.ShipmentId = v.ShipmentId);

INSERT INTO route4me.DeliveryShipmentLine (ShipmentId, ProductId, Quantity)
SELECT v.ShipmentId, v.ProductId, v.Quantity
FROM (VALUES
    ('SHIP-0001', 'P000142', 3),
    ('SHIP-0001', 'P000143', 2)
) AS v (ShipmentId, ProductId, Quantity)
WHERE NOT EXISTS (SELECT 1 FROM route4me.DeliveryShipmentLine AS t
                  WHERE t.ShipmentId = v.ShipmentId AND t.ProductId = v.ProductId);

INSERT INTO route4me.DeliveryEvent (EventId, ShipmentId, Outcome, OccurredAt, ProofReference)
SELECT v.EventId, v.ShipmentId, v.Outcome, CAST(v.OccurredAt AS datetimeoffset(7)), v.ProofReference
FROM (VALUES
    ('DEL-0001', 'SHIP-0001', 'Delivered', '2026-11-09T12:00:00-05:00', 'POD-0001')
) AS v (EventId, ShipmentId, Outcome, OccurredAt, ProofReference)
WHERE NOT EXISTS (SELECT 1 FROM route4me.DeliveryEvent AS t WHERE t.EventId = v.EventId);

COMMIT TRANSACTION;

SELECT N'route4me.Shipment'                AS TableName, COUNT(*) AS RowCnt FROM route4me.Shipment
UNION ALL SELECT N'route4me.ShipmentLine',         COUNT(*) FROM route4me.ShipmentLine
UNION ALL SELECT N'route4me.DeliveryShipment',     COUNT(*) FROM route4me.DeliveryShipment
UNION ALL SELECT N'route4me.DeliveryShipmentLine', COUNT(*) FROM route4me.DeliveryShipmentLine
UNION ALL SELECT N'route4me.DeliveryEvent',        COUNT(*) FROM route4me.DeliveryEvent;

PRINT N'110-route4me-seed-shipment-delivery: synthetic shipment and delivery data present.';
GO
