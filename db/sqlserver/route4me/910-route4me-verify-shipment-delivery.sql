/*
    Frontier Distribution working model - Route4Me Business Application
    Increment 4: Route4Me shipment, dispatch and delivery
    Script 910: Verification - expected result 53/53

    Reads the Route4Me model only; needs nothing from ERP or Voxware.
    Read-only and repeatable: every constraint case runs in its own transaction
    and is rolled back; check Z01 confirms nothing changed. Returns one result set
    (one row per check) and raises error 50003 when any check fails, so
    sqlcmd -b exits with code 1.

    Collation-safe: every catalog value (names, sys.objects.type, which carries a
    fixed system collation) is compared with COLLATE DATABASE_DEFAULT on both
    sides. Literals compared with Route4Me columns take the column collation.
    No #temp tables. ASCII only, for sqlcmd under Windows PowerShell 5.1.

    Areas
      S  Structure (9): tables, constraints, keys, dispatch index, collation,
         foreign keys, index support, model boundary
      D  Data (11): counts, the SO-0001 records, and the C# rules the tables do
         not enforce
      I  Integration references (2): ERP identifiers and Voxware references carried
      C  Constraint behavior (30): rejected and accepted writes, rolled back
      Z  Repeatability (1)
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT OFF;  -- constraint cases must reach CATCH and roll back individually

IF @@TRANCOUNT > 0
    THROW 50002, N'Run 910-route4me-verify-shipment-delivery.sql outside an open transaction.', 1;

DECLARE @Missing nvarchar(4000) =
(
    SELECT STUFF((
        SELECT N', route4me.' + e.TableName
        FROM (VALUES (N'Shipment'), (N'ShipmentLine'), (N'DeliveryShipment'), (N'DeliveryShipmentLine'),
                     (N'DeliveryEvent')) AS e (TableName)
        WHERE OBJECT_ID(N'route4me.' + e.TableName, N'U') IS NULL
        FOR XML PATH(''), TYPE).value('.', 'nvarchar(4000)'), 1, 2, N'')
);
IF @Missing IS NOT NULL
BEGIN
    SET @Missing = N'Route4Me tables missing: ' + @Missing + N'. Run install-route4me-shipment-delivery.sql first.';
    THROW 50001, @Missing, 1;
END;

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
      (SELECT COUNT(*) FROM route4me.Shipment)         + (SELECT COUNT(*) FROM route4me.ShipmentLine)
    + (SELECT COUNT(*) FROM route4me.DeliveryShipment) + (SELECT COUNT(*) FROM route4me.DeliveryShipmentLine)
    + (SELECT COUNT(*) FROM route4me.DeliveryEvent);

/* ===== S: Structure ===================================================== */

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'S01', N'Structure', N'Schema route4me exists', N'present',
       CASE WHEN SCHEMA_ID(N'route4me') IS NULL THEN N'missing' ELSE N'present' END,
       CASE WHEN SCHEMA_ID(N'route4me') IS NULL THEN 0 ELSE 1 END;

SELECT @Actual = CONCAT(
    SUM(CASE WHEN t.name COLLATE DATABASE_DEFAULT IN (N'Shipment', N'ShipmentLine', N'DeliveryShipment',
                  N'DeliveryShipmentLine', N'DeliveryEvent') THEN 1 ELSE 0 END),
    N' expected, ',
    SUM(CASE WHEN t.name COLLATE DATABASE_DEFAULT IN (N'Shipment', N'ShipmentLine', N'DeliveryShipment',
                  N'DeliveryShipmentLine', N'DeliveryEvent') THEN 0 ELSE 1 END),
    N' other')
FROM sys.tables AS t
WHERE t.schema_id = SCHEMA_ID(N'route4me');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S02', N'Structure', N'Schema route4me holds exactly the 5 shipment and delivery tables (no ERP or Voxware copies)',
        N'5 expected, 0 other', @Actual, CASE WHEN @Actual = N'5 expected, 0 other' THEN 1 ELSE 0 END);

DECLARE @ExpectedConstraints TABLE (TableName sysname NOT NULL, ConstraintName sysname NOT NULL, ObjectType char(2) NOT NULL);
INSERT INTO @ExpectedConstraints (TableName, ConstraintName, ObjectType) VALUES
    (N'Shipment',             N'PK_Shipment',                               'PK'),
    (N'Shipment',             N'UQ_Shipment_Order',                         'UQ'),
    (N'Shipment',             N'CK_Shipment_ShipmentId_NotBlank',           'C'),
    (N'Shipment',             N'CK_Shipment_SalesOrderId_NotBlank',         'C'),
    (N'Shipment',             N'CK_Shipment_WarehouseId_NotBlank',          'C'),
    (N'Shipment',             N'CK_Shipment_PackingId_NotBlank',            'C'),
    (N'Shipment',             N'CK_Shipment_PickConfirmationId_NotBlank',   'C'),
    (N'Shipment',             N'CK_Shipment_Status',                        'C'),
    (N'ShipmentLine',         N'PK_ShipmentLine',                           'PK'),
    (N'ShipmentLine',         N'FK_ShipmentLine_Shipment',                  'F'),
    (N'ShipmentLine',         N'CK_ShipmentLine_ProductId_NotBlank',        'C'),
    (N'ShipmentLine',         N'CK_ShipmentLine_Quantity_Positive',         'C'),
    (N'DeliveryShipment',     N'PK_DeliveryShipment',                       'PK'),
    (N'DeliveryShipment',     N'CK_DeliveryShipment_ShipmentId_NotBlank',   'C'),
    (N'DeliveryShipment',     N'CK_DeliveryShipment_SalesOrderId_NotBlank', 'C'),
    (N'DeliveryShipment',     N'CK_DeliveryShipment_WarehouseId_NotBlank',  'C'),
    (N'DeliveryShipmentLine', N'PK_DeliveryShipmentLine',                   'PK'),
    (N'DeliveryShipmentLine', N'FK_DeliveryShipmentLine_DeliveryShipment',  'F'),
    (N'DeliveryShipmentLine', N'CK_DeliveryShipmentLine_ProductId_NotBlank', 'C'),
    (N'DeliveryShipmentLine', N'CK_DeliveryShipmentLine_Quantity_Positive', 'C'),
    (N'DeliveryEvent',        N'PK_DeliveryEvent',                          'PK'),
    (N'DeliveryEvent',        N'UQ_DeliveryEvent_Shipment',                 'UQ'),
    (N'DeliveryEvent',        N'FK_DeliveryEvent_DeliveryShipment',         'F'),
    (N'DeliveryEvent',        N'CK_DeliveryEvent_EventId_NotBlank',         'C'),
    (N'DeliveryEvent',        N'CK_DeliveryEvent_Outcome',                  'C'),
    (N'DeliveryEvent',        N'CK_DeliveryEvent_OccurredAt_Required',      'C');

SET @Actual = STUFF((
    SELECT N', ' + e.ConstraintName
    FROM @ExpectedConstraints AS e
    WHERE NOT EXISTS (SELECT 1 FROM sys.objects AS o
                      WHERE o.schema_id = SCHEMA_ID(N'route4me')
                        AND o.name COLLATE DATABASE_DEFAULT = e.ConstraintName COLLATE DATABASE_DEFAULT
                        AND o.type COLLATE DATABASE_DEFAULT = e.ObjectType COLLATE DATABASE_DEFAULT
                        AND o.parent_object_id = OBJECT_ID(N'route4me.' + e.TableName))
    ORDER BY e.ConstraintName
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S03', N'Structure', N'All 26 named keys, foreign keys and checks exist on their tables', N'none missing',
        COALESCE(N'missing: ' + @Actual, N'none missing'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

/* Every unique key, in designed column order: one shipment per order and
   warehouse, ShipmentId, DispatchId, EventId and one outcome per shipment. */
DECLARE @ExpectedKeys TABLE (TableName sysname NOT NULL, IndexName sysname NOT NULL, KeyColumns nvarchar(400) NOT NULL);
INSERT INTO @ExpectedKeys (TableName, IndexName, KeyColumns) VALUES
    (N'Shipment',             N'PK_Shipment',               N'ShipmentId'),
    (N'Shipment',             N'UQ_Shipment_Order',         N'SalesOrderId, WarehouseId'),
    (N'Shipment',             N'UX_Shipment_DispatchId',    N'DispatchId'),
    (N'ShipmentLine',         N'PK_ShipmentLine',           N'ShipmentId, ProductId'),
    (N'DeliveryShipment',     N'PK_DeliveryShipment',       N'ShipmentId'),
    (N'DeliveryShipmentLine', N'PK_DeliveryShipmentLine',   N'ShipmentId, ProductId'),
    (N'DeliveryEvent',        N'PK_DeliveryEvent',          N'EventId'),
    (N'DeliveryEvent',        N'UQ_DeliveryEvent_Shipment', N'ShipmentId');

SET @Actual = STUFF((
    SELECT N'; ' + e.IndexName + N' (' + COALESCE(a.KeyColumns, N'missing') + N')'
    FROM @ExpectedKeys AS e
    OUTER APPLY (SELECT STUFF((
                     SELECT N', ' + c.name
                     FROM sys.indexes AS i
                     JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
                     JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                     WHERE i.object_id = OBJECT_ID(N'route4me.' + e.TableName)
                       AND i.is_unique = 1
                       AND i.name COLLATE DATABASE_DEFAULT = e.IndexName COLLATE DATABASE_DEFAULT
                     ORDER BY ic.key_ordinal
                     FOR XML PATH(''), TYPE).value('.', 'nvarchar(400)'), 1, 2, N'') AS KeyColumns) AS a
    WHERE a.KeyColumns IS NULL
       OR a.KeyColumns COLLATE DATABASE_DEFAULT <> e.KeyColumns COLLATE DATABASE_DEFAULT
    ORDER BY e.IndexName
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S04', N'Structure', N'All 8 unique keys exist with their designed columns', N'none different',
        COALESCE(N'different: ' + @Actual, N'none different'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

DECLARE @DispatchIndex nvarchar(40) =
    COALESCE((SELECT CONCAT(CASE WHEN i.is_unique = 1 THEN N'unique' ELSE N'not unique' END, N', ',
                            CASE WHEN i.has_filter = 1 THEN N'filtered' ELSE N'not filtered' END)
              FROM sys.indexes AS i
              WHERE i.object_id = OBJECT_ID(N'route4me.Shipment')
                AND i.name COLLATE DATABASE_DEFAULT = N'UX_Shipment_DispatchId' COLLATE DATABASE_DEFAULT), N'missing');
SET @Actual = CONCAT(@DispatchIndex, N'; ',
    (SELECT i.filter_definition FROM sys.indexes AS i
     WHERE i.object_id = OBJECT_ID(N'route4me.Shipment')
       AND i.name COLLATE DATABASE_DEFAULT = N'UX_Shipment_DispatchId' COLLATE DATABASE_DEFAULT));
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S05', N'Structure', N'UX_Shipment_DispatchId is unique over recorded dispatches only (filtered); behavior in C09, C10',
        N'unique, filtered', @Actual, CASE WHEN @DispatchIndex = N'unique, filtered' THEN 1 ELSE 0 END);

SET @Actual = STUFF((
    SELECT N', ' + e.TableName + N'.' + e.ColumnName + N' (' + COALESCE(c.collation_name COLLATE DATABASE_DEFAULT, N'missing') + N')'
    FROM (VALUES (N'Shipment', N'ShipmentId'), (N'Shipment', N'SalesOrderId'), (N'Shipment', N'WarehouseId'),
                 (N'Shipment', N'PackingId'), (N'Shipment', N'PickConfirmationId'), (N'Shipment', N'Status'),
                 (N'Shipment', N'DispatchId'),
                 (N'ShipmentLine', N'ShipmentId'), (N'ShipmentLine', N'ProductId'),
                 (N'DeliveryShipment', N'ShipmentId'), (N'DeliveryShipment', N'SalesOrderId'),
                 (N'DeliveryShipment', N'WarehouseId'), (N'DeliveryShipment', N'Status'),
                 (N'DeliveryShipmentLine', N'ShipmentId'), (N'DeliveryShipmentLine', N'ProductId'),
                 (N'DeliveryEvent', N'EventId'), (N'DeliveryEvent', N'ShipmentId'), (N'DeliveryEvent', N'Outcome'),
                 (N'DeliveryEvent', N'ProofReference')) AS e (TableName, ColumnName)
    LEFT JOIN sys.columns AS c
           ON c.object_id = OBJECT_ID(N'route4me.' + e.TableName)
          AND c.name COLLATE DATABASE_DEFAULT = e.ColumnName COLLATE DATABASE_DEFAULT
    WHERE c.collation_name IS NULL
       OR c.collation_name COLLATE DATABASE_DEFAULT <> N'Latin1_General_100_CI_AS' COLLATE DATABASE_DEFAULT
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S06', N'Structure', N'All 19 identifier, status and outcome columns use Latin1_General_100_CI_AS (D9; no BIN2)', N'none different',
        COALESCE(N'different: ' + @Actual, N'none different'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

/* Lines belong to their headers and outcomes to delivery shipments. The
   Shipping -> Delivery handoff is a component contract: no foreign key joins
   the Shipping tables to the Delivery tables. */
SELECT @Actual = CONCAT(
    SUM(CASE WHEN e.ChildTable IS NULL THEN 0 ELSE 1 END), N' as designed, ',
    SUM(CASE WHEN e.ChildTable IS NULL THEN 1 ELSE 0 END), N' other')
FROM sys.foreign_keys AS fk
LEFT JOIN (VALUES (N'ShipmentLine', N'Shipment'), (N'DeliveryShipmentLine', N'DeliveryShipment'),
                  (N'DeliveryEvent', N'DeliveryShipment')) AS e (ChildTable, ParentTable)
       ON OBJECT_NAME(fk.parent_object_id) COLLATE DATABASE_DEFAULT = e.ChildTable COLLATE DATABASE_DEFAULT
      AND OBJECT_NAME(fk.referenced_object_id) COLLATE DATABASE_DEFAULT = e.ParentTable COLLATE DATABASE_DEFAULT
      AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) = N'route4me'
WHERE OBJECT_SCHEMA_NAME(fk.parent_object_id) = N'route4me';
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S07', N'Structure', N'Foreign keys exactly as designed; none between the Shipping and Delivery tables',
        N'3 as designed, 0 other', @Actual, CASE WHEN @Actual = N'3 as designed, 0 other' THEN 1 ELSE 0 END);

/* A foreign key is supported when its columns lead an index on the child table,
   or a unique index on the child is a leading prefix of them. */
SELECT @Actual = CONCAT(SUM(CASE WHEN s.Supported = 0 THEN 1 ELSE 0 END), N' unsupported of ', COUNT(*), N' foreign keys')
FROM sys.foreign_keys AS fk
CROSS APPLY
(
    SELECT CASE WHEN EXISTS
    (
        SELECT 1
        FROM sys.indexes AS i
        CROSS APPLY (SELECT
            (SELECT COUNT(*) FROM sys.foreign_key_columns AS fkc WHERE fkc.constraint_object_id = fk.object_id) AS FkColumns,
            (SELECT COUNT(*) FROM sys.index_columns AS ic
             WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0) AS IndexColumns,
            (SELECT COUNT(*) FROM sys.foreign_key_columns AS fkc
             JOIN sys.index_columns AS ic
               ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              AND ic.key_ordinal = fkc.constraint_column_id AND ic.column_id = fkc.parent_column_id
             WHERE fkc.constraint_object_id = fk.object_id) AS Matched) AS m
        WHERE i.object_id = fk.parent_object_id
          AND (m.Matched = m.FkColumns OR (i.is_unique = 1 AND m.IndexColumns > 0 AND m.Matched = m.IndexColumns))
    ) THEN 1 ELSE 0 END AS Supported
) AS s
WHERE OBJECT_SCHEMA_NAME(fk.parent_object_id) = N'route4me';
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S08', N'Structure', N'Every foreign key is supported by an index on its child table', N'0 unsupported of 3 foreign keys',
        @Actual, CASE WHEN @Actual = N'0 unsupported of 3 foreign keys' THEN 1 ELSE 0 END);

SET @Actual = CONCAT(
    (SELECT COUNT(*) FROM sys.foreign_keys AS fk
     WHERE (OBJECT_SCHEMA_NAME(fk.parent_object_id) = N'route4me' AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) <> N'route4me')
        OR (OBJECT_SCHEMA_NAME(fk.parent_object_id) <> N'route4me' AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) = N'route4me')),
    N' foreign keys, ',
    (SELECT COUNT(*) FROM sys.sql_expression_dependencies AS d
     WHERE (OBJECT_SCHEMA_NAME(d.referencing_id) <> N'route4me' AND d.referenced_schema_name = N'route4me')
        OR (OBJECT_SCHEMA_NAME(d.referencing_id) = N'route4me'
            AND (d.referenced_server_name IS NOT NULL
              OR d.referenced_database_name IS NOT NULL
              OR (d.referenced_schema_name IS NOT NULL AND d.referenced_schema_name <> N'route4me')))),
    N' module references');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S09', N'Structure', N'No foreign key or SQL-module reference crosses the Route4Me model boundary (ERP, Voxware or other)',
        N'0 foreign keys, 0 module references', @Actual,
        CASE WHEN @Actual = N'0 foreign keys, 0 module references' THEN 1 ELSE 0 END);

/* ===== D: Data ========================================================== */

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT e.CheckId, N'Data', N'Row count of route4me.' + e.TableName, CONCAT(e.ExpectedRows, N' rows'),
       CONCAT(a.ActualRows, N' rows'), CASE WHEN a.ActualRows = e.ExpectedRows THEN 1 ELSE 0 END
FROM (VALUES ('D01', N'Shipment', 1), ('D02', N'ShipmentLine', 2), ('D03', N'DeliveryShipment', 1),
             ('D04', N'DeliveryShipmentLine', 2), ('D05', N'DeliveryEvent', 1)) AS e (CheckId, TableName, ExpectedRows)
JOIN (          SELECT N'Shipment' AS TableName, COUNT(*) AS ActualRows FROM route4me.Shipment
      UNION ALL SELECT N'ShipmentLine',         COUNT(*) FROM route4me.ShipmentLine
      UNION ALL SELECT N'DeliveryShipment',     COUNT(*) FROM route4me.DeliveryShipment
      UNION ALL SELECT N'DeliveryShipmentLine', COUNT(*) FROM route4me.DeliveryShipmentLine
      UNION ALL SELECT N'DeliveryEvent',        COUNT(*) FROM route4me.DeliveryEvent) AS a
  ON a.TableName = e.TableName
ORDER BY e.CheckId;

SELECT @Actual = CONCAT(COUNT(*), N' match')
FROM route4me.Shipment AS s
WHERE s.ShipmentId = 'SHIP-0001' AND s.Status = 'Dispatched' AND s.DispatchId = 'DISP-0001';
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D06', N'Data', N'SHIP-0001 was prepared and then dispatched as DISP-0001 (Shipping harness)', N'1 match',
        @Actual, CASE WHEN @Actual = N'1 match' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT d.ShipmentId, d.SalesOrderId, d.WarehouseId, d.Status, l.ProductId, l.Quantity
        FROM route4me.DeliveryShipment AS d
        JOIN route4me.DeliveryShipmentLine AS l ON l.ShipmentId = d.ShipmentId
        EXCEPT
        SELECT v.ShipmentId, v.SalesOrderId, v.WarehouseId, v.Status, v.ProductId, v.Quantity
        FROM (VALUES ('SHIP-0001', 'SO-0001', 'WH-01', 'Dispatched', 'P000142', 3),
                     ('SHIP-0001', 'SO-0001', 'WH-01', 'Dispatched', 'P000143', 2))
             AS v (ShipmentId, SalesOrderId, WarehouseId, Status, ProductId, Quantity)) AS x),
    N' unexpected, ',
    (SELECT COUNT(*) FROM (
        SELECT v.ShipmentId, v.SalesOrderId, v.WarehouseId, v.Status, v.ProductId, v.Quantity
        FROM (VALUES ('SHIP-0001', 'SO-0001', 'WH-01', 'Dispatched', 'P000142', 3),
                     ('SHIP-0001', 'SO-0001', 'WH-01', 'Dispatched', 'P000143', 2))
             AS v (ShipmentId, SalesOrderId, WarehouseId, Status, ProductId, Quantity)
        EXCEPT
        SELECT d.ShipmentId, d.SalesOrderId, d.WarehouseId, d.Status, l.ProductId, l.Quantity
        FROM route4me.DeliveryShipment AS d
        JOIN route4me.DeliveryShipmentLine AS l ON l.ShipmentId = d.ShipmentId) AS x),
    N' missing');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D07', N'Data', N'Delivery holds SHIP-0001 exactly as samples/stage2-dispatch.json hands it over (Dispatched, P000142 x3, P000143 x2)',
        N'0 unexpected, 0 missing', @Actual, CASE WHEN @Actual = N'0 unexpected, 0 missing' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(COUNT(*), N' match')
FROM route4me.DeliveryEvent AS e
WHERE e.EventId = 'DEL-0001' AND e.ShipmentId = 'SHIP-0001' AND e.Outcome = 'Delivered'
  AND e.ProofReference = 'POD-0001' AND e.ExceptionReason IS NULL
  AND e.OccurredAt = CAST('2026-11-09T12:00:00-05:00' AS datetimeoffset(7))
  AND DATEPART(TZOFFSET, e.OccurredAt) = -300;
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D08', N'Data', N'SHIP-0001 delivered: DEL-0001 with proof POD-0001 at 2026-11-09 12:00 -05:00, offset kept (Delivery harness)',
        N'1 match', @Actual, CASE WHEN @Actual = N'1 match' THEN 1 ELSE 0 END);

SET @Actual = CONCAT(
    (SELECT COUNT(*) FROM route4me.Shipment AS s
     WHERE NOT EXISTS (SELECT 1 FROM route4me.ShipmentLine AS l WHERE l.ShipmentId = s.ShipmentId)),
    N' shipments, ',
    (SELECT COUNT(*) FROM route4me.DeliveryShipment AS d
     WHERE NOT EXISTS (SELECT 1 FROM route4me.DeliveryShipmentLine AS l WHERE l.ShipmentId = d.ShipmentId)),
    N' delivery shipments without lines');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D09', N'Data', N'Every shipment and every delivery shipment has at least one line (Shipping, Delivery)',
        N'0 shipments, 0 delivery shipments without lines', @Actual,
        CASE WHEN @Actual = N'0 shipments, 0 delivery shipments without lines' THEN 1 ELSE 0 END);

/* Shipping -> Delivery component handoff: Delivery's copy carries the shipment's
   ShipmentId, SalesOrderId, WarehouseId, products and quantities unchanged. */
SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT d.ShipmentId, d.SalesOrderId, d.WarehouseId, l.ProductId, l.Quantity
        FROM route4me.DeliveryShipment AS d
        JOIN route4me.DeliveryShipmentLine AS l ON l.ShipmentId = d.ShipmentId
        EXCEPT
        SELECT s.ShipmentId, s.SalesOrderId, s.WarehouseId, l.ProductId, l.Quantity
        FROM route4me.Shipment AS s
        JOIN route4me.ShipmentLine AS l ON l.ShipmentId = s.ShipmentId) AS x),
    N' changed or unknown, ',
    (SELECT COUNT(*) FROM (
        SELECT s.ShipmentId, s.SalesOrderId, s.WarehouseId, l.ProductId, l.Quantity
        FROM route4me.Shipment AS s
        JOIN route4me.ShipmentLine AS l ON l.ShipmentId = s.ShipmentId
        WHERE EXISTS (SELECT 1 FROM route4me.DeliveryShipment AS d WHERE d.ShipmentId = s.ShipmentId)
        EXCEPT
        SELECT d.ShipmentId, d.SalesOrderId, d.WarehouseId, l.ProductId, l.Quantity
        FROM route4me.DeliveryShipment AS d
        JOIN route4me.DeliveryShipmentLine AS l ON l.ShipmentId = d.ShipmentId) AS x),
    N' lost');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D10', N'Data', N'Shipping -> Delivery handoff preserved ShipmentId, SalesOrderId, WarehouseId, products and quantities',
        N'0 changed or unknown, 0 lost', @Actual, CASE WHEN @Actual = N'0 changed or unknown, 0 lost' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(COUNT(*), N' violations')
FROM route4me.DeliveryEvent AS e
JOIN route4me.DeliveryShipment AS d ON d.ShipmentId = e.ShipmentId
WHERE d.Status <> 'Dispatched';
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D11', N'Data', N'Delivery outcomes exist only for shipments Delivery holds as Dispatched (Delivery)',
        N'0 violations', @Actual, CASE WHEN @Actual = N'0 violations' THEN 1 ELSE 0 END);

/* ===== I: Integration references =======================================
   Route4Me holds ERP identifiers and Voxware references as values. The other
   side of the same values is verified by each model's own script: Voxware 910
   I02 (ReadyForShipment packing PACK-0001, PICK-0001, P000142 x3, P000143 x2)
   and ERP 920 D04, Q02 (SO-0001; order quantities 3 and 2). No query here
   reads another model. */

SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT SalesOrderId, WarehouseId FROM route4me.Shipment
        UNION SELECT SalesOrderId, WarehouseId FROM route4me.DeliveryShipment) AS x
     WHERE NOT (x.SalesOrderId = 'SO-0001' AND x.WarehouseId = 'WH-01')),
    N' unexpected order or warehouse, ',
    (SELECT COUNT(*) FROM (
        SELECT ProductId FROM route4me.ShipmentLine
        UNION SELECT ProductId FROM route4me.DeliveryShipmentLine) AS x
     WHERE x.ProductId NOT IN ('P000142', 'P000143')),
    N' unexpected products');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('I01', N'Integration', N'Route4Me records carry only the ERP identifiers issued for SO-0001 (WH-01, P000142, P000143)',
        N'0 unexpected order or warehouse, 0 unexpected products', @Actual,
        CASE WHEN @Actual = N'0 unexpected order or warehouse, 0 unexpected products' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT s.SalesOrderId, s.WarehouseId, s.PackingId, s.PickConfirmationId, l.ProductId, l.Quantity
        FROM route4me.Shipment AS s
        JOIN route4me.ShipmentLine AS l ON l.ShipmentId = s.ShipmentId
        EXCEPT
        SELECT v.SalesOrderId, v.WarehouseId, v.PackingId, v.PickConfirmationId, v.ProductId, v.Quantity
        FROM (VALUES ('SO-0001', 'WH-01', 'PACK-0001', 'PICK-0001', 'P000142', 3),
                     ('SO-0001', 'WH-01', 'PACK-0001', 'PICK-0001', 'P000143', 2))
             AS v (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, ProductId, Quantity)) AS x),
    N' unexpected, ',
    (SELECT COUNT(*) FROM (
        SELECT v.SalesOrderId, v.WarehouseId, v.PackingId, v.PickConfirmationId, v.ProductId, v.Quantity
        FROM (VALUES ('SO-0001', 'WH-01', 'PACK-0001', 'PICK-0001', 'P000142', 3),
                     ('SO-0001', 'WH-01', 'PACK-0001', 'PICK-0001', 'P000143', 2))
             AS v (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, ProductId, Quantity)
        EXCEPT
        SELECT s.SalesOrderId, s.WarehouseId, s.PackingId, s.PickConfirmationId, l.ProductId, l.Quantity
        FROM route4me.Shipment AS s
        JOIN route4me.ShipmentLine AS l ON l.ShipmentId = s.ShipmentId) AS x),
    N' missing');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('I02', N'Integration', N'Shipment keeps the Voxware packing references and accepted order quantities (PACK-0001, PICK-0001, P000142 x3, P000143 x2)',
        N'0 unexpected, 0 missing', @Actual, CASE WHEN @Actual = N'0 unexpected, 0 missing' THEN 1 ELSE 0 END);

/* ===== C: Constraint behavior (each case rolled back) ===================
   ExpectedError NULL = the write must be accepted.
   Otherwise the error number must match and the message must name ExpectedText.
   Cases needing more records build SHIP-9001 inside their own transaction. */

DECLARE @Ship9001 nvarchar(max) =
    N'INSERT INTO route4me.Shipment (ShipmentId, SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Status, DispatchId)
      VALUES (''SHIP-9001'', ''SO-9001'', ''WH-01'', ''PACK-9001'', ''PICK-9001'', ''Prepared'', NULL);';
DECLARE @Delivery9001 nvarchar(max) =
    N'INSERT INTO route4me.DeliveryShipment (ShipmentId, SalesOrderId, WarehouseId, Status) VALUES (''SHIP-9001'', ''SO-9001'', ''WH-01'', ''Dispatched'');
      INSERT INTO route4me.DeliveryShipmentLine (ShipmentId, ProductId, Quantity) VALUES (''SHIP-9001'', ''P000142'', 2);';
DECLARE @InsertEvent nvarchar(200) =
    N'INSERT INTO route4me.DeliveryEvent (EventId, ShipmentId, Outcome, OccurredAt, ProofReference, ExceptionReason) VALUES ';
DECLARE @At nvarchar(80) = N'CAST(''2026-11-09T12:00:00-05:00'' AS datetimeoffset(7))';

DECLARE @Cases TABLE
(
    CheckId        varchar(10)   NOT NULL PRIMARY KEY,
    Description    nvarchar(300) NOT NULL,
    ExpectedError  int           NULL,
    ExpectedText   nvarchar(200) NULL,
    Sql            nvarchar(max) NOT NULL
);
INSERT INTO @Cases (CheckId, Description, ExpectedError, ExpectedText, Sql) VALUES
    ('C01', N'Second shipment for the same order and warehouse rejected, ignoring case', 2627, N'UQ_Shipment_Order',
        N'INSERT INTO route4me.Shipment (ShipmentId, SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Status, DispatchId)
          VALUES (''SHIP-0002'', ''so-0001'', ''wh-01'', ''PACK-0001'', ''PICK-0001'', ''Prepared'', NULL);'),
    ('C02', N'ShipmentId cannot be reused for another order, ignoring case', 2627, N'PK_Shipment',
        N'INSERT INTO route4me.Shipment (ShipmentId, SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Status, DispatchId)
          VALUES (''ship-0001'', ''SO-9001'', ''WH-01'', ''PACK-9001'', ''PICK-9001'', ''Prepared'', NULL);'),
    ('C03', N'Blank shipment ID rejected', 547, N'CK_Shipment_ShipmentId_NotBlank',
        N'INSERT INTO route4me.Shipment (ShipmentId, SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Status, DispatchId)
          VALUES ('''', ''SO-9001'', ''WH-01'', ''PACK-9001'', ''PICK-9001'', ''Prepared'', NULL);'),
    ('C04', N'Blank packing reference rejected', 547, N'CK_Shipment_PackingId_NotBlank',
        N'INSERT INTO route4me.Shipment (ShipmentId, SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Status, DispatchId)
          VALUES (''SHIP-9001'', ''SO-9001'', ''WH-01'', '' '', ''PICK-9001'', ''Prepared'', NULL);'),
    ('C05', N'Shipment status other than Prepared or Dispatched rejected', 547, N'CK_Shipment_Status',
        N'UPDATE route4me.Shipment SET Status = ''Delivered'' WHERE ShipmentId = ''SHIP-0001'';'),
    ('C06', N'Dispatched shipment without a DispatchId rejected', 547, N'CK_Shipment_Status',
        N'UPDATE route4me.Shipment SET DispatchId = NULL WHERE ShipmentId = ''SHIP-0001'';'),
    ('C07', N'Prepared shipment with a DispatchId rejected', 547, N'CK_Shipment_Status',
        N'INSERT INTO route4me.Shipment (ShipmentId, SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Status, DispatchId)
          VALUES (''SHIP-9001'', ''SO-9001'', ''WH-01'', ''PACK-9001'', ''PICK-9001'', ''Prepared'', ''DISP-9001'');'),
    ('C08', N'Dispatch moves a prepared shipment to Dispatched with its DispatchId', NULL, NULL,
        @Ship9001 + N'UPDATE route4me.Shipment SET Status = ''Dispatched'', DispatchId = ''DISP-9001'' WHERE ShipmentId = ''SHIP-9001'';'),
    ('C09', N'DispatchId cannot be reused by another shipment, ignoring case', 2601, N'UX_Shipment_DispatchId',
        @Ship9001 + N'UPDATE route4me.Shipment SET Status = ''Dispatched'', DispatchId = ''disp-0001'' WHERE ShipmentId = ''SHIP-9001'';'),
    ('C10', N'Several prepared shipments without a DispatchId accepted', NULL, NULL,
        @Ship9001 + N'INSERT INTO route4me.Shipment (ShipmentId, SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Status, DispatchId)
          VALUES (''SHIP-9002'', ''SO-9002'', ''WH-01'', ''PACK-9002'', ''PICK-9002'', ''Prepared'', NULL);'),
    ('C11', N'Shipment line without a shipment rejected', 547, N'FK_ShipmentLine_Shipment',
        N'INSERT INTO route4me.ShipmentLine (ShipmentId, ProductId, Quantity) VALUES (''SHIP-9001'', ''P000142'', 1);'),
    ('C12', N'A product appears once per shipment, ignoring case', 2627, N'PK_ShipmentLine',
        N'INSERT INTO route4me.ShipmentLine (ShipmentId, ProductId, Quantity) VALUES (''SHIP-0001'', ''p000142'', 1);'),
    ('C13', N'Nonpositive shipment quantity rejected', 547, N'CK_ShipmentLine_Quantity_Positive',
        N'UPDATE route4me.ShipmentLine SET Quantity = 0 WHERE ShipmentId = ''SHIP-0001'' AND ProductId = ''P000142'';'),
    ('C14', N'Delivery registers a ShipmentId once, ignoring case (D9)', 2627, N'PK_DeliveryShipment',
        N'INSERT INTO route4me.DeliveryShipment (ShipmentId, SalesOrderId, WarehouseId, Status) VALUES (''ship-0001'', ''SO-0001'', ''WH-01'', ''Dispatched'');'),
    ('C15', N'Delivery registration needs no Shipping record and accepts any status (Prepared)', NULL, NULL,
        N'INSERT INTO route4me.DeliveryShipment (ShipmentId, SalesOrderId, WarehouseId, Status) VALUES (''SHIP-PREP'', ''SO-9002'', ''WH-01'', ''Prepared'');
          INSERT INTO route4me.DeliveryShipmentLine (ShipmentId, ProductId, Quantity) VALUES (''SHIP-PREP'', ''P000142'', 3);'),
    ('C16', N'Blank delivery shipment ID rejected', 547, N'CK_DeliveryShipment_ShipmentId_NotBlank',
        N'INSERT INTO route4me.DeliveryShipment (ShipmentId, SalesOrderId, WarehouseId, Status) VALUES ('''', ''SO-9001'', ''WH-01'', ''Dispatched'');'),
    ('C17', N'Delivery line without a delivery shipment rejected', 547, N'FK_DeliveryShipmentLine_DeliveryShipment',
        N'INSERT INTO route4me.DeliveryShipmentLine (ShipmentId, ProductId, Quantity) VALUES (''SHIP-9001'', ''P000142'', 2);'),
    ('C18', N'A product appears once per delivery shipment, ignoring case', 2627, N'PK_DeliveryShipmentLine',
        N'INSERT INTO route4me.DeliveryShipmentLine (ShipmentId, ProductId, Quantity) VALUES (''SHIP-0001'', ''p000143'', 2);'),
    ('C19', N'Nonpositive delivery quantity rejected', 547, N'CK_DeliveryShipmentLine_Quantity_Positive',
        N'INSERT INTO route4me.DeliveryShipmentLine (ShipmentId, ProductId, Quantity) VALUES (''SHIP-0001'', ''P000144'', 0);'),
    ('C20', N'Second terminal outcome for the same shipment rejected', 2627, N'UQ_DeliveryEvent_Shipment',
        @InsertEvent + N'(''EXC-0001'', ''SHIP-0001'', ''Exception'', ' + @At + N', NULL, N''Damaged'');'),
    ('C21', N'EventId cannot be reused for another shipment, ignoring case', 2627, N'PK_DeliveryEvent',
        @Delivery9001 + @InsertEvent + N'(''del-0001'', ''SHIP-9001'', ''Delivered'', ' + @At + N', ''POD-9001'', NULL);'),
    ('C22', N'Outcome for a shipment Delivery has not registered rejected', 547, N'FK_DeliveryEvent_DeliveryShipment',
        @InsertEvent + N'(''DEL-9001'', ''SHIP-MISSING'', ''Delivered'', ' + @At + N', ''POD-9001'', NULL);'),
    ('C23', N'Delivered without a proof reference rejected', 547, N'CK_DeliveryEvent_Outcome',
        @Delivery9001 + @InsertEvent + N'(''DEL-9001'', ''SHIP-9001'', ''Delivered'', ' + @At + N', NULL, NULL);'),
    ('C24', N'Delivered with a blank proof reference rejected', 547, N'CK_DeliveryEvent_Outcome',
        @Delivery9001 + @InsertEvent + N'(''DEL-9001'', ''SHIP-9001'', ''Delivered'', ' + @At + N', '' '', NULL);'),
    ('C25', N'Exception with a blank reason rejected', 547, N'CK_DeliveryEvent_Outcome',
        @Delivery9001 + @InsertEvent + N'(''EXC-9001'', ''SHIP-9001'', ''Exception'', ' + @At + N', NULL, N'' '');'),
    ('C26', N'Outcome other than Delivered or Exception rejected', 547, N'CK_DeliveryEvent_Outcome',
        @Delivery9001 + @InsertEvent + N'(''DEL-9001'', ''SHIP-9001'', ''Lost'', ' + @At + N', ''POD-9001'', N''Lost in transit'');'),
    ('C27', N'Exception with a reason accepted', NULL, NULL,
        @Delivery9001 + @InsertEvent + N'(''EXC-9001'', ''SHIP-9001'', ''Exception'', ' + @At + N', NULL, N''Customer unavailable'');'),
    ('C28', N'OccurredAt left at the .NET default value rejected', 547, N'CK_DeliveryEvent_OccurredAt_Required',
        @Delivery9001 + @InsertEvent + N'(''DEL-9001'', ''SHIP-9001'', ''Delivered'', CAST(''0001-01-01T00:00:00+00:00'' AS datetimeoffset(7)), ''POD-9001'', NULL);'),
    ('C29', N'Missing OccurredAt rejected', 515, N'OccurredAt',
        @Delivery9001 + @InsertEvent + N'(''DEL-9001'', ''SHIP-9001'', ''Delivered'', NULL, ''POD-9001'', NULL);'),
    ('C30', N'Blank event ID rejected', 547, N'CK_DeliveryEvent_EventId_NotBlank',
        @Delivery9001 + @InsertEvent + N'('''', ''SHIP-9001'', ''Delivered'', ' + @At + N', ''POD-9001'', NULL);');

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
      (SELECT COUNT(*) FROM route4me.Shipment)         + (SELECT COUNT(*) FROM route4me.ShipmentLine)
    + (SELECT COUNT(*) FROM route4me.DeliveryShipment) + (SELECT COUNT(*) FROM route4me.DeliveryShipmentLine)
    + (SELECT COUNT(*) FROM route4me.DeliveryEvent);

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('Z01', N'Repeatability', N'Verification left no rows behind in the 5 Route4Me tables and no open transaction',
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
DECLARE @Summary nvarchar(200) = CONCAT(N'910-route4me-verify-shipment-delivery: ', @Total - @Failed, N'/', @Total, N' checks passed',
                                        CASE WHEN @Total = 53 THEN N'.' ELSE N' (expected 53 checks).' END);
PRINT @Summary;
IF @Failed > 0 OR @Total <> 53
    THROW 50003, @Summary, 1;
GO
