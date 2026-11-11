/*
    Frontier Distribution working model — ERP Business Application
    Increment 1: ERP foundation
    Script 110: Synthetic foundation data

    Mirrors data/synthetic-identities.json row for row (the Stage 1 fixture the
    C# harnesses use). It also covers everything the Stage 2 fixtures need from
    master data: customer C0001 with ship-to LOC-01, nopCommerce SKUs
    FD-DOG-0142 and FD-DOG-0143, and warehouse WH-01 positions for both products.

    Inserts only rows that are missing, in one transaction. Rerunnable.
    Synthetic data only. Not Frontier's production data.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'erp.ProductIdentity', N'U') IS NULL
    THROW 50001, N'ERP foundation tables are missing. Run scripts 010 to 030 first.', 1;

BEGIN TRANSACTION;

INSERT INTO erp.Customer (CustomerId, Name)
SELECT v.CustomerId, v.Name
FROM (VALUES
    ('C0001', N'Synthetic pet retailer')
) AS v (CustomerId, Name)
WHERE NOT EXISTS (SELECT 1 FROM erp.Customer AS t WHERE t.CustomerId = v.CustomerId);

INSERT INTO erp.CustomerLocation (LocationId, CustomerId, Label)
SELECT v.LocationId, v.CustomerId, v.Label
FROM (VALUES
    ('LOC-01', 'C0001', N'Retail store'),
    ('LOC-02', 'C0001', N'Secondary receiving')
) AS v (LocationId, CustomerId, Label)
WHERE NOT EXISTS (SELECT 1 FROM erp.CustomerLocation AS t WHERE t.LocationId = v.LocationId);

INSERT INTO erp.Product (ProductId, Description, BaseUnit)
SELECT v.ProductId, v.Description, v.BaseUnit
FROM (VALUES
    ('P000142', N'Synthetic dog food 15 lb', 'Each'),
    ('P000143', N'Synthetic dog food 30 lb', 'Each')
) AS v (ProductId, Description, BaseUnit)
WHERE NOT EXISTS (SELECT 1 FROM erp.Product AS t WHERE t.ProductId = v.ProductId);

INSERT INTO erp.Supplier (SupplierId, Name)
SELECT v.SupplierId, v.Name
FROM (VALUES
    ('SUP-1', N'Synthetic supplier A'),
    ('SUP-2', N'Synthetic supplier B')
) AS v (SupplierId, Name)
WHERE NOT EXISTS (SELECT 1 FROM erp.Supplier AS t WHERE t.SupplierId = v.SupplierId);

/* No natural key exists (D13), so a row counts as present when every column matches. */
INSERT INTO erp.SupplierProduct (SupplierId, ProductId, SupplierItemNumber, PurchaseUnit, BaseUnitsPerPurchaseUnit)
SELECT v.SupplierId, v.ProductId, v.SupplierItemNumber, v.PurchaseUnit, v.BaseUnitsPerPurchaseUnit
FROM (VALUES
    ('SUP-1', 'P000142', 'SHARED-01', 'Case', CAST(4 AS decimal(18,6))),
    ('SUP-2', 'P000142', 'SP-142',    'Each', CAST(1 AS decimal(18,6))),
    ('SUP-2', 'P000143', 'SHARED-01', 'Case', CAST(2 AS decimal(18,6)))
) AS v (SupplierId, ProductId, SupplierItemNumber, PurchaseUnit, BaseUnitsPerPurchaseUnit)
WHERE NOT EXISTS (SELECT 1 FROM erp.SupplierProduct AS t
                  WHERE t.SupplierId = v.SupplierId
                    AND t.ProductId = v.ProductId
                    AND t.SupplierItemNumber = v.SupplierItemNumber
                    AND t.PurchaseUnit = v.PurchaseUnit
                    AND t.BaseUnitsPerPurchaseUnit = v.BaseUnitsPerPurchaseUnit);

INSERT INTO erp.Warehouse (WarehouseId, Name)
SELECT v.WarehouseId, v.Name
FROM (VALUES
    ('WH-01', N'Synthetic distribution warehouse')
) AS v (WarehouseId, Name)
WHERE NOT EXISTS (SELECT 1 FROM erp.Warehouse AS t WHERE t.WarehouseId = v.WarehouseId);

INSERT INTO erp.InventoryPosition (WarehouseId, ProductId)
SELECT v.WarehouseId, v.ProductId
FROM (VALUES
    ('WH-01', 'P000142'),
    ('WH-01', 'P000143')
) AS v (WarehouseId, ProductId)
WHERE NOT EXISTS (SELECT 1 FROM erp.InventoryPosition AS t
                  WHERE t.WarehouseId = v.WarehouseId AND t.ProductId = v.ProductId);

/* Identity namespaces are the fixture's own: ERP, nopCommerce, Warehouse
   (no Business Application yet; UR02) and Supplier (Context = supplier). */
INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context])
SELECT v.ProductId, v.[System], v.IdentifierType, v.ExternalId, v.[Context]
FROM (VALUES
    ('P000142', 'ERP',         'ItemNumber', 'ITEM-000142', CAST(NULL AS varchar(150))),
    ('P000142', 'nopCommerce', 'SKU',        'FD-DOG-0142', NULL),
    ('P000142', 'Warehouse',   'ItemRef',    'WH-0142',     NULL),
    ('P000142', 'Supplier',    'ItemNumber', 'SHARED-01',   'SUP-1'),
    ('P000142', 'Supplier',    'ItemNumber', 'SP-142',      'SUP-2'),
    ('P000143', 'ERP',         'ItemNumber', 'ITEM-000143', NULL),
    ('P000143', 'Supplier',    'ItemNumber', 'SHARED-01',   'SUP-2'),
    ('P000143', 'nopCommerce', 'SKU',        'FD-DOG-0143', NULL)
) AS v (ProductId, [System], IdentifierType, ExternalId, [Context])
WHERE NOT EXISTS (SELECT 1 FROM erp.ProductIdentity AS t
                  WHERE t.[System] = v.[System]
                    AND t.IdentifierType = v.IdentifierType
                    AND t.ExternalId = v.ExternalId
                    AND (t.[Context] = v.[Context] OR (t.[Context] IS NULL AND v.[Context] IS NULL)));

COMMIT TRANSACTION;

SELECT N'erp.Customer'          AS TableName, COUNT(*) AS RowCnt FROM erp.Customer
UNION ALL SELECT N'erp.CustomerLocation',  COUNT(*) FROM erp.CustomerLocation
UNION ALL SELECT N'erp.Product',           COUNT(*) FROM erp.Product
UNION ALL SELECT N'erp.Supplier',          COUNT(*) FROM erp.Supplier
UNION ALL SELECT N'erp.SupplierProduct',   COUNT(*) FROM erp.SupplierProduct
UNION ALL SELECT N'erp.Warehouse',         COUNT(*) FROM erp.Warehouse
UNION ALL SELECT N'erp.InventoryPosition', COUNT(*) FROM erp.InventoryPosition
UNION ALL SELECT N'erp.ProductIdentity',   COUNT(*) FROM erp.ProductIdentity;

PRINT N'110-erp-seed-foundation: synthetic foundation data present.';
GO
