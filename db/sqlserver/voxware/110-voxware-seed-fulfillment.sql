/*
    Frontier Distribution working model - Voxware Business Application
    Increment 3: Voxware warehouse fulfillment
    Script 110: Synthetic fulfillment data

    The fulfillment records the C# components produce for ERP order SO-0001 at
    warehouse WH-01, as in the Shipping harness and samples/stage2-dispatch.json:
      - PickPlanning: SO-0001 assesses Available (P000142 3 of 10, P000143 2 of 5),
        so the plan is Ready with aggregated demand P000142 x3, P000143 x2.
      - PickExecution: PICK-0001 picks both lines in full, so Completed.
      - Packing: PACK-0001 cites PICK-0001 and packs both lines in full, so
        ReadyForShipment, which is what Shipping needs to create SHIP-0001.
    SO-0001, WH-01, P000142 and P000143 are ERP identifiers carried as values
    (ERP increment 2). No ERP table is read or referenced.

    Inserts only rows that are missing, in one transaction. Rerunnable.
    Synthetic data only. Not Frontier's production data.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'voxware.PackingConfirmationLine', N'U') IS NULL
    THROW 50001, N'Voxware tables are missing. Run scripts 010 to 040 first.', 1;

BEGIN TRANSACTION;

INSERT INTO voxware.PickPlan (SalesOrderId, WarehouseId, Status)
SELECT v.SalesOrderId, v.WarehouseId, v.Status
FROM (VALUES
    ('SO-0001', 'WH-01', 'Ready')
) AS v (SalesOrderId, WarehouseId, Status)
WHERE NOT EXISTS (SELECT 1 FROM voxware.PickPlan AS t
                  WHERE t.SalesOrderId = v.SalesOrderId AND t.WarehouseId = v.WarehouseId);

INSERT INTO voxware.PickPlanLine (SalesOrderId, WarehouseId, ProductId, Quantity)
SELECT v.SalesOrderId, v.WarehouseId, v.ProductId, v.Quantity
FROM (VALUES
    ('SO-0001', 'WH-01', 'P000142', 3),
    ('SO-0001', 'WH-01', 'P000143', 2)
) AS v (SalesOrderId, WarehouseId, ProductId, Quantity)
WHERE NOT EXISTS (SELECT 1 FROM voxware.PickPlanLine AS t
                  WHERE t.SalesOrderId = v.SalesOrderId AND t.WarehouseId = v.WarehouseId
                    AND t.ProductId = v.ProductId);

INSERT INTO voxware.PickConfirmation (SalesOrderId, WarehouseId, ConfirmationId, Outcome)
SELECT v.SalesOrderId, v.WarehouseId, v.ConfirmationId, v.Outcome
FROM (VALUES
    ('SO-0001', 'WH-01', 'PICK-0001', 'Completed')
) AS v (SalesOrderId, WarehouseId, ConfirmationId, Outcome)
WHERE NOT EXISTS (SELECT 1 FROM voxware.PickConfirmation AS t
                  WHERE t.SalesOrderId = v.SalesOrderId AND t.WarehouseId = v.WarehouseId);

INSERT INTO voxware.PickConfirmationLine (SalesOrderId, WarehouseId, ProductId, Quantity)
SELECT v.SalesOrderId, v.WarehouseId, v.ProductId, v.Quantity
FROM (VALUES
    ('SO-0001', 'WH-01', 'P000142', 3),
    ('SO-0001', 'WH-01', 'P000143', 2)
) AS v (SalesOrderId, WarehouseId, ProductId, Quantity)
WHERE NOT EXISTS (SELECT 1 FROM voxware.PickConfirmationLine AS t
                  WHERE t.SalesOrderId = v.SalesOrderId AND t.WarehouseId = v.WarehouseId
                    AND t.ProductId = v.ProductId);

INSERT INTO voxware.PackingConfirmation (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Outcome)
SELECT v.SalesOrderId, v.WarehouseId, v.PackingId, v.PickConfirmationId, v.Outcome
FROM (VALUES
    ('SO-0001', 'WH-01', 'PACK-0001', 'PICK-0001', 'ReadyForShipment')
) AS v (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Outcome)
WHERE NOT EXISTS (SELECT 1 FROM voxware.PackingConfirmation AS t
                  WHERE t.SalesOrderId = v.SalesOrderId AND t.WarehouseId = v.WarehouseId);

INSERT INTO voxware.PackingConfirmationLine (SalesOrderId, WarehouseId, ProductId, Quantity)
SELECT v.SalesOrderId, v.WarehouseId, v.ProductId, v.Quantity
FROM (VALUES
    ('SO-0001', 'WH-01', 'P000142', 3),
    ('SO-0001', 'WH-01', 'P000143', 2)
) AS v (SalesOrderId, WarehouseId, ProductId, Quantity)
WHERE NOT EXISTS (SELECT 1 FROM voxware.PackingConfirmationLine AS t
                  WHERE t.SalesOrderId = v.SalesOrderId AND t.WarehouseId = v.WarehouseId
                    AND t.ProductId = v.ProductId);

COMMIT TRANSACTION;

SELECT N'voxware.PickPlan'                AS TableName, COUNT(*) AS RowCnt FROM voxware.PickPlan
UNION ALL SELECT N'voxware.PickPlanLine',            COUNT(*) FROM voxware.PickPlanLine
UNION ALL SELECT N'voxware.PickConfirmation',        COUNT(*) FROM voxware.PickConfirmation
UNION ALL SELECT N'voxware.PickConfirmationLine',    COUNT(*) FROM voxware.PickConfirmationLine
UNION ALL SELECT N'voxware.PackingConfirmation',     COUNT(*) FROM voxware.PackingConfirmation
UNION ALL SELECT N'voxware.PackingConfirmationLine', COUNT(*) FROM voxware.PackingConfirmationLine;

PRINT N'110-voxware-seed-fulfillment: synthetic fulfillment data present.';
GO
