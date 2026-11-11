/*
    Frontier Distribution working model - Voxware Business Application
    Increment 3: Voxware warehouse fulfillment
    Script 910: Verification

    Reads the Voxware model only; needs nothing from ERP or Route4Me.
    Read-only and repeatable: every constraint case runs in its own transaction
    and is rolled back; check Z01 confirms nothing changed. Returns one result set
    (one row per check) and raises error 50003 when any check fails, so
    sqlcmd -b exits with code 1.

    Collation-safe: every catalog value (names and sys.objects.type, which carries
    a fixed system collation) is compared with COLLATE DATABASE_DEFAULT on both
    sides. No #temp tables. ASCII only, for sqlcmd under Windows PowerShell 5.1.

    Areas
      S  Structure: tables, constraints, collation, correlation keys, FK support, boundary
      D  Data: counts, fixture records, and the C# rules the tables do not enforce
      I  Integration references: ERP identifiers carried; shipment contract supplied
      C  Constraint behavior: rejected and accepted writes, rolled back
      Z  Repeatability
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT OFF;  -- constraint cases must reach CATCH and roll back individually

IF @@TRANCOUNT > 0
    THROW 50002, N'Run 910-voxware-verify-fulfillment.sql outside an open transaction.', 1;

DECLARE @Missing nvarchar(4000) =
(
    SELECT STUFF((
        SELECT N', voxware.' + e.TableName
        FROM (VALUES (N'PickPlan'), (N'PickPlanLine'), (N'PickConfirmation'), (N'PickConfirmationLine'),
                     (N'PackingConfirmation'), (N'PackingConfirmationLine')) AS e (TableName)
        WHERE OBJECT_ID(N'voxware.' + e.TableName, N'U') IS NULL
        FOR XML PATH(''), TYPE).value('.', 'nvarchar(4000)'), 1, 2, N'')
);
IF @Missing IS NOT NULL
BEGIN
    SET @Missing = N'Voxware tables missing: ' + @Missing + N'. Run install-voxware-fulfillment.sql first.';
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
      (SELECT COUNT(*) FROM voxware.PickPlan)            + (SELECT COUNT(*) FROM voxware.PickPlanLine)
    + (SELECT COUNT(*) FROM voxware.PickConfirmation)    + (SELECT COUNT(*) FROM voxware.PickConfirmationLine)
    + (SELECT COUNT(*) FROM voxware.PackingConfirmation) + (SELECT COUNT(*) FROM voxware.PackingConfirmationLine);

/* ===== S: Structure ===================================================== */

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT 'S01', N'Structure', N'Schema voxware exists', N'present',
       CASE WHEN SCHEMA_ID(N'voxware') IS NULL THEN N'missing' ELSE N'present' END,
       CASE WHEN SCHEMA_ID(N'voxware') IS NULL THEN 0 ELSE 1 END;

SELECT @Actual = CONCAT(
    SUM(CASE WHEN t.name COLLATE DATABASE_DEFAULT IN (N'PickPlan', N'PickPlanLine', N'PickConfirmation',
                  N'PickConfirmationLine', N'PackingConfirmation', N'PackingConfirmationLine') THEN 1 ELSE 0 END),
    N' expected, ',
    SUM(CASE WHEN t.name COLLATE DATABASE_DEFAULT IN (N'PickPlan', N'PickPlanLine', N'PickConfirmation',
                  N'PickConfirmationLine', N'PackingConfirmation', N'PackingConfirmationLine') THEN 0 ELSE 1 END),
    N' other')
FROM sys.tables AS t
WHERE t.schema_id = SCHEMA_ID(N'voxware');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S02', N'Structure', N'Schema voxware holds exactly the 6 fulfillment tables (no ERP or shipment copies)',
        N'6 expected, 0 other', @Actual, CASE WHEN @Actual = N'6 expected, 0 other' THEN 1 ELSE 0 END);

DECLARE @ExpectedConstraints TABLE (TableName sysname NOT NULL, ConstraintName sysname NOT NULL, ObjectType char(2) NOT NULL);
INSERT INTO @ExpectedConstraints (TableName, ConstraintName, ObjectType) VALUES
    (N'PickPlan',                N'PK_PickPlan',                                      'PK'),
    (N'PickPlan',                N'CK_PickPlan_SalesOrderId_NotBlank',                'C'),
    (N'PickPlan',                N'CK_PickPlan_WarehouseId_NotBlank',                 'C'),
    (N'PickPlan',                N'CK_PickPlan_Status',                               'C'),
    (N'PickPlanLine',            N'PK_PickPlanLine',                                  'PK'),
    (N'PickPlanLine',            N'FK_PickPlanLine_PickPlan',                         'F'),
    (N'PickPlanLine',            N'CK_PickPlanLine_ProductId_NotBlank',               'C'),
    (N'PickPlanLine',            N'CK_PickPlanLine_Quantity_Positive',                'C'),
    (N'PickConfirmation',        N'PK_PickConfirmation',                              'PK'),
    (N'PickConfirmation',        N'UQ_PickConfirmation_Reference',                    'UQ'),
    (N'PickConfirmation',        N'FK_PickConfirmation_PickPlan',                     'F'),
    (N'PickConfirmation',        N'CK_PickConfirmation_ConfirmationId_NotBlank',      'C'),
    (N'PickConfirmation',        N'CK_PickConfirmation_Outcome',                      'C'),
    (N'PickConfirmationLine',    N'PK_PickConfirmationLine',                          'PK'),
    (N'PickConfirmationLine',    N'FK_PickConfirmationLine_PickConfirmation',         'F'),
    (N'PickConfirmationLine',    N'FK_PickConfirmationLine_PickPlanLine',             'F'),
    (N'PickConfirmationLine',    N'CK_PickConfirmationLine_Quantity_NonNegative',     'C'),
    (N'PackingConfirmation',     N'PK_PackingConfirmation',                           'PK'),
    (N'PackingConfirmation',     N'FK_PackingConfirmation_PickConfirmation',          'F'),
    (N'PackingConfirmation',     N'CK_PackingConfirmation_PackingId_NotBlank',        'C'),
    (N'PackingConfirmation',     N'CK_PackingConfirmation_Outcome',                   'C'),
    (N'PackingConfirmationLine', N'PK_PackingConfirmationLine',                       'PK'),
    (N'PackingConfirmationLine', N'FK_PackingConfirmationLine_PackingConfirmation',   'F'),
    (N'PackingConfirmationLine', N'FK_PackingConfirmationLine_PickConfirmationLine',  'F'),
    (N'PackingConfirmationLine', N'CK_PackingConfirmationLine_Quantity_NonNegative',  'C');

SET @Actual = STUFF((
    SELECT N', ' + e.ConstraintName
    FROM @ExpectedConstraints AS e
    WHERE NOT EXISTS (SELECT 1 FROM sys.objects AS o
                      WHERE o.schema_id = SCHEMA_ID(N'voxware')
                        AND o.name COLLATE DATABASE_DEFAULT = e.ConstraintName COLLATE DATABASE_DEFAULT
                        AND o.type COLLATE DATABASE_DEFAULT = e.ObjectType COLLATE DATABASE_DEFAULT
                        AND o.parent_object_id = OBJECT_ID(N'voxware.' + e.TableName))
    ORDER BY e.ConstraintName
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S03', N'Structure', N'All 25 named keys, foreign keys and checks exist on their tables', N'none missing',
        COALESCE(N'missing: ' + @Actual, N'none missing'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

SET @Actual = STUFF((
    SELECT N', ' + e.TableName + N'.' + e.ColumnName + N' (' + COALESCE(c.collation_name COLLATE DATABASE_DEFAULT, N'missing') + N')'
    FROM (VALUES (N'PickPlan', N'SalesOrderId'), (N'PickPlan', N'WarehouseId'), (N'PickPlan', N'Status'),
                 (N'PickPlanLine', N'SalesOrderId'), (N'PickPlanLine', N'WarehouseId'), (N'PickPlanLine', N'ProductId'),
                 (N'PickConfirmation', N'SalesOrderId'), (N'PickConfirmation', N'WarehouseId'),
                 (N'PickConfirmation', N'ConfirmationId'), (N'PickConfirmation', N'Outcome'),
                 (N'PickConfirmationLine', N'SalesOrderId'), (N'PickConfirmationLine', N'WarehouseId'), (N'PickConfirmationLine', N'ProductId'),
                 (N'PackingConfirmation', N'SalesOrderId'), (N'PackingConfirmation', N'WarehouseId'), (N'PackingConfirmation', N'PackingId'),
                 (N'PackingConfirmation', N'PickConfirmationId'), (N'PackingConfirmation', N'Outcome'),
                 (N'PackingConfirmationLine', N'SalesOrderId'), (N'PackingConfirmationLine', N'WarehouseId'),
                 (N'PackingConfirmationLine', N'ProductId')) AS e (TableName, ColumnName)
    LEFT JOIN sys.columns AS c
           ON c.object_id = OBJECT_ID(N'voxware.' + e.TableName)
          AND c.name COLLATE DATABASE_DEFAULT = e.ColumnName COLLATE DATABASE_DEFAULT
    WHERE c.collation_name IS NULL
       OR c.collation_name COLLATE DATABASE_DEFAULT <> N'Latin1_General_100_CI_AS' COLLATE DATABASE_DEFAULT
    FOR XML PATH(''), TYPE).value('.', 'nvarchar(1000)'), 1, 2, N'');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S04', N'Structure', N'All 21 identifier, status and outcome columns use Latin1_General_100_CI_AS (D9)', N'none different',
        COALESCE(N'different: ' + @Actual, N'none different'), CASE WHEN @Actual IS NULL THEN 1 ELSE 0 END);

/* Plans, pick confirmations and packings are keyed by (SalesOrderId, WarehouseId);
   no unique key holds ConfirmationId or PackingId without that correlation (D8). */
DECLARE @KeyedHeaders int, @GlobalIdKeys int;

SELECT @KeyedHeaders = COUNT(*)
FROM (VALUES (N'PickPlan'), (N'PickConfirmation'), (N'PackingConfirmation')) AS h (TableName)
CROSS APPLY (SELECT COUNT(*) AS KeyColumns,
                    SUM(CASE WHEN c.name COLLATE DATABASE_DEFAULT IN (N'SalesOrderId', N'WarehouseId') THEN 1 ELSE 0 END) AS CorrelationColumns
             FROM sys.indexes AS i
             JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
             JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE i.object_id = OBJECT_ID(N'voxware.' + h.TableName) AND i.is_primary_key = 1) AS k
WHERE k.KeyColumns = 2 AND k.CorrelationColumns = 2;

SELECT @GlobalIdKeys = COUNT(*)
FROM sys.indexes AS i
CROSS APPLY (SELECT SUM(CASE WHEN c.name COLLATE DATABASE_DEFAULT IN (N'ConfirmationId', N'PackingId', N'PickConfirmationId')
                             THEN 1 ELSE 0 END) AS ReferenceColumns,
                    SUM(CASE WHEN c.name COLLATE DATABASE_DEFAULT IN (N'SalesOrderId', N'WarehouseId')
                             THEN 1 ELSE 0 END) AS CorrelationColumns
             FROM sys.index_columns AS ic
             JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0) AS k
WHERE i.is_unique = 1
  AND OBJECT_SCHEMA_NAME(i.object_id) = N'voxware'
  AND k.ReferenceColumns > 0
  AND k.CorrelationColumns < 2;

SET @Actual = CONCAT(@KeyedHeaders, N' of 3 headers keyed by (SalesOrderId, WarehouseId); ', @GlobalIdKeys, N' global ID keys');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S05', N'Structure', N'Correlation keys as implemented; ConfirmationId and PackingId are not global keys',
        N'3 of 3 headers keyed by (SalesOrderId, WarehouseId); 0 global ID keys', @Actual,
        CASE WHEN @Actual = N'3 of 3 headers keyed by (SalesOrderId, WarehouseId); 0 global ID keys' THEN 1 ELSE 0 END);

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
WHERE OBJECT_SCHEMA_NAME(fk.parent_object_id) = N'voxware';
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S06', N'Structure', N'Every foreign key is supported by an index on its child table', N'0 unsupported of 7 foreign keys',
        @Actual, CASE WHEN @Actual = N'0 unsupported of 7 foreign keys' THEN 1 ELSE 0 END);

SET @Actual = CONCAT(
    (SELECT COUNT(*) FROM sys.foreign_keys AS fk
     WHERE (OBJECT_SCHEMA_NAME(fk.parent_object_id) = N'voxware' AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) <> N'voxware')
        OR (OBJECT_SCHEMA_NAME(fk.parent_object_id) <> N'voxware' AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) = N'voxware')),
    N' foreign keys, ',
    (SELECT COUNT(*) FROM sys.sql_expression_dependencies AS d
     WHERE (OBJECT_SCHEMA_NAME(d.referencing_id) <> N'voxware' AND d.referenced_schema_name = N'voxware')
        OR (OBJECT_SCHEMA_NAME(d.referencing_id) = N'voxware'
            AND (d.referenced_server_name IS NOT NULL
              OR d.referenced_database_name IS NOT NULL
              OR (d.referenced_schema_name IS NOT NULL AND d.referenced_schema_name <> N'voxware')))),
    N' module references');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('S07', N'Structure', N'No foreign key or SQL-module reference crosses the Voxware model boundary (ERP, Route4Me or other)',
        N'0 foreign keys, 0 module references', @Actual,
        CASE WHEN @Actual = N'0 foreign keys, 0 module references' THEN 1 ELSE 0 END);

/* ===== D: Data ========================================================== */

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
SELECT e.CheckId, N'Data', N'Row count of voxware.' + e.TableName, CONCAT(e.ExpectedRows, N' rows'),
       CONCAT(a.ActualRows, N' rows'), CASE WHEN a.ActualRows = e.ExpectedRows THEN 1 ELSE 0 END
FROM (VALUES ('D01', N'PickPlan', 1), ('D02', N'PickPlanLine', 2), ('D03', N'PickConfirmation', 1),
             ('D04', N'PickConfirmationLine', 2), ('D05', N'PackingConfirmation', 1),
             ('D06', N'PackingConfirmationLine', 2)) AS e (CheckId, TableName, ExpectedRows)
JOIN (          SELECT N'PickPlan' AS TableName, COUNT(*) AS ActualRows FROM voxware.PickPlan
      UNION ALL SELECT N'PickPlanLine',            COUNT(*) FROM voxware.PickPlanLine
      UNION ALL SELECT N'PickConfirmation',        COUNT(*) FROM voxware.PickConfirmation
      UNION ALL SELECT N'PickConfirmationLine',    COUNT(*) FROM voxware.PickConfirmationLine
      UNION ALL SELECT N'PackingConfirmation',     COUNT(*) FROM voxware.PackingConfirmation
      UNION ALL SELECT N'PackingConfirmationLine', COUNT(*) FROM voxware.PackingConfirmationLine) AS a
  ON a.TableName = e.TableName;

SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT p.SalesOrderId, p.WarehouseId, p.Status, l.ProductId, l.Quantity
        FROM voxware.PickPlan AS p
        JOIN voxware.PickPlanLine AS l ON l.SalesOrderId = p.SalesOrderId AND l.WarehouseId = p.WarehouseId
        EXCEPT
        SELECT v.SalesOrderId, v.WarehouseId, v.Status, v.ProductId, v.Quantity
        FROM (VALUES ('SO-0001', 'WH-01', 'Ready', 'P000142', 3), ('SO-0001', 'WH-01', 'Ready', 'P000143', 2))
             AS v (SalesOrderId, WarehouseId, Status, ProductId, Quantity)) AS x),
    N' unexpected, ',
    (SELECT COUNT(*) FROM (
        SELECT v.SalesOrderId, v.WarehouseId, v.Status, v.ProductId, v.Quantity
        FROM (VALUES ('SO-0001', 'WH-01', 'Ready', 'P000142', 3), ('SO-0001', 'WH-01', 'Ready', 'P000143', 2))
             AS v (SalesOrderId, WarehouseId, Status, ProductId, Quantity)
        EXCEPT
        SELECT p.SalesOrderId, p.WarehouseId, p.Status, l.ProductId, l.Quantity
        FROM voxware.PickPlan AS p
        JOIN voxware.PickPlanLine AS l ON l.SalesOrderId = p.SalesOrderId AND l.WarehouseId = p.WarehouseId) AS x),
    N' missing');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D07', N'Data', N'SO-0001 at WH-01 has a Ready plan with demand aggregated by product (P000142 x3, P000143 x2)',
        N'0 unexpected, 0 missing', @Actual, CASE WHEN @Actual = N'0 unexpected, 0 missing' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(COUNT(*), N' violations')
FROM voxware.PickPlan AS p
OUTER APPLY (SELECT COUNT(*) AS Lines FROM voxware.PickPlanLine AS l
             WHERE l.SalesOrderId = p.SalesOrderId AND l.WarehouseId = p.WarehouseId) AS x
WHERE (p.Status = 'Ready' AND x.Lines = 0) OR (p.Status <> 'Ready' AND x.Lines > 0);
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D08', N'Data', N'Blocked plans have no lines; Ready plans have at least one (PickPlanning)', N'0 violations',
        @Actual, CASE WHEN @Actual = N'0 violations' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM voxware.PickConfirmation AS c
     JOIN voxware.PickPlan AS p ON p.SalesOrderId = c.SalesOrderId AND p.WarehouseId = c.WarehouseId
     WHERE p.Status <> 'Ready')
  + (SELECT COUNT(*) FROM voxware.PickConfirmation AS c
     JOIN voxware.PickPlanLine AS pl ON pl.SalesOrderId = c.SalesOrderId AND pl.WarehouseId = c.WarehouseId
     LEFT JOIN voxware.PickConfirmationLine AS cl
            ON cl.SalesOrderId = pl.SalesOrderId AND cl.WarehouseId = pl.WarehouseId AND cl.ProductId = pl.ProductId
     WHERE cl.ProductId IS NULL OR cl.Quantity > pl.Quantity),
    N' violations');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D09', N'Data', N'Pick confirmations exist only for Ready plans and cover every planned product, 0 to planned (PickExecution)',
        N'0 violations', @Actual, CASE WHEN @Actual = N'0 violations' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(COUNT(*), N' violations')
FROM voxware.PickConfirmation AS c
CROSS APPLY (SELECT COALESCE(SUM(CASE WHEN cl.Quantity = pl.Quantity THEN 0 ELSE 1 END), 0) AS ShortLines
             FROM voxware.PickPlanLine AS pl
             LEFT JOIN voxware.PickConfirmationLine AS cl
                    ON cl.SalesOrderId = pl.SalesOrderId AND cl.WarehouseId = pl.WarehouseId AND cl.ProductId = pl.ProductId
             WHERE pl.SalesOrderId = c.SalesOrderId AND pl.WarehouseId = c.WarehouseId) AS s
WHERE (c.Outcome = 'Completed' AND s.ShortLines <> 0) OR (c.Outcome = 'Partial' AND s.ShortLines = 0);
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D10', N'Data', N'Pick outcome is Completed exactly when every planned quantity was picked, else Partial',
        N'0 violations', @Actual, CASE WHEN @Actual = N'0 violations' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(COUNT(*), N' violations')
FROM voxware.PackingConfirmation AS k
JOIN voxware.PickPlanLine AS pl ON pl.SalesOrderId = k.SalesOrderId AND pl.WarehouseId = k.WarehouseId
LEFT JOIN voxware.PickConfirmationLine AS cl
       ON cl.SalesOrderId = pl.SalesOrderId AND cl.WarehouseId = pl.WarehouseId AND cl.ProductId = pl.ProductId
LEFT JOIN voxware.PackingConfirmationLine AS kl
       ON kl.SalesOrderId = pl.SalesOrderId AND kl.WarehouseId = pl.WarehouseId AND kl.ProductId = pl.ProductId
WHERE kl.ProductId IS NULL OR cl.ProductId IS NULL OR kl.Quantity > cl.Quantity;
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D11', N'Data', N'Packings cover every planned product; packed never exceeds picked (Packing)',
        N'0 violations', @Actual, CASE WHEN @Actual = N'0 violations' THEN 1 ELSE 0 END);

SELECT @Actual = CONCAT(COUNT(*), N' violations')
FROM voxware.PackingConfirmation AS k
CROSS APPLY (SELECT COALESCE(SUM(CASE WHEN cl.Quantity = pl.Quantity AND kl.Quantity = pl.Quantity THEN 0 ELSE 1 END), 0) AS NotFull
             FROM voxware.PickPlanLine AS pl
             LEFT JOIN voxware.PickConfirmationLine AS cl
                    ON cl.SalesOrderId = pl.SalesOrderId AND cl.WarehouseId = pl.WarehouseId AND cl.ProductId = pl.ProductId
             LEFT JOIN voxware.PackingConfirmationLine AS kl
                    ON kl.SalesOrderId = pl.SalesOrderId AND kl.WarehouseId = pl.WarehouseId AND kl.ProductId = pl.ProductId
             WHERE pl.SalesOrderId = k.SalesOrderId AND pl.WarehouseId = k.WarehouseId) AS s
WHERE (k.Outcome = 'ReadyForShipment' AND s.NotFull <> 0) OR (k.Outcome = 'Incomplete' AND s.NotFull = 0);
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('D12', N'Data', N'ReadyForShipment exactly when the pick is complete and every packed quantity equals planned',
        N'0 violations', @Actual, CASE WHEN @Actual = N'0 violations' THEN 1 ELSE 0 END);

/* ===== I: Integration references =======================================
   Voxware holds ERP identifiers as values. The ERP side of the same values is
   verified by the ERP model's own 920: D04 (SO-0001 accepted), D06 (P000142 and
   P000143 resolve), Q01 (Available at WH-01) and Q02 (quantities 3 and 2).
   No query here reads another model. */

SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT SalesOrderId, WarehouseId FROM voxware.PickPlan
        UNION SELECT SalesOrderId, WarehouseId FROM voxware.PickConfirmation
        UNION SELECT SalesOrderId, WarehouseId FROM voxware.PackingConfirmation) AS x
     WHERE NOT (x.SalesOrderId = 'SO-0001' AND x.WarehouseId = 'WH-01')),
    N' unexpected order or warehouse, ',
    (SELECT COUNT(*) FROM (
        SELECT ProductId FROM voxware.PickPlanLine
        UNION SELECT ProductId FROM voxware.PickConfirmationLine
        UNION SELECT ProductId FROM voxware.PackingConfirmationLine) AS x
     WHERE x.ProductId NOT IN ('P000142', 'P000143')),
    N' unexpected products');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('I01', N'Integration', N'Voxware records carry only the ERP identifiers issued for SO-0001 (WH-01, P000142, P000143)',
        N'0 unexpected order or warehouse, 0 unexpected products', @Actual,
        CASE WHEN @Actual = N'0 unexpected order or warehouse, 0 unexpected products' THEN 1 ELSE 0 END);

/* The existing shipment contract: Shipping.Create takes a ReadyForShipment
   PackingResult (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, lines);
   the Shipping harness and samples/stage2-dispatch.json use these values. */
SELECT @Actual = CONCAT(
    (SELECT COUNT(*) FROM (
        SELECT k.SalesOrderId, k.WarehouseId, k.PackingId, k.PickConfirmationId, kl.ProductId, kl.Quantity
        FROM voxware.PackingConfirmation AS k
        JOIN voxware.PackingConfirmationLine AS kl ON kl.SalesOrderId = k.SalesOrderId AND kl.WarehouseId = k.WarehouseId
        WHERE k.Outcome = 'ReadyForShipment'
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
        SELECT k.SalesOrderId, k.WarehouseId, k.PackingId, k.PickConfirmationId, kl.ProductId, kl.Quantity
        FROM voxware.PackingConfirmation AS k
        JOIN voxware.PackingConfirmationLine AS kl ON kl.SalesOrderId = k.SalesOrderId AND kl.WarehouseId = k.WarehouseId
        WHERE k.Outcome = 'ReadyForShipment') AS x),
    N' missing');
INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('I02', N'Integration', N'ReadyForShipment packing supplies the existing shipment contract (PACK-0001, PICK-0001, P000142 x3, P000143 x2)',
        N'0 unexpected, 0 missing', @Actual, CASE WHEN @Actual = N'0 unexpected, 0 missing' THEN 1 ELSE 0 END);

/* ===== C: Constraint behavior (each case rolled back) ===================
   ExpectedError NULL = the write must be accepted.
   Otherwise the error number must match and the message must name ExpectedText.
   Cases needing a second plan build SO-9001 inside their own transaction. */

DECLARE @Plan9001 nvarchar(max) =
    N'INSERT INTO voxware.PickPlan (SalesOrderId, WarehouseId, Status) VALUES (''SO-9001'', ''WH-01'', ''Ready'');
      INSERT INTO voxware.PickPlanLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-9001'', ''WH-01'', ''P000142'', 2);';
DECLARE @Pick9001 nvarchar(max) = @Plan9001 +
    N'INSERT INTO voxware.PickConfirmation (SalesOrderId, WarehouseId, ConfirmationId, Outcome) VALUES (''SO-9001'', ''WH-01'', ''PICK-9001'', ''Completed'');
      INSERT INTO voxware.PickConfirmationLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-9001'', ''WH-01'', ''P000142'', 2);';

DECLARE @Cases TABLE
(
    CheckId        varchar(10)   NOT NULL PRIMARY KEY,
    Description    nvarchar(300) NOT NULL,
    ExpectedError  int           NULL,
    ExpectedText   nvarchar(200) NULL,
    Sql            nvarchar(max) NOT NULL
);
INSERT INTO @Cases (CheckId, Description, ExpectedError, ExpectedText, Sql) VALUES
    ('C01', N'Second plan for the same order and warehouse rejected, ignoring case', 2627, N'PK_PickPlan',
        N'INSERT INTO voxware.PickPlan (SalesOrderId, WarehouseId, Status) VALUES (''so-0001'', ''wh-01'', ''Ready'');'),
    ('C02', N'Unknown plan status rejected', 547, N'CK_PickPlan_Status',
        N'INSERT INTO voxware.PickPlan (SalesOrderId, WarehouseId, Status) VALUES (''SO-9001'', ''WH-01'', ''Planned'');'),
    ('C03', N'Plan line without a plan rejected', 547, N'FK_PickPlanLine_PickPlan',
        N'INSERT INTO voxware.PickPlanLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-9001'', ''WH-01'', ''P000142'', 1);'),
    ('C04', N'A product appears once per plan (demand aggregated), ignoring case', 2627, N'PK_PickPlanLine',
        N'INSERT INTO voxware.PickPlanLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-0001'', ''WH-01'', ''p000142'', 1);'),
    ('C05', N'Nonpositive planned quantity rejected', 547, N'CK_PickPlanLine_Quantity_Positive',
        N'INSERT INTO voxware.PickPlanLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-0001'', ''WH-01'', ''P000144'', 0);'),
    ('C06', N'Blocked plan with no lines accepted', NULL, NULL,
        N'INSERT INTO voxware.PickPlan (SalesOrderId, WarehouseId, Status) VALUES (''SO-9001'', ''WH-01'', ''BlockedShort'');'),
    ('C07', N'Second, different confirmation for the same plan rejected', 2627, N'PK_PickConfirmation',
        N'INSERT INTO voxware.PickConfirmation (SalesOrderId, WarehouseId, ConfirmationId, Outcome) VALUES (''SO-0001'', ''WH-01'', ''PICK-0002'', ''Completed'');'),
    ('C08', N'Confirmation without a pick plan rejected', 547, N'FK_PickConfirmation_PickPlan',
        N'INSERT INTO voxware.PickConfirmation (SalesOrderId, WarehouseId, ConfirmationId, Outcome) VALUES (''SO-9001'', ''WH-01'', ''PICK-9001'', ''Completed'');'),
    ('C09', N'Blank confirmation ID rejected', 547, N'CK_PickConfirmation_ConfirmationId_NotBlank',
        @Plan9001 + N'INSERT INTO voxware.PickConfirmation (SalesOrderId, WarehouseId, ConfirmationId, Outcome) VALUES (''SO-9001'', ''WH-01'', '''', ''Completed'');'),
    ('C10', N'Unknown pick outcome rejected', 547, N'CK_PickConfirmation_Outcome',
        N'UPDATE voxware.PickConfirmation SET Outcome = ''Rejected'' WHERE SalesOrderId = ''SO-0001'' AND WarehouseId = ''WH-01'';'),
    ('C11', N'Picked line without a pick confirmation rejected', 547, N'FK_PickConfirmationLine_PickConfirmation',
        @Plan9001 + N'INSERT INTO voxware.PickConfirmationLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-9001'', ''WH-01'', ''P000142'', 2);'),
    ('C12', N'Picked product must be on the plan', 547, N'FK_PickConfirmationLine_PickPlanLine',
        N'INSERT INTO voxware.PickConfirmationLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-0001'', ''WH-01'', ''P000144'', 0);'),
    ('C13', N'Negative picked quantity rejected', 547, N'CK_PickConfirmationLine_Quantity_NonNegative',
        N'UPDATE voxware.PickConfirmationLine SET Quantity = -1 WHERE SalesOrderId = ''SO-0001'' AND WarehouseId = ''WH-01'' AND ProductId = ''P000142'';'),
    ('C14', N'Partial pick with zero picked accepted', NULL, NULL,
        @Plan9001 + N'INSERT INTO voxware.PickConfirmation (SalesOrderId, WarehouseId, ConfirmationId, Outcome) VALUES (''SO-9001'', ''WH-01'', ''PICK-9001'', ''Partial'');
          INSERT INTO voxware.PickConfirmationLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-9001'', ''WH-01'', ''P000142'', 0);'),
    ('C15', N'ConfirmationId reused by another plan accepted (not a global key)', NULL, NULL,
        @Plan9001 + N'INSERT INTO voxware.PickConfirmation (SalesOrderId, WarehouseId, ConfirmationId, Outcome) VALUES (''SO-9001'', ''WH-01'', ''PICK-0001'', ''Completed'');'),
    ('C16', N'Second, different packing for the same plan rejected', 2627, N'PK_PackingConfirmation',
        N'INSERT INTO voxware.PackingConfirmation (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Outcome) VALUES (''SO-0001'', ''WH-01'', ''PACK-0002'', ''PICK-0001'', ''ReadyForShipment'');'),
    ('C17', N'Packing must cite its own plan''s pick confirmation', 547, N'FK_PackingConfirmation_PickConfirmation',
        @Pick9001 + N'INSERT INTO voxware.PackingConfirmation (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Outcome) VALUES (''SO-9001'', ''WH-01'', ''PACK-9001'', ''PICK-0001'', ''Incomplete'');'),
    ('C18', N'Pick confirmation reference matches ignoring case (D9)', NULL, NULL,
        @Pick9001 + N'INSERT INTO voxware.PackingConfirmation (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Outcome) VALUES (''SO-9001'', ''WH-01'', ''PACK-9001'', ''pick-9001'', ''Incomplete'');'),
    ('C19', N'Packed line without a packing rejected', 547, N'FK_PackingConfirmationLine_PackingConfirmation',
        @Pick9001 + N'INSERT INTO voxware.PackingConfirmationLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-9001'', ''WH-01'', ''P000142'', 2);'),
    ('C20', N'Packed product must have a picked line', 547, N'FK_PackingConfirmationLine_PickConfirmationLine',
        N'INSERT INTO voxware.PackingConfirmationLine (SalesOrderId, WarehouseId, ProductId, Quantity) VALUES (''SO-0001'', ''WH-01'', ''P000144'', 0);'),
    ('C21', N'Negative packed quantity rejected', 547, N'CK_PackingConfirmationLine_Quantity_NonNegative',
        N'UPDATE voxware.PackingConfirmationLine SET Quantity = -1 WHERE SalesOrderId = ''SO-0001'' AND WarehouseId = ''WH-01'' AND ProductId = ''P000142'';'),
    ('C22', N'Unknown packing outcome rejected', 547, N'CK_PackingConfirmation_Outcome',
        N'UPDATE voxware.PackingConfirmation SET Outcome = ''Shipped'' WHERE SalesOrderId = ''SO-0001'' AND WarehouseId = ''WH-01'';'),
    ('C23', N'PackingId reused by another plan accepted (not a global key)', NULL, NULL,
        @Pick9001 + N'INSERT INTO voxware.PackingConfirmation (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Outcome) VALUES (''SO-9001'', ''WH-01'', ''PACK-0001'', ''PICK-9001'', ''Incomplete'');'),
    ('C24', N'Blank packing ID rejected', 547, N'CK_PackingConfirmation_PackingId_NotBlank',
        @Pick9001 + N'INSERT INTO voxware.PackingConfirmation (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, Outcome) VALUES (''SO-9001'', ''WH-01'', '''', ''PICK-9001'', ''Incomplete'');');

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
      (SELECT COUNT(*) FROM voxware.PickPlan)            + (SELECT COUNT(*) FROM voxware.PickPlanLine)
    + (SELECT COUNT(*) FROM voxware.PickConfirmation)    + (SELECT COUNT(*) FROM voxware.PickConfirmationLine)
    + (SELECT COUNT(*) FROM voxware.PackingConfirmation) + (SELECT COUNT(*) FROM voxware.PackingConfirmationLine);

INSERT INTO @Results (CheckId, Area, Description, Expected, Actual, Passed)
VALUES ('Z01', N'Repeatability', N'Verification left no rows behind in the 6 Voxware tables and no open transaction',
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
DECLARE @Summary nvarchar(200) = CONCAT(N'910-voxware-verify-fulfillment: ', @Total - @Failed, N'/', @Total, N' checks passed.');
PRINT @Summary;
IF @Failed > 0
    THROW 50003, @Summary, 1;
GO
