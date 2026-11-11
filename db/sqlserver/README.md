# Frontier Distribution — SQL Server data models

Synthetic, executable best-guess data models for Frontier's Business Applications. Not Frontier's production schema.

Design baseline: `Frontier-Database-Architecture-Assessment_2026-11-11_r2.md`, with decisions D2, D3, D4, D5, D7 and D9 approved 2026-11-11. One deviation from r2 is approved: D9 makes identifiers case-insensitive throughout, including Delivery, with no BIN2 columns.

| Folder | Business Application | Status |
| --- | --- | --- |
| `erp/` | ERP | Increment 1, ERP foundation: delivered for review |
| — | ERP sales orders and inventory | Next increment, not started |
| — | Voxware | Later increment |
| — | Route4Me | Later increment |
| — | nopCommerce | No tables (D11): vendor-owned store; the JSON fixture stays the source |

Each Business Application has its own folder, scripts and SQL Server schema. No script references another application's objects.

## Increment 1 — ERP foundation

Builds the ERP master data and the cross-application product identity map: 8 tables in schema `erp`, loaded with the Stage 1 synthetic data.

| File | Step | Purpose |
| --- | --- | --- |
| `erp/010-erp-schema.sql` | Build | Schema `erp` |
| `erp/020-erp-master-data.sql` | Build | Customer, CustomerLocation, Product, Supplier, SupplierProduct, Warehouse, InventoryPosition |
| `erp/030-erp-product-identity.sql` | Build | ProductIdentity |
| `erp/110-erp-seed-foundation.sql` | Execute | Synthetic data, row for row from `data/synthetic-identities.json` |
| `erp/install-erp-foundation.sql` | Build + Execute | sqlcmd runner for 010, 020, 030, 110 |
| `erp/910-erp-verify-foundation.sql` | Verify | 49 checks; read-only; fails with error 50003 if any check fails |
| `erp/990-erp-remove-foundation.sql` | Reset | Drops the foundation for a clean rebuild |

### Prerequisites

- SQL Server 2016 or later, any edition, including LocalDB.
- `sqlcmd` (ODBC or go-sqlcmd) for the runner; any client for the individual scripts.
- Permission to create a schema and tables in the target database (db_owner or db_ddladmin).

### Choose where the ERP model lives

The scripts never create or name a database, so placement stays open. Create or choose a database, then run everything against it.

| Option | Example |
| --- | --- |
| R1 — one shared model database | `CREATE DATABASE FrontierModel;` |
| R2 — ERP-only database | `CREATE DATABASE FrontierERP;` |
| R3 — separate instance | Either of the above on its own instance |

### Build, execute, verify

PowerShell, from the repository root:

```powershell
$Server   = '.'               # or '(localdb)\MSSQLLocalDB', 'HOST\INSTANCE'
$Database = 'FrontierModel'   # the database chosen for the ERP model

Push-Location .\db\sqlserver\erp

# Build + Execute: schema, tables, synthetic data
sqlcmd -S $Server -d $Database -E -I -b -i install-erp-foundation.sql

# Verify
sqlcmd -S $Server -d $Database -E -I -b -W -s '|' -i 910-erp-verify-foundation.sql
"Exit code: $LASTEXITCODE"

Pop-Location
```

Run the runner from the `erp` folder: `:r` resolves file names against the current directory. Use `-U`/`-P` instead of `-E` for SQL authentication. go-sqlcmd and recent ODBC sqlcmd versions may need `-C` to trust a local server certificate.

Expected result: the verification prints one row per check, ends with `910-erp-verify-foundation: 49/49 checks passed.`, and exits with code 0. Any failure shows `FAIL` rows, raises error 50003 and exits with code 1.

Without sqlcmd mode, run the files in order with the SqlServer module:

```powershell
'010-erp-schema.sql', '020-erp-master-data.sql', '030-erp-product-identity.sql', '110-erp-seed-foundation.sql' |
    ForEach-Object { Invoke-Sqlcmd -ServerInstance $Server -Database $Database -InputFile $_ -ErrorAction Stop }

Invoke-Sqlcmd -ServerInstance $Server -Database $Database -InputFile .\910-erp-verify-foundation.sql -ErrorAction Stop |
    Format-Table CheckId, Result, Description, Actual -AutoSize
```

SqlServer module 22 or later may need `-TrustServerCertificate` for a local instance. In SSMS, open and run the files one at a time in the same order.

### Rerun and rebuild

- 010 to 030 create only missing objects and never alter existing ones. After changing a definition, run `990-erp-remove-foundation.sql` and install again.
- 110 inserts only missing rows, in one transaction.
- 910 writes nothing that survives: each constraint case runs in its own transaction and is rolled back, and check Z01 confirms the row count is unchanged.
- 990 is all-or-nothing. Once a later increment references these tables, remove that increment first.

### What verification covers

| Area | Checks | What passes means |
| --- | --- | --- |
| S Structure | 6 | Schema, 8 tables, 25 named keys and checks, 5 indexes; 16 identifier columns case-insensitive; no foreign key or module reference crosses the ERP boundary |
| D Data | 10 | Row counts equal the fixture (1, 2, 2, 2, 3, 1, 2, 8); multiple suppliers for one product; multiple locations for one customer |
| R Resolution | 10 | Stored identities produce the Stage 1 resolver outcomes: Found, NotFound, Ambiguous, context disambiguation, case-insensitive lookup |
| Q Readiness | 2 | Master data the Stage 2 order and stock fixtures need: C0001 owns LOC-01; WH-01 positions for both products |
| C Constraints | 20 | 16 invalid writes rejected by the named constraint; 4 valid writes accepted; all rolled back |
| Z Repeatability | 1 | No rows left behind; no open transaction |

The R checks reproduce expected outcomes with a query; they are not a resolver. Resolution stays in the C# `ProductIdentityResolver`.

### Design conventions

- **Separation.** ERP objects only, in schema `erp`. Cross-application values will be plain reference columns; no cross-application foreign keys.
- **Keys.** Business identifiers are primary keys, as in code. Surrogate row keys only where no natural key exists: SupplierProduct (D13 open) and ProductIdentity (nullable Context cannot be in a primary key). Both reuse the Stage 1 Toad projection.
- **Case-insensitive identifiers (D9).** Every identifier column declares `COLLATE Latin1_General_100_CI_AS`, so behavior does not depend on the database's default collation. Descriptive text uses the database default.
- **Scoped identity.** `UQ_ProductIdentity_Scope` covers (System, IdentifierType, ExternalId, Context). SQL Server treats NULLs as equal in a unique constraint, which matches the C# rule: two NULL-context duplicates collide; NULL and a supplied Context do not.
- **Division of rules.** The database enforces single-table and same-model rules. The C# services stay authoritative. `NotBlank` checks reject empty and space-only identifiers; other whitespace remains a service rule.
- **Types.** Reused from the Stage 1 Toad projection; human-readable text is `nvarchar` instead of `varchar`.
- **Not added.** No resolver function, views, procedures, triggers, audit columns, or rows outside the fixture.

### Traceability

| C# rule (`IdentityCatalog.Validate`, `ProductIdentityResolver`) | Database object | Verified by |
| --- | --- | --- |
| Product, supplier, customer, location, warehouse IDs non-blank | `CK_*_NotBlank` | C02, C03 |
| Those IDs unique, case-insensitive | Primary keys under CI collation | C01, C05 |
| Location belongs to an existing customer | `FK_CustomerLocation_Customer` | C04 |
| Supplier product references existing supplier and product | `FK_SupplierProduct_Supplier`, `FK_SupplierProduct_Product` | C07 |
| Purchasing conversion > 0 | `CK_SupplierProduct_BaseUnitsPerPurchaseUnit_Positive` | C06 |
| No uniqueness on supplier and product | No constraint, by design | C20, D09 |
| Identity references an existing product | `FK_ProductIdentity_Product` | C08 |
| Identity System, IdentifierType, ExternalId non-blank | `CK_ProductIdentity_*_NotBlank` | C13 |
| Fully scoped identity unique; NULL Context is its own scope | `UQ_ProductIdentity_Scope` | C09–C12, C17–C19 |
| Inventory position references warehouse and product; unique pair | `PK_InventoryPosition`, `FK_InventoryPosition_*` | C14, C15 |
| Resolution outcomes and context rules | Stored rows plus scope index | R01–R10 |

### Recorded discrepancy for C# alignment (D9)

D9 requires case-insensitive identifiers throughout. `Frontier.Delivery` still compares case-sensitively; not changed in this increment.

| Location in `src/Frontier.Delivery/Delivery.cs` | Current comparison |
| --- | --- |
| Lines 18–20: `_shipments`, `_eventsByShipment`, `_eventsById` | `StringComparer.Ordinal` |
| Line 29: distinct products on a registered shipment | `Distinct(StringComparer.Ordinal)` |
| Line 45: `shipment.Status != "Dispatched"` | Ordinal string inequality |
| Line 64: replay equivalence `a == b` | Record equality, ordinal for strings |

Until the C# side is aligned, the Route4Me tables (later increment) will treat `SHIP-0001` and `ship-0001` as one shipment while `DeliveryExecution` treats them as two. No current Delivery or integration test uses case variants, so no test depends on the difference.

### Verification status of this delivery

Not yet executed on SQL Server: the authoring workspace had no SQL Server engine. Before delivery:

- Every T-SQL batch parsed with sqlglot (T-SQL dialect). That parser cannot read THROW inside BEGIN...END, so that batch was re-parsed with THROW neutralized.
- Every constraint, index and identifier-column name in 910 matches the DDL.
- The DDL, seed and all R, C, D, Q and Z cases ran in a SQLite emulation (NOCASE for CI_AS, an expression index for NULL-equal uniqueness): 35 of 35 passed. The seed rerun added nothing. Seeded rows equal the JSON fixture exactly.
- Not covered by that emulation: SQL Server catalog checks S01–S06, T-SQL error numbers and message text, `IF OBJECT_ID` rerun behavior, and the sqlcmd runner.

Your SQL Server run of `910-erp-verify-foundation.sql` is the authoritative Verify step.
