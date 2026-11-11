/*
    Frontier Distribution working model - ERP Business Application
    Increment 2: ERP sales orders and inventory
    Script 920: Verification

    Read-only and repeatable: every constraint case runs in its own transaction
    and is rolled back; check Z01 confirms nothing changed. Returns one result set
    (one row per check) and raises error 50003 when any check fails, so
    sqlcmd -b exits with code 1.

    Collation-safe: catalog names are compared with COLLATE DATABASE_DEFAULT on
    both sides, and work tables that meet erp columns declare the erp collation,
    so the script runs whatever the server and database default collations are.
    ASCII only, for sqlcmd under Windows PowerShell 5.1.

    Areas
      S  Structure: tables, constraints, indexes, collation, ship-to wiring, boundary
      D  Synthetic data: counts, WEB-10001 outcome, submission snapshot, stock fixture
      Q  Readiness for the Voxware increment: availability and dispatch quantities
      C  Constraint behavior: rejected and accepted writes, rolled back
      Z  Repeatability
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT OFF;  -- constraint cases must reach CATCH and roll back individually

IF @@TRANCOUNT > 0
    THROW 50002, N'Run 920-erp-verify-sales-inventory.sql outside an open transaction.', 1;

DECLARE @Missing nvarchar(4000) =
(
    SELECT STUFF((
        SELECT N', erp.' + e.TableName
        FROM (VALUES (N'Customer'), (N'CustomerLocation'), (N'Product'), (N'Supplier'),
                     (N'SupplierProduct'), (N'Warehouse'), (N'InventoryPosition'), (N'ProductIdentity'),
                     (N'StockOnHand'), (N'SalesOrder'), (N'SalesOrderLine')) AS e (TableName)
        WHERE OBJECT_ID(N'erp.' + e.TableName, N'U') IS NULL
        FOR XML PATH(''), TYPE).value('.', 'nvarchar(4000)'), 1, 2, N'')
);
IF @Missing IS NOT NULL
BEGIN
    SET @Missing = N'ERP tables missing: ' + @Missing + N'. Install increments 1 and 2 first.';
    THROW 50001, @Missing, 1;
END;

/* Environment, for the record (informational) */
DECLARE @Environment nvarchar(400) = CONCAT(
    N'Server collation: ', CAST(SERVERPROPERTY('Collation') AS nvarchar(128)),
    N'; database ', DB_NAME(), N' collation: ', CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)),
    N'; product version: ', CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)));
PRINT @Environment;

DECLARE @Results TABLE
(
    Seq          int IDENTITY(1,1) PRIMARY KEY,
    CheckId      varchar(10)    NOT NULL UNIQUE,
    Area         nvarchar(20)   NOT NULL,
    Description  nvarchar(300)  NOT NULL,
    Expected     nvarchar(300)  NOT NULL,
    Actual       nvarchar(1000) NULL,
    Passed       bit            NOT NULL
);

DECLARE @Actual nvarchar(1000);
DECLARE @RowsBefore int =
      (SELECT COUNT(*) FROM erp.Customer)          + (SELECT COUNT(*) FROM erp.CustomerLocation)
    + (SELECT COUNT(*) FROM erp.Product)           + (SELECT COUNT(*) FROM erp.Supplier)
    + (SELECT COUNT(*) FROM erp.SupplierProduct)   + (SELECT COUNT(*) FROM erp.Warehouse)
    + (SELECT COUNT(*) FROM erp.InventoryPosition) + (SELECT COUNT(*) FROM erp.ProductIdentity)
    + (SELECT COUNT(*) FROM erp.StockOnHand)       + (SELECT COUNT(*) FROM erp.SalesOrder)
    + (SELECT COUNT(*) FROM erp.SalesOrderLine);

/* ===== S: Structure ===================================================== */

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'S01', N'Structure', N'ERP foundation from increment 1 is still present (8 tables)', N'8 tables',
       CONCAT(COUNT(*), N' tables'), CASE WHEN COUNT(*) = 8 THEN 1 ELSE 0 END
FROM sys.tables AS t
WHERE t.schema_id = SCHEMA_ID(N'erp')
  AND t.name COLLATE DATABASE_DEFAULT IN (N'Customer', N'CustomerLocation', N'Product', N'Supplier',
                 N'SupplierProduct', N'Warehouse', N'InventoryPosition', N'ProductIdentity');

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'S02', N'Structure', N'The 3 increment 2 tables exist', N'3 tables',
       CONCAT(COUNT(*), N' tables'), CASE WHEN COUNT(*) = 3 THEN 1 ELSE 0 END
FROM sys.tables AS t
WHERE t.schema_id = SCHEMA_ID(N'erp')
  AND t.name COLLATE DATABASE_DEFAULT IN (N'StockOnHand', N'SalesOrder', N'SalesOrderLine');

DECLARE @ExpectedConstraints TABLE (TableName sysname NOT NULL, ConstraintName sysname NOT NULL, ObjectType char(2) NOT NULL);
INSERT INTO @ExpectedConstraints (TableName, ConstraintName, ObjectType) VALUES
    (N'StockOnHand',    N'PK_StockOnHand',                           'PK'),
    (N'StockOnHand',    N'FK_StockOnHand_InventoryPosition',         'F'),
    (N'StockOnHand',    N'CK_StockOnHand_OnHand_NonNegative',        'C'),
    (N'SalesOrder',     N'PK_SalesOrder',                            'PK'),
    (N'SalesOrder',     N'UQ_SalesOrder_Source',                     'UQ'),
    (N'SalesOrder',     N'FK_SalesOrder_Customer',                   'F'),
    (N'SalesOrder',     N'FK_SalesOrder_ShipTo',                     'F'),
    (N'SalesOrder',     N'CK_SalesOrder_SalesOrderId_NotBlank',      'C'),
    (N'SalesOrder',     N'CK_SalesOrder_SourceSystem_NotBlank',      'C'),
    (N'SalesOrder',     N'CK_SalesOrder_ExternalOrderId_NotBlank',   'C'),
    (N'SalesOrder',     N'CK_SalesOrder_Status',                     'C'),
    (N'SalesOrderLine', N'PK_SalesOrderLine',                        'PK'),
    (N'SalesOrderLine', N'UQ_SalesOrderLine_Sequence',               'UQ'),
    (N'SalesOrderLine', N'FK_SalesOrderLine_SalesOrder',             'F'),
    (N'SalesOrderLine', N'FK_SalesOrderLine_Product',                'F'),
    (N'SalesOrderLine', N'CK_SalesOrderLine_SourceLineId_NotBlank',  'C'),
    (N'SalesOrderLine', N'CK_SalesOrderLine_SourceSku_NotBlank',     'C'),
    (N'SalesOrderLine', N'CK_SalesOrderLine_LineSequence_Positive',  'C'),
    (N'SalesOrderLine', N'CK_SalesOrderLine_Quantity_Positive',      'C');

SET @Actual = STUFF((
    SELECT N', ' + e.ConstraintName
    FROM @ExpectedConstraints AS e
    WHERE NOT EXISTS (SELECT 1 FROM sys.objects AS o
                      WHERE o.schema_id = SCHEMA_ID(N'erp')
                        AND o.name COLLATE DATABASE_DEFAULT = e.ConstraintName COLLATE DATABASE_DEFAULT
                        AND o.type COLLATE DATABASE_DEFAULT = e.ObjectType COLLATE DATABASE_DEFAULT
                        AND o.parent_object_id = OBJECT_ID(N'erp.' + e.TableName))
    ORDER BY e.ConstraintName
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S03', N'Structure', N'All 19 named keys, foreign keys and checks exist on their tables', N'none missing',
        COALESCE(N'missing: ' + @Actual, N'none missing'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

SET @Actual = STUFF((
    SELECT N', ' + e.IndexName
    FROM (VALUES (N'SalesOrder',     N'IX_SalesOrder_CustomerId'),
                 (N'SalesOrder',     N'IX_SalesOrder_ShipTo'),
                 (N'SalesOrderLine', N'IX_SalesOrderLine_ProductId')) AS e (TableName, IndexName)
    WHERE NOT EXISTS (SELECT 1 FROM sys.indexes AS i
                      WHERE i.object_id = OBJECT_ID(N'erp.' + e.TableName)
                        AND i.name COLLATE DATABASE_DEFAULT = e.IndexName COLLATE DATABASE_DEFAULT)
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S04', N'Structure', N'All 3 foreign-key support indexes exist', N'none missing',
        COALESCE(N'missing: ' + @Actual, N'none missing'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

SET @Actual = STUFF((
    SELECT N', ' + e.TableName + N'.' + e.ColumnName + N' (' + COALESCE(c.collation_name COLLATE DATABASE_DEFAULT, N'missing') + N')'
    FROM (VALUES (N'StockOnHand', N'WarehouseId'), (N'StockOnHand', N'ProductId'),
                 (N'SalesOrder', N'SalesOrderId'), (N'SalesOrder', N'SourceSystem'), (N'SalesOrder', N'ExternalOrderId'),
                 (N'SalesOrder', N'CustomerId'), (N'SalesOrder', N'ShipToLocationId'), (N'SalesOrder', N'Status'),
                 (N'SalesOrderLine', N'SalesOrderId'), (N'SalesOrderLine', N'SourceLineId'),
                 (N'SalesOrderLine', N'SourceSku'), (N'SalesOrderLine', N'ProductId')) AS e (TableName, ColumnName)
    LEFT JOIN sys.columns AS c
           ON c.object_id = OBJECT_ID(N'erp.' + e.TableName)
          AND c.name COLLATE DATABASE_DEFAULT = e.ColumnName COLLATE DATABASE_DEFAULT
    WHERE c.collation_name IS NULL
       OR c.collation_name COLLATE DATABASE_DEFAULT <> N'Latin1_General_100_CI_AS' COLLATE DATABASE_DEFAULT
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S05', N'Structure', N'All 12 identifier and status columns use Latin1_General_100_CI_AS (D9)', N'none different',
        COALESCE(N'different: ' + @Actual, N'none different'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(COUNT(*), N' match')
FROM sys.foreign_keys AS fk
JOIN sys.key_constraints AS kc
  ON kc.parent_object_id = fk.referenced_object_id
 AND kc.unique_index_id = fk.key_index_id
WHERE fk.parent_object_id = OBJECT_ID(N'erp.SalesOrder')
  AND fk.name COLLATE DATABASE_DEFAULT = N'FK_SalesOrder_ShipTo' COLLATE DATABASE_DEFAULT
  AND fk.referenced_object_id = OBJECT_ID(N'erp.CustomerLocation')
  AND kc.name COLLATE DATABASE_DEFAULT = N'UQ_CustomerLocation_Location_Customer' COLLATE DATABASE_DEFAULT;
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S06', N'Structure', N'Ship-to foreign key targets UQ_CustomerLocation_Location_Customer (location belongs to customer)',
        N'1 match', @Actual, CASE WHEN @Actual = N'1 match' THEN 1 ELSE 0 END);

SET @Actual = CONCAT(
    (SELECT COUNT(*) FROM sys.foreign_keys AS fk
     WHERE (OBJECT_SCHEMA_NAME(fk.parent_object_id) = N'erp' AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) <> N'erp')
        OR (OBJECT_SCHEMA_NAME(fk.parent_object_id) <> N'erp' AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) = N'erp')),
    N' foreign keys, ',
    (SELECT COUNT(*) FROM sys.sql_expression_dependencies AS d
     WHERE (OBJECT_SCHEMA_NAME(d.referencing_id) <> N'erp' AND d.referenced_schema_name = N'erp')
        OR (OBJECT_SCHEMA_NAME(d.referencing_id) = N'erp'
            AND (d.referenced_server_name IS NOT NULL
              OR d.referenced_database_name IS NOT NULL
              OR (d.referenced_schema_name IS NOT NULL AND d.referenced_schema_name <> N'erp')))),
    N' module references');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S07', N'Structure', N'No foreign key or SQL-module reference crosses the ERP model boundary', N'0 foreign keys, 0 module references',
        @Actual, CASE WHEN @Actual = N'0 foreign keys, 0 module references' THEN 1 ELSE 0 END);

/* ===== D: Synthetic data ================================================ */

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT e.CheckId, N'Data', N'Row count of erp.' + e.TableName, CONCAT(e.ExpectedRows, N' rows'),
       CONCAT(a.ActualRows, N' rows'), CASE WHEN a.ActualRows = e.ExpectedRows THEN 1 ELSE 0 END
FROM (VALUES ('D01', N'StockOnHand', 2), ('D02', N'SalesOrder', 1), ('D03', N'SalesOrderLine', 2)) AS e (CheckId, TableName, ExpectedRows)
JOIN (          SELECT N'StockOnHand' AS TableName, COUNT(*) AS ActualRows FROM erp.StockOnHand
      UNION ALL SELECT N'SalesOrder',     COUNT(*) FROM erp.SalesOrder
      UNION ALL SELECT N'SalesOrderLine', COUNT(*) FROM erp.SalesOrderLine) AS a
  ON a.TableName = e.TableName;

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'D04', N'Data', N'WEB-10001 from nopCommerce accepted as SO-0001: C0001, ship-to LOC-01, Received', N'1 match',
       CONCAT(COUNT(*), N' match'), CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END
FROM erp.SalesOrder AS o
WHERE o.SalesOrderId = 'SO-0001' AND o.SourceSystem = 'nopCommerce' AND o.ExternalOrderId = 'WEB-10001'
  AND o.CustomerId = 'C0001' AND o.ShipToLocationId = 'LOC-01' AND o.Status = 'Received';

/* The stored snapshot must reproduce the submitted lines in submitted order:
   OrderIntake compares SourceLineId, Sku and Quantity position by position. */
SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT l.LineSequence, l.SourceLineId, l.SourceSku, l.Quantity FROM erp.SalesOrderLine AS l WHERE l.SalesOrderId = 'SO-0001'
        EXCEPT
        SELECT v.LineSequence, v.SourceLineId, v.SourceSku, v.Quantity
        FROM (VALUES (1, '1', 'FD-DOG-0142', 3), (2, '2', 'FD-DOG-0143', 2)) AS v (LineSequence, SourceLineId, SourceSku, Quantity)) AS x),
    N' unexpected, ',
    (SELECT COUNT(*) FROM (
        SELECT v.LineSequence, v.SourceLineId, v.SourceSku, v.Quantity
        FROM (VALUES (1, '1', 'FD-DOG-0142', 3), (2, '2', 'FD-DOG-0143', 2)) AS v (LineSequence, SourceLineId, SourceSku, Quantity)
        EXCEPT
        SELECT l.LineSequence, l.SourceLineId, l.SourceSku, l.Quantity FROM erp.SalesOrderLine AS l WHERE l.SalesOrderId = 'SO-0001') AS x),
    N' missing');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D05', N'Data', N'Stored submission snapshot reproduces WEB-10001 lines in submitted order (D7)',
        N'0 unexpected, 0 missing', @Actual, CASE WHEN @Actual = N'0 unexpected, 0 missing' THEN 1 ELSE 0 END);

/* Each SourceSku must resolve, under the order's SourceSystem and without context,
   to exactly the ProductId stored on the line (OrderIntake's resolution outcome). */
SELECT @Actual = CONCAT(SUM(CASE WHEN r.Candidates = 1 AND r.OnlyProduct = l.ProductId THEN 1 ELSE 0 END),
                        N' of ', COUNT(*), N' lines')
FROM erp.SalesOrderLine AS l
JOIN erp.SalesOrder AS o ON o.SalesOrderId = l.SalesOrderId
CROSS APPLY (SELECT COUNT(DISTINCT pi.ProductId) AS Candidates, MIN(pi.ProductId) AS OnlyProduct
             FROM erp.ProductIdentity AS pi
             WHERE pi.[System] = o.SourceSystem AND pi.IdentifierType = 'SKU' AND pi.ExternalId = l.SourceSku) AS r;
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D06', N'Data', N'Every line SKU resolves (Found) to the product stored on the line', N'2 of 2 lines',
        @Actual, CASE WHEN @Actual = N'2 of 2 lines' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(COUNT(*), N' orders with gaps or no lines')
FROM erp.SalesOrder AS o
OUTER APPLY (SELECT COUNT(*) AS Lines, MIN(l.LineSequence) AS FirstSeq, MAX(l.LineSequence) AS LastSeq
             FROM erp.SalesOrderLine AS l WHERE l.SalesOrderId = o.SalesOrderId) AS s
WHERE s.Lines = 0 OR s.FirstSeq <> 1 OR s.LastSeq <> s.Lines;
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D07', N'Data', N'Every order has at least one line, numbered 1..n', N'0 orders with gaps or no lines',
        @Actual, CASE WHEN @Actual = N'0 orders with gaps or no lines' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT s.WarehouseId, s.ProductId, s.OnHand FROM erp.StockOnHand AS s
        EXCEPT
        SELECT v.WarehouseId, v.ProductId, v.OnHand FROM (VALUES ('WH-01', 'P000142', 10), ('WH-01', 'P000143', 5)) AS v (WarehouseId, ProductId, OnHand)) AS x),
    N' unexpected, ',
    (SELECT COUNT(*) FROM (
        SELECT v.WarehouseId, v.ProductId, v.OnHand FROM (VALUES ('WH-01', 'P000142', 10), ('WH-01', 'P000143', 5)) AS v (WarehouseId, ProductId, OnHand)
        EXCEPT
        SELECT s.WarehouseId, s.ProductId, s.OnHand FROM erp.StockOnHand AS s) AS x),
    N' missing');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D08', N'Data', N'Stock equals data/synthetic-stock.json', N'0 unexpected, 0 missing',
        @Actual, CASE WHEN @Actual = N'0 unexpected, 0 missing' THEN 1 ELSE 0 END);

/* ===== Q: Readiness for the Voxware increment ===========================
   Reproduces the outcome C# InventoryAvailability expects for SO-0001 at WH-01:
   demand aggregated per product; no stock row = Unknown; on hand below demand =
   Short; otherwise Available. Not an availability function; nothing is stored. */

;WITH Demand AS
(
    SELECT l.ProductId, SUM(l.Quantity) AS Requested
    FROM erp.SalesOrderLine AS l
    WHERE l.SalesOrderId = 'SO-0001'
    GROUP BY l.ProductId
),
Assessed AS
(
    SELECT d.ProductId, d.Requested, s.OnHand,
           CASE WHEN s.OnHand IS NULL THEN 2 WHEN s.OnHand < d.Requested THEN 1 ELSE 0 END AS Severity
    FROM Demand AS d
    LEFT JOIN erp.StockOnHand AS s ON s.WarehouseId = 'WH-01' AND s.ProductId = d.ProductId
)
SELECT @Actual = CONCAT(
    CASE MAX(a.Severity) WHEN 2 THEN N'Unknown' WHEN 1 THEN N'Short' WHEN 0 THEN N'Available' ELSE N'No demand' END,
    N': ',
    STUFF((SELECT N', ' + a2.ProductId + N' ' + CAST(a2.Requested AS nvarchar(12)) + N'/' + COALESCE(CAST(a2.OnHand AS nvarchar(12)), N'?')
           FROM Assessed AS a2 ORDER BY a2.ProductId
           FOR XML PATH(''), TYPE).value('.', 'nvarchar(400)'), 1, 2, N''))
FROM Assessed AS a;
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('Q01', N'Readiness', N'SO-0001 at WH-01 assesses Available (requested/on hand)', N'Available: P000142 3/10, P000143 2/5',
        @Actual, CASE WHEN @Actual = N'Available: P000142 3/10, P000143 2/5' THEN 1 ELSE 0 END);

/* samples/stage2-dispatch.json: SHIP-0001 for SO-0001 carries P000142 x3 and P000143 x2.
   EndToEnd invariant: shipment quantities equal accepted order quantities. */
SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT l.ProductId, SUM(l.Quantity) AS Quantity FROM erp.SalesOrderLine AS l WHERE l.SalesOrderId = 'SO-0001' GROUP BY l.ProductId
        EXCEPT
        SELECT v.ProductId, v.Quantity FROM (VALUES ('P000142', 3), ('P000143', 2)) AS v (ProductId, Quantity)) AS x),
    N' unexpected, ',
    (SELECT COUNT(*) FROM (
        SELECT v.ProductId, v.Quantity FROM (VALUES ('P000142', 3), ('P000143', 2)) AS v (ProductId, Quantity)
        EXCEPT
        SELECT l.ProductId, SUM(l.Quantity) FROM erp.SalesOrderLine AS l WHERE l.SalesOrderId = 'SO-0001' GROUP BY l.ProductId) AS x),
    N' missing');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('Q02', N'Readiness', N'SO-0001 product quantities equal the Stage 2 dispatch sample', N'0 unexpected, 0 missing',
        @Actual, CASE WHEN @Actual = N'0 unexpected, 0 missing' THEN 1 ELSE 0 END);

/* ===== C: Constraint behavior (each case rolled back) ===================
   ExpectedError NULL = the write must be accepted.
   Otherwise the error number must match and the message must name ExpectedText. */

DECLARE @Cases TABLE
(
    CheckId        varchar(10)   NOT NULL PRIMARY KEY,
    Description    nvarchar(300) NOT NULL,
    ExpectedError  int           NULL,
    ExpectedText   nvarchar(200) NULL,
    Sql            nvarchar(max) NOT NULL
);
INSERT INTO @Cases (CheckId, Description, ExpectedError, ExpectedText, Sql) VALUES
    ('C01', N'Same source order differing only in case is a duplicate', 2627, N'UQ_SalesOrder_Source',
        N'INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status) VALUES (''SO-9001'', ''NOPCOMMERCE'', ''web-10001'', ''C0001'', ''LOC-01'', ''Received'');'),
    ('C02', N'Sales order ID differing only in case is a duplicate', 2627, N'PK_SalesOrder',
        N'INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status) VALUES (''so-0001'', ''nopCommerce'', ''WEB-90001'', ''C0001'', ''LOC-01'', ''Received'');'),
    ('C03', N'Unknown customer rejected', 547, N'FK_SalesOrder_',
        N'INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status) VALUES (''SO-9001'', ''nopCommerce'', ''WEB-90001'', ''C9999'', ''LOC-01'', ''Received'');'),
    ('C04', N'Ship-to location of another customer rejected', 547, N'FK_SalesOrder_ShipTo',
        N'INSERT INTO erp.Customer (CustomerId, Name) VALUES (''C0002'', N''Second customer'');
          INSERT INTO erp.CustomerLocation (LocationId, CustomerId, Label) VALUES (''LOC-03'', ''C0002'', N''Other customer site'');
          INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status) VALUES (''SO-9001'', ''nopCommerce'', ''WEB-90001'', ''C0001'', ''LOC-03'', ''Received'');'),
    ('C05', N'Missing ship-to rejected', 515, N'ShipToLocationId',
        N'INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status) VALUES (''SO-9001'', ''nopCommerce'', ''WEB-90001'', ''C0001'', NULL, ''Received'');'),
    ('C06', N'Status other than Received rejected', 547, N'CK_SalesOrder_Status',
        N'INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status) VALUES (''SO-9001'', ''nopCommerce'', ''WEB-90001'', ''C0001'', ''LOC-01'', ''Accepted'');'),
    ('C07', N'Blank external order ID rejected', 547, N'CK_SalesOrder_ExternalOrderId_NotBlank',
        N'INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status) VALUES (''SO-9001'', ''nopCommerce'', '' '', ''C0001'', ''LOC-01'', ''Received'');'),
    ('C08', N'Same external order ID from another source system accepted', NULL, NULL,
        N'INSERT INTO erp.SalesOrder (SalesOrderId, SourceSystem, ExternalOrderId, CustomerId, ShipToLocationId, Status) VALUES (''SO-9001'', ''OtherSource'', ''WEB-10001'', ''C0001'', ''LOC-01'', ''Received'');'),
    ('C09', N'Nonpositive quantity rejected', 547, N'CK_SalesOrderLine_Quantity_Positive',
        N'INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (''SO-0001'', ''3'', 3, ''FD-DOG-0142'', ''P000142'', 0);'),
    ('C10', N'Duplicate line IDs rejected, ignoring case', 2627, N'PK_SalesOrderLine',
        N'INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (''SO-0001'', ''a1'', 3, ''FD-DOG-0142'', ''P000142'', 1);
          INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (''SO-0001'', ''A1'', 4, ''FD-DOG-0142'', ''P000142'', 1);'),
    ('C11', N'Duplicate line sequence within an order rejected', 2627, N'UQ_SalesOrderLine_Sequence',
        N'INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (''SO-0001'', ''3'', 1, ''FD-DOG-0142'', ''P000142'', 1);'),
    ('C12', N'Line sequence below 1 rejected', 547, N'CK_SalesOrderLine_LineSequence_Positive',
        N'INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (''SO-0001'', ''3'', 0, ''FD-DOG-0142'', ''P000142'', 1);'),
    ('C13', N'Blank SKU snapshot rejected', 547, N'CK_SalesOrderLine_SourceSku_NotBlank',
        N'INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (''SO-0001'', ''3'', 3, '''', ''P000142'', 1);'),
    ('C14', N'Line for unknown product rejected', 547, N'FK_SalesOrderLine_Product',
        N'INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (''SO-0001'', ''3'', 3, ''FD-DOG-9999'', ''P999999'', 1);'),
    ('C15', N'Line for unknown order rejected', 547, N'FK_SalesOrderLine_SalesOrder',
        N'INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (''SO-9999'', ''1'', 1, ''FD-DOG-0142'', ''P000142'', 1);'),
    ('C16', N'Repeated product on another line accepted (demand aggregates later)', NULL, NULL,
        N'INSERT INTO erp.SalesOrderLine (SalesOrderId, SourceLineId, LineSequence, SourceSku, ProductId, Quantity) VALUES (''SO-0001'', ''3'', 3, ''FD-DOG-0142'', ''P000142'', 1);'),
    ('C17', N'Negative stock rejected', 547, N'CK_StockOnHand_OnHand_NonNegative',
        N'UPDATE erp.StockOnHand SET OnHand = -1 WHERE WarehouseId = ''WH-01'' AND ProductId = ''P000142'';'),
    ('C18', N'Stock without an inventory position rejected', 547, N'FK_StockOnHand_InventoryPosition',
        N'INSERT INTO erp.Product (ProductId, Description, BaseUnit) VALUES (''P000144'', N''Unpositioned product'', ''Each'');
          INSERT INTO erp.StockOnHand (WarehouseId, ProductId, OnHand) VALUES (''WH-01'', ''P000144'', 1);'),
    ('C19', N'Duplicate stock snapshot rejected, ignoring case', 2627, N'PK_StockOnHand',
        N'INSERT INTO erp.StockOnHand (WarehouseId, ProductId, OnHand) VALUES (''wh-01'', ''p000142'', 1);'),
    ('C20', N'Zero stock is a known quantity and accepted', NULL, NULL,
        N'INSERT INTO erp.Product (ProductId, Description, BaseUnit) VALUES (''P000144'', N''Positioned product'', ''Each'');
          INSERT INTO erp.InventoryPosition (WarehouseId, ProductId) VALUES (''WH-01'', ''P000144'');
          INSERT INTO erp.StockOnHand (WarehouseId, ProductId, OnHand) VALUES (''WH-01'', ''P000144'', 0);');

DECLARE @CheckId varchar(10), @Description nvarchar(300), @ExpectedError int, @ExpectedText nvarchar(200),
        @Sql nvarchar(max), @Error int, @Message nvarchar(4000);

DECLARE case_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT CheckId, Description, ExpectedError, ExpectedText, Sql FROM @Cases ORDER BY CheckId;
OPEN case_cursor;
FETCH NEXT FROM case_cursor INTO @CheckId, @Description, @ExpectedError, @ExpectedText, @Sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @Error = 0, @Message = N'';
    BEGIN TRANSACTION;
    BEGIN TRY
        EXEC sys.sp_executesql @Sql;
    END TRY
    BEGIN CATCH
        SELECT @Error = ERROR_NUMBER(), @Message = ERROR_MESSAGE();
    END CATCH;
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

    INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
    VALUES (@CheckId, N'Constraint', @Description,
            CASE WHEN @ExpectedError IS NULL THEN N'accepted'
                 ELSE CONCAT(N'error ', @ExpectedError, N' naming ', @ExpectedText) END,
            CASE WHEN @Error = 0 THEN N'accepted'
                 ELSE CONCAT(N'error ', @Error, N': ', LEFT(@Message, 200)) END,
            CASE WHEN (@ExpectedError IS NULL AND @Error = 0)
                   OR (@Error = @ExpectedError AND CHARINDEX(@ExpectedText, @Message) > 0)
                 THEN 1 ELSE 0 END);

    FETCH NEXT FROM case_cursor INTO @CheckId, @Description, @ExpectedError, @ExpectedText, @Sql;
END;
CLOSE case_cursor;
DEALLOCATE case_cursor;

/* ===== Z: Repeatability ================================================= */

DECLARE @RowsAfter int =
      (SELECT COUNT(*) FROM erp.Customer)          + (SELECT COUNT(*) FROM erp.CustomerLocation)
    + (SELECT COUNT(*) FROM erp.Product)           + (SELECT COUNT(*) FROM erp.Supplier)
    + (SELECT COUNT(*) FROM erp.SupplierProduct)   + (SELECT COUNT(*) FROM erp.Warehouse)
    + (SELECT COUNT(*) FROM erp.InventoryPosition) + (SELECT COUNT(*) FROM erp.ProductIdentity)
    + (SELECT COUNT(*) FROM erp.StockOnHand)       + (SELECT COUNT(*) FROM erp.SalesOrder)
    + (SELECT COUNT(*) FROM erp.SalesOrderLine);

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('Z01', N'Repeatability', N'Verification left no rows behind in the 11 ERP tables and no open transaction',
        CONCAT(@RowsBefore, N' rows, 0 open transactions'),
        CONCAT(@RowsAfter, N' rows, ', @@TRANCOUNT, N' open transactions'),
        CASE WHEN @RowsAfter = @RowsBefore AND @@TRANCOUNT = 0 THEN 1 ELSE 0 END);

/* ===== Report =========================================================== */

SELECT CheckId,
       CASE Passed WHEN 1 THEN 'PASS' ELSE 'FAIL' END AS Result,
       Area, Description, Expected, LEFT(Actual, 250) AS Actual
FROM @Results
ORDER BY Seq;

DECLARE @Total int = (SELECT COUNT(*) FROM @Results);
DECLARE @Failed int = (SELECT COUNT(*) FROM @Results WHERE Passed = 0);
DECLARE @Summary nvarchar(200) = CONCAT(N'920-erp-verify-sales-inventory: ', @Total - @Failed, N'/', @Total, N' checks passed.');
PRINT @Summary;
IF @Failed > 0
    THROW 50003, @Summary, 1;
GO
