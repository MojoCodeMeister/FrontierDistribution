/*
    Frontier Distribution working model — ERP Business Application
    Increment 1: ERP foundation
    Script 910: Verification

    Read-only and repeatable: every constraint case runs in its own transaction
    and is rolled back; check Z01 confirms the data is unchanged afterwards.
    Returns one result set (one row per check) and raises error 50003 when any
    check fails, so sqlcmd -b exits non-zero.

    Areas
      S  Structure: schema, tables, constraints, indexes, collation, boundary
      D  Synthetic data: row counts and Stage 1 fixture facts
      R  Product identity resolution expectations from the Stage 1 harness
      Q  Readiness for increment 2 (Stage 2 order and stock fixtures)
      C  Constraint behavior: rejected and accepted writes, rolled back
      Z  Repeatability
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT OFF;  -- constraint cases must reach CATCH and roll back individually

IF @@TRANCOUNT > 0
    THROW 50002, N'Run 910-erp-verify-foundation.sql outside an open transaction.', 1;

DECLARE @Missing nvarchar(4000) =
(
    SELECT STUFF((
        SELECT N', erp.' + e.TableName
        FROM (VALUES (N'Customer'), (N'CustomerLocation'), (N'Product'), (N'Supplier'),
                     (N'SupplierProduct'), (N'Warehouse'), (N'InventoryPosition'), (N'ProductIdentity')) AS e (TableName)
        WHERE OBJECT_ID(N'erp.' + e.TableName, N'U') IS NULL
        FOR XML PATH(''), TYPE).value('.', 'nvarchar(4000)'), 1, 2, N'')
);
IF @Missing IS NOT NULL
BEGIN
    SET @Missing = N'ERP foundation tables missing: ' + @Missing + N'. Run scripts 010 to 110 first.';
    THROW 50001, @Missing, 1;
END;

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
      (SELECT COUNT(*) FROM erp.Customer)        + (SELECT COUNT(*) FROM erp.CustomerLocation)
    + (SELECT COUNT(*) FROM erp.Product)         + (SELECT COUNT(*) FROM erp.Supplier)
    + (SELECT COUNT(*) FROM erp.SupplierProduct) + (SELECT COUNT(*) FROM erp.Warehouse)
    + (SELECT COUNT(*) FROM erp.InventoryPosition) + (SELECT COUNT(*) FROM erp.ProductIdentity);

/* ===== S: Structure ===================================================== */

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'S01', N'Structure', N'Schema erp exists', N'present',
       CASE WHEN SCHEMA_ID(N'erp') IS NULL THEN N'missing' ELSE N'present' END,
       CASE WHEN SCHEMA_ID(N'erp') IS NULL THEN 0 ELSE 1 END;

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'S02', N'Structure', N'The 8 ERP foundation tables exist', N'8 tables',
       CONCAT(COUNT(*), N' tables'), CASE WHEN COUNT(*) = 8 THEN 1 ELSE 0 END
FROM sys.tables AS t
WHERE t.schema_id = SCHEMA_ID(N'erp')
  AND t.name IN (N'Customer', N'CustomerLocation', N'Product', N'Supplier',
                 N'SupplierProduct', N'Warehouse', N'InventoryPosition', N'ProductIdentity');

DECLARE @ExpectedConstraints TABLE (TableName sysname NOT NULL, ConstraintName sysname NOT NULL, ObjectType char(2) NOT NULL);
INSERT INTO @ExpectedConstraints (TableName, ConstraintName, ObjectType) VALUES
    (N'Customer',          N'PK_Customer',                                          'PK'),
    (N'Customer',          N'CK_Customer_CustomerId_NotBlank',                      'C'),
    (N'CustomerLocation',  N'PK_CustomerLocation',                                  'PK'),
    (N'CustomerLocation',  N'UQ_CustomerLocation_Location_Customer',                'UQ'),
    (N'CustomerLocation',  N'FK_CustomerLocation_Customer',                         'F'),
    (N'CustomerLocation',  N'CK_CustomerLocation_LocationId_NotBlank',              'C'),
    (N'Product',           N'PK_Product',                                           'PK'),
    (N'Product',           N'CK_Product_ProductId_NotBlank',                        'C'),
    (N'Supplier',          N'PK_Supplier',                                          'PK'),
    (N'Supplier',          N'CK_Supplier_SupplierId_NotBlank',                      'C'),
    (N'SupplierProduct',   N'PK_SupplierProduct',                                   'PK'),
    (N'SupplierProduct',   N'FK_SupplierProduct_Supplier',                          'F'),
    (N'SupplierProduct',   N'FK_SupplierProduct_Product',                           'F'),
    (N'SupplierProduct',   N'CK_SupplierProduct_BaseUnitsPerPurchaseUnit_Positive', 'C'),
    (N'Warehouse',         N'PK_Warehouse',                                         'PK'),
    (N'Warehouse',         N'CK_Warehouse_WarehouseId_NotBlank',                    'C'),
    (N'InventoryPosition', N'PK_InventoryPosition',                                 'PK'),
    (N'InventoryPosition', N'FK_InventoryPosition_Warehouse',                       'F'),
    (N'InventoryPosition', N'FK_InventoryPosition_Product',                         'F'),
    (N'ProductIdentity',   N'PK_ProductIdentity',                                   'PK'),
    (N'ProductIdentity',   N'UQ_ProductIdentity_Scope',                             'UQ'),
    (N'ProductIdentity',   N'FK_ProductIdentity_Product',                           'F'),
    (N'ProductIdentity',   N'CK_ProductIdentity_System_NotBlank',                   'C'),
    (N'ProductIdentity',   N'CK_ProductIdentity_IdentifierType_NotBlank',           'C'),
    (N'ProductIdentity',   N'CK_ProductIdentity_ExternalId_NotBlank',               'C');

SET @Actual = STUFF((
    SELECT N', ' + e.ConstraintName
    FROM @ExpectedConstraints AS e
    WHERE NOT EXISTS (SELECT 1 FROM sys.objects AS o
                      WHERE o.schema_id = SCHEMA_ID(N'erp')
                        AND o.name COLLATE Latin1_General_100_CI_AS = e.ConstraintName COLLATE Latin1_General_100_CI_AS
                        AND o.type COLLATE Latin1_General_100_CI_AS = e.ObjectType COLLATE Latin1_General_100_CI_AS
                        AND o.parent_object_id = OBJECT_ID(N'erp.' + e.TableName))
    ORDER BY e.ConstraintName
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S03', N'Structure', N'All 25 named keys, foreign keys and checks exist on their tables', N'none missing',
        COALESCE(N'missing: ' + @Actual, N'none missing'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

SET @Actual = STUFF((
    SELECT N', ' + e.IndexName
    FROM (VALUES (N'CustomerLocation',  N'IX_CustomerLocation_CustomerId'),
                 (N'SupplierProduct',   N'IX_SupplierProduct_SupplierId'),
                 (N'SupplierProduct',   N'IX_SupplierProduct_ProductId'),
                 (N'InventoryPosition', N'IX_InventoryPosition_ProductId'),
                 (N'ProductIdentity',   N'IX_ProductIdentity_ProductId')) AS e (TableName, IndexName)
    WHERE NOT EXISTS (SELECT 1 FROM sys.indexes AS i
                      WHERE i.object_id = OBJECT_ID(N'erp.' + e.TableName) AND i.name COLLATE Latin1_General_100_CI_AS = e.IndexName COLLATE Latin1_General_100_CI_AS)
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S04', N'Structure', N'All 5 foreign-key support indexes exist', N'none missing',
        COALESCE(N'missing: ' + @Actual, N'none missing'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

SET @Actual = STUFF((
    SELECT N', ' + e.TableName + N'.' + e.ColumnName + N' (' + COALESCE(c.collation_name, N'missing') + N')'
    FROM (VALUES (N'Customer', N'CustomerId'),
                 (N'CustomerLocation', N'LocationId'), (N'CustomerLocation', N'CustomerId'),
                 (N'Product', N'ProductId'),
                 (N'Supplier', N'SupplierId'),
                 (N'SupplierProduct', N'SupplierId'), (N'SupplierProduct', N'ProductId'), (N'SupplierProduct', N'SupplierItemNumber'),
                 (N'Warehouse', N'WarehouseId'),
                 (N'InventoryPosition', N'WarehouseId'), (N'InventoryPosition', N'ProductId'),
                 (N'ProductIdentity', N'ProductId'), (N'ProductIdentity', N'System'), (N'ProductIdentity', N'IdentifierType'),
                 (N'ProductIdentity', N'ExternalId'), (N'ProductIdentity', N'Context')) AS e (TableName, ColumnName)
    LEFT JOIN sys.columns AS c
           ON c.object_id = OBJECT_ID(N'erp.' + e.TableName) AND c.name COLLATE Latin1_General_100_CI_AS = e.ColumnName COLLATE Latin1_General_100_CI_AS
    WHERE c.collation_name IS NULL OR c.collation_name <> N'Latin1_General_100_CI_AS'
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S05', N'Structure', N'All 16 identifier columns use case-insensitive Latin1_General_100_CI_AS (D9)', N'none different',
        COALESCE(N'different: ' + @Actual, N'none different'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

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
VALUES ('S06', N'Structure', N'No foreign key or SQL-module reference crosses the ERP model boundary', N'0 foreign keys, 0 module references',
        @Actual, CASE WHEN @Actual = N'0 foreign keys, 0 module references' THEN 1 ELSE 0 END);

/* ===== D: Synthetic data (mirrors data/synthetic-identities.json) ====== */

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT e.CheckId, N'Data', N'Row count of erp.' + e.TableName, CONCAT(e.ExpectedRows, N' rows'),
       CONCAT(a.ActualRows, N' rows'), CASE WHEN a.ActualRows = e.ExpectedRows THEN 1 ELSE 0 END
FROM (VALUES ('D01', N'Customer', 1), ('D02', N'CustomerLocation', 2), ('D03', N'Product', 2),
             ('D04', N'Supplier', 2), ('D05', N'SupplierProduct', 3), ('D06', N'Warehouse', 1),
             ('D07', N'InventoryPosition', 2), ('D08', N'ProductIdentity', 8)) AS e (CheckId, TableName, ExpectedRows)
JOIN (          SELECT N'Customer' AS TableName, COUNT(*) AS ActualRows FROM erp.Customer
      UNION ALL SELECT N'CustomerLocation',  COUNT(*) FROM erp.CustomerLocation
      UNION ALL SELECT N'Product',           COUNT(*) FROM erp.Product
      UNION ALL SELECT N'Supplier',          COUNT(*) FROM erp.Supplier
      UNION ALL SELECT N'SupplierProduct',   COUNT(*) FROM erp.SupplierProduct
      UNION ALL SELECT N'Warehouse',         COUNT(*) FROM erp.Warehouse
      UNION ALL SELECT N'InventoryPosition', COUNT(*) FROM erp.InventoryPosition
      UNION ALL SELECT N'ProductIdentity',   COUNT(*) FROM erp.ProductIdentity) AS a
  ON a.TableName COLLATE Latin1_General_100_CI_AS = e.TableName COLLATE Latin1_General_100_CI_AS;

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'D09', N'Data', N'Multiple suppliers for one product (P000142)', N'2 suppliers',
       CONCAT(COUNT(DISTINCT sp.SupplierId), N' suppliers'), CASE WHEN COUNT(DISTINCT sp.SupplierId) = 2 THEN 1 ELSE 0 END
FROM erp.SupplierProduct AS sp
WHERE sp.ProductId = 'P000142';

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'D10', N'Data', N'Multiple locations for one customer (C0001)', N'2 locations',
       CONCAT(COUNT(*), N' locations'), CASE WHEN COUNT(*) = 2 THEN 1 ELSE 0 END
FROM erp.CustomerLocation AS cl
WHERE cl.CustomerId = 'C0001';

/* ===== R: Identity resolution expectations ===============================
   Reproduces the outcomes the Stage 1 harness expects from the C# resolver,
   against stored rows: candidates match System, IdentifierType and ExternalId;
   a supplied Context must match; an omitted Context matches every scope.
   0 products = NotFound, 1 = Found, more = Ambiguous. Not a resolver. */

DECLARE @Resolution TABLE
(
    CheckId         varchar(10)  NOT NULL,
    Description     nvarchar(300) NOT NULL,
    [System]        varchar(80)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    IdentifierType  varchar(80)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    ExternalId      varchar(150) COLLATE Latin1_General_100_CI_AS NOT NULL,
    [Context]       varchar(150) COLLATE Latin1_General_100_CI_AS NULL,
    ExpectedResult  varchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL
);
INSERT INTO @Resolution (CheckId, Description, [System], IdentifierType, ExternalId, [Context], ExpectedResult) VALUES
    ('R01', N'ERP identifier resolves',                                   'ERP',         'ItemNumber', 'ITEM-000142', NULL,    'Found:P000142'),
    ('R02', N'Webstore identifier resolves same canonical product',      'nopCommerce', 'SKU',        'FD-DOG-0142', NULL,    'Found:P000142'),
    ('R03', N'Unknown identifier is NotFound',                           'ERP',         'ItemNumber', 'BAD',         NULL,    'NotFound'),
    ('R04', N'Supplier-context ambiguity surfaced (2 candidates)',       'Supplier',    'ItemNumber', 'SHARED-01',   NULL,    'Ambiguous:P000142,P000143'),
    ('R05', N'Context disambiguates (SUP-1)',                            'Supplier',    'ItemNumber', 'SHARED-01',   'SUP-1', 'Found:P000142'),
    ('R06', N'Context lookup remains selective (SUP-2)',                 'Supplier',    'ItemNumber', 'SHARED-01',   'SUP-2', 'Found:P000143'),
    ('R07', N'Identifier lookup ignores case (D9)',                      'NOPCOMMERCE', 'sku',        'fd-dog-0142', NULL,    'Found:P000142'),
    ('R08', N'Context lookup ignores case (D9)',                         'Supplier',    'ItemNumber', 'shared-01',   'sup-2', 'Found:P000143'),
    ('R09', N'Supplied context does not match a NULL-context identity',  'ERP',         'ItemNumber', 'ITEM-000142', 'SUP-1', 'NotFound'),
    ('R10', N'Second order SKU resolves (WEB-10001 line 2)',             'nopCommerce', 'SKU',        'FD-DOG-0143', NULL,    'Found:P000143');

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT c.CheckId, N'Resolution', c.Description, c.ExpectedResult, r.Actual,
       CASE WHEN r.Actual = c.ExpectedResult THEN 1 ELSE 0 END
FROM @Resolution AS c
CROSS APPLY
(
    SELECT CASE COUNT(DISTINCT pi.ProductId)
               WHEN 0 THEN CAST('NotFound' AS varchar(200))
               WHEN 1 THEN 'Found:' + MIN(pi.ProductId)
               WHEN 2 THEN 'Ambiguous:' + MIN(pi.ProductId) + ',' + MAX(pi.ProductId)
               ELSE CONCAT('Ambiguous:', COUNT(DISTINCT pi.ProductId), ' products')
           END AS Actual
    FROM erp.ProductIdentity AS pi
    WHERE pi.[System] = c.[System]
      AND pi.IdentifierType = c.IdentifierType
      AND pi.ExternalId = c.ExternalId
      AND (c.[Context] IS NULL OR pi.[Context] = c.[Context])
) AS r;

/* ===== Q: Readiness for increment 2 ===================================== */

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'Q01', N'Readiness', N'Order WEB-10001 customer C0001 owns ship-to LOC-01', N'1 match',
       CONCAT(COUNT(*), N' match'), CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END
FROM erp.Customer AS c
JOIN erp.CustomerLocation AS cl ON cl.CustomerId = c.CustomerId
WHERE c.CustomerId = 'C0001' AND cl.LocationId = 'LOC-01';

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'Q02', N'Readiness', N'Stock fixture positions exist: WH-01 for P000142 and P000143', N'2 positions',
       CONCAT(COUNT(*), N' positions'), CASE WHEN COUNT(*) = 2 THEN 1 ELSE 0 END
FROM erp.InventoryPosition AS ip
WHERE ip.WarehouseId = 'WH-01' AND ip.ProductId IN ('P000142', 'P000143');

/* ===== C: Constraint behavior (each case rolled back) ===================
   ExpectedError NULL = the write must be accepted.
   Otherwise the error number and the named constraint or column must match. */

DECLARE @Cases TABLE
(
    CheckId        varchar(10)   NOT NULL PRIMARY KEY,
    Description    nvarchar(300) NOT NULL,
    ExpectedError  int           NULL,
    ExpectedText   nvarchar(200) NULL,
    Sql            nvarchar(max) NOT NULL
);
INSERT INTO @Cases (CheckId, Description, ExpectedError, ExpectedText, Sql) VALUES
    ('C01', N'Product ID differing only in case is a duplicate', 2627, N'PK_Product',
        N'INSERT INTO erp.Product (ProductId, Description, BaseUnit) VALUES (''p000142'', N''Case variant'', ''Each'');'),
    ('C02', N'Empty product ID is rejected', 547, N'CK_Product_ProductId_NotBlank',
        N'INSERT INTO erp.Product (ProductId, Description, BaseUnit) VALUES ('''', N''Blank'', ''Each'');'),
    ('C03', N'Space-only customer ID is rejected', 547, N'CK_Customer_CustomerId_NotBlank',
        N'INSERT INTO erp.Customer (CustomerId, Name) VALUES (''   '', N''Blank'');'),
    ('C04', N'Orphan references rejected (location for unknown customer)', 547, N'FK_CustomerLocation_Customer',
        N'INSERT INTO erp.CustomerLocation (LocationId, CustomerId, Label) VALUES (''BAD-LOC'', ''NO-CUSTOMER'', N''Invalid'');'),
    ('C05', N'Location ID is unique across customers, ignoring case', 2627, N'PK_CustomerLocation',
        N'INSERT INTO erp.Customer (CustomerId, Name) VALUES (''C0002'', N''Second customer'');
          INSERT INTO erp.CustomerLocation (LocationId, CustomerId, Label) VALUES (''loc-01'', ''C0002'', N''Reused location id'');'),
    ('C06', N'Purchasing conversion must be positive', 547, N'CK_SupplierProduct_BaseUnitsPerPurchaseUnit_Positive',
        N'INSERT INTO erp.SupplierProduct (SupplierId, ProductId, SupplierItemNumber, PurchaseUnit, BaseUnitsPerPurchaseUnit) VALUES (''SUP-1'', ''P000143'', ''ZERO-01'', ''Case'', 0);'),
    ('C07', N'Supplier product for unknown supplier is rejected', 547, N'FK_SupplierProduct_Supplier',
        N'INSERT INTO erp.SupplierProduct (SupplierId, ProductId, SupplierItemNumber, PurchaseUnit, BaseUnitsPerPurchaseUnit) VALUES (''SUP-9'', ''P000142'', ''X-01'', ''Each'', 1);'),
    ('C08', N'Identity for unknown product is rejected', 547, N'FK_ProductIdentity_Product',
        N'INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context]) VALUES (''P999999'', ''ERP'', ''ItemNumber'', ''ITEM-999999'', NULL);'),
    ('C09', N'Duplicate fully scoped identity rejected (NULL context)', 2627, N'UQ_ProductIdentity_Scope',
        N'INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context]) VALUES (''P000142'', ''ERP'', ''ItemNumber'', ''ITEM-000142'', NULL);'),
    ('C10', N'Conflicting fully scoped identity rejected (Supplier SHARED-01, SUP-2)', 2627, N'UQ_ProductIdentity_Scope',
        N'INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context]) VALUES (''P000142'', ''Supplier'', ''ItemNumber'', ''SHARED-01'', ''SUP-2'');'),
    ('C11', N'Scoped identity differing only in case is a duplicate (D9)', 2627, N'UQ_ProductIdentity_Scope',
        N'INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context]) VALUES (''P000143'', ''NOPCOMMERCE'', ''sku'', ''fd-dog-0142'', NULL);'),
    ('C12', N'Context differing only in case is the same scope (D9)', 2627, N'UQ_ProductIdentity_Scope',
        N'INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context]) VALUES (''P000143'', ''Supplier'', ''ItemNumber'', ''SHARED-01'', ''sup-1'');'),
    ('C13', N'Blank identity value is rejected', 547, N'CK_ProductIdentity_ExternalId_NotBlank',
        N'INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context]) VALUES (''P000142'', ''ERP'', ''ItemNumber'', '''', NULL);'),
    ('C14', N'Inventory position differing only in case is a duplicate', 2627, N'PK_InventoryPosition',
        N'INSERT INTO erp.InventoryPosition (WarehouseId, ProductId) VALUES (''wh-01'', ''p000142'');'),
    ('C15', N'Inventory position for unknown warehouse is rejected', 547, N'FK_InventoryPosition_Warehouse',
        N'INSERT INTO erp.InventoryPosition (WarehouseId, ProductId) VALUES (''WH-99'', ''P000142'');'),
    ('C16', N'Customer name is required', 515, N'Name',
        N'INSERT INTO erp.Customer (CustomerId, Name) VALUES (''C0003'', NULL);'),
    ('C17', N'Same identifier under a new context is accepted', NULL, NULL,
        N'INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context]) VALUES (''P000143'', ''Supplier'', ''ItemNumber'', ''SHARED-01'', ''SUP-3'');'),
    ('C18', N'NULL context is its own scope beside supplied contexts', NULL, NULL,
        N'INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context]) VALUES (''P000142'', ''Supplier'', ''ItemNumber'', ''SHARED-01'', NULL);'),
    ('C19', N'Same value in another namespace is accepted', NULL, NULL,
        N'INSERT INTO erp.ProductIdentity (ProductId, [System], IdentifierType, ExternalId, [Context]) VALUES (''P000143'', ''Warehouse'', ''ItemRef'', ''FD-DOG-0142'', NULL);'),
    ('C20', N'Second purchase configuration for the same supplier and product is accepted', NULL, NULL,
        N'INSERT INTO erp.SupplierProduct (SupplierId, ProductId, SupplierItemNumber, PurchaseUnit, BaseUnitsPerPurchaseUnit) VALUES (''SUP-1'', ''P000142'', ''SHARED-01-PAL'', ''Pallet'', 48);');

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
                 ELSE CONCAT(N'error ', @Error, N': ', LEFT(@Message, 400)) END,
            CASE WHEN (@ExpectedError IS NULL AND @Error = 0)
                   OR (@Error = @ExpectedError AND CHARINDEX(@ExpectedText, @Message) > 0)
                 THEN 1 ELSE 0 END);

    FETCH NEXT FROM case_cursor INTO @CheckId, @Description, @ExpectedError, @ExpectedText, @Sql;
END;
CLOSE case_cursor;
DEALLOCATE case_cursor;

/* ===== Z: Repeatability ================================================= */

DECLARE @RowsAfter int =
      (SELECT COUNT(*) FROM erp.Customer)        + (SELECT COUNT(*) FROM erp.CustomerLocation)
    + (SELECT COUNT(*) FROM erp.Product)         + (SELECT COUNT(*) FROM erp.Supplier)
    + (SELECT COUNT(*) FROM erp.SupplierProduct) + (SELECT COUNT(*) FROM erp.Warehouse)
    + (SELECT COUNT(*) FROM erp.InventoryPosition) + (SELECT COUNT(*) FROM erp.ProductIdentity);

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('Z01', N'Repeatability', N'Verification left no rows behind and no open transaction',
        CONCAT(@RowsBefore, N' rows, 0 open transactions'),
        CONCAT(@RowsAfter, N' rows, ', @@TRANCOUNT, N' open transactions'),
        CASE WHEN @RowsAfter = @RowsBefore AND @@TRANCOUNT = 0 THEN 1 ELSE 0 END);

/* ===== Report =========================================================== */

SELECT CheckId, Area, Description, Expected, Actual,
       CASE Passed WHEN 1 THEN 'PASS' ELSE 'FAIL' END AS Result
FROM @Results
ORDER BY Seq;

DECLARE @Total int = (SELECT COUNT(*) FROM @Results);
DECLARE @Failed int = (SELECT COUNT(*) FROM @Results WHERE Passed = 0);
DECLARE @Summary nvarchar(200) = CONCAT(N'910-erp-verify-foundation: ', @Total - @Failed, N'/', @Total, N' checks passed.');
PRINT @Summary;
IF @Failed > 0
    THROW 50003, @Summary, 1;
GO
