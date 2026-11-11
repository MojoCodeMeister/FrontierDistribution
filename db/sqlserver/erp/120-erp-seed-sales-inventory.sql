/*
    Frontier Distribution working model - ERP Business Application
    Increment 2: ERP sales orders and inventory
    Script 120: Synthetic sales order and stock data

    erp.StockOnHand mirrors data/synthetic-stock.json row for row.

    erp.SalesOrder and erp.SalesOrderLine hold the outcome the C# OrderIntake
    produces for data/synthetic-order.json (Orders and EndToEnd harnesses):
    WEB-10001 from nopCommerce is accepted as SO-0001, status Received, with
    SKU FD-DOG-0142 resolved to P000142 and FD-DOG-0143 to P000143. The script
    records that outcome; it does not perform intake.

    Inserts only rows that are missing, in one transaction. Rerunnable.
    Synthetic data only. Not Frontier's production data.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'erp.StockOnHand', N'U') IS NULL OR OBJECT_ID(N'erp.SalesOrderLine', N'U') IS NULL
    THROW 50001, N'Increment 2 tables are missing. Run 040-erp-stock-on-hand.sql and 050-erp-sales-orders.sql first.', 1;

BEGIN TRANSACTION;

INSERT INTO erp.StockOnHand (WarehouseId, ProductId, OnHand)
SELECT v.WarehouseId, v.ProductId, v.OnHand
FROM (VALUES
    ('WH-01', 'P000142', 10),
    ('WH-01', 'P000143', 5)
) AS v (WarehouseId, ProductId, OnHand)
WHERE NOT EXISTS (SELECT 1 FROM erp.StockOnHand AS t
                  WHERE t.WarehouseId = v.WarehouseId AND t.ProductId = v.ProductId);

INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status)
SELECT v.SalesOrderId, v.SourceSystem, v.ExternalOrderId, v.CustomerId, v.ShipToLocationId, v.Status
FROM (VALUES
    ('SO-0001', 'nopCommerce', 'WEB-10001', 'C0001', 'LOC-01', 'Received')
) AS v (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status)
WHERE NOT EXISTS (SELECT 1 FROM erp.SalesOrder AS t WHERE t.SalesOrderId = v.SalesOrderId);

INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity)
SELECT v.SalesOrderId, v.SourceLineId, v.LineSequence, v.SourceSku, v.ProductId, v.Quantity
FROM (VALUES
    ('SO-0001', '1', 1, 'FD-DOG-0142', 'P000142', 3),
    ('SO-0001', '2', 2, 'FD-DOG-0143', 'P000143', 2)
) AS v (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity)
WHERE NOT EXISTS (SELECT 1 FROM erp.SalesOrderLine AS t
                  WHERE t.SalesOrderId = v.SalesOrderId AND t.SourceLineId = v.SourceLineId);

COMMIT TRANSACTION;

SELECT N'erp.StockOnHand'    AS TableName, COUNT(*) AS RowCnt FROM erp.StockOnHand
UNION ALL SELECT N'erp.SalesOrder',     COUNT(*) FROM erp.SalesOrder
UNION ALL SELECT N'erp.SalesOrderLine', COUNT(*) FROM erp.SalesOrderLine;

PRINT N'120-erp-seed-sales-inventory: synthetic sales order and stock data present.';
GO
