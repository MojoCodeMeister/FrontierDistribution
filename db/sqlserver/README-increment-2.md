# Frontier Distribution — SQL Server Increment 2: ERP sales orders and inventory

Synthetic, executable best-guess data model. Not Frontier's production schema.

## Package provenance

This package holds the increment 2 files only. It deliberately leaves out every increment 1 file and the top-level `README.md`. Use your verified versions of those (Git 428d439), including the locally corrected `910-erp-verify-foundation.sql`. Unzipping at the repository root adds six SQL files and this document; it overwrites nothing from increment 1.

| File | SHA-256 |
| --- | --- |
| `erp/040-erp-stock-on-hand.sql` | `D73DC557434672793C9B1049F86E9CED80BD8D28E415FC25E9FAF2D671EB512B` |
| `erp/050-erp-sales-orders.sql` | `015FD2BE17026AD9A1232DBCB1D5ED55E2CA1B70724B6DBB4EFA5185D7107E13` |
| `erp/120-erp-seed-sales-inventory.sql` | `6552E457A21253A1B083FD35F2C7DFA457C88573232DF14829FEBE8659893EC5` |
| `erp/920-erp-verify-sales-inventory.sql` | `3F2405E3F35EAE0E3950D1CE857030D834925BD2C5FF420397F81FC6E6D5E774` |
| `erp/980-erp-remove-sales-inventory.sql` | `F7914F5500F4DFF51AB68C63EF08663C132BA740075CA6F07ADEE04382F357DE` |
| `erp/install-erp-sales-inventory.sql` | `217DB99F4365E099372E0CD46F9AA14A78C998694BCAFB6D820C578BFBC87408` |

Check them in Windows PowerShell 5.1 from the repository root:

```powershell
Get-FileHash .\db\sqlserver\erp\*sales-inventory*.sql, .\db\sqlserver\erp\040-erp-stock-on-hand.sql, .\db\sqlserver\erp\050-erp-sales-orders.sql |
    Format-Table Hash, @{ n = 'File'; e = { Split-Path $_.Path -Leaf } } -AutoSize
```

The SQL files are identical to those in the first increment 2 package; only the packaging changed.

## Scope

Adds the accepted sales order with its submission snapshot (D7) and the on-hand stock snapshot (D5): 3 tables in schema `erp`, loaded with the Stage 2 synthetic data. The ERP foundation is untouched.

| File | Step | Purpose |
| --- | --- | --- |
| `erp/040-erp-stock-on-hand.sql` | Build | StockOnHand |
| `erp/050-erp-sales-orders.sql` | Build | SalesOrder, SalesOrderLine |
| `erp/120-erp-seed-sales-inventory.sql` | Execute | Stock from `data/synthetic-stock.json`; SO-0001 as OrderIntake accepts `data/synthetic-order.json` |
| `erp/install-erp-sales-inventory.sql` | Build + Execute | sqlcmd runner for 040, 050, 120 |
| `erp/920-erp-verify-sales-inventory.sql` | Verify | 38 checks; read-only; fails with error 50003 if any check fails |
| `erp/980-erp-remove-sales-inventory.sql` | Reset | Drops increment 2 only, for a clean rebuild |

### Tables

| Table | Key | Rules in the database | Rules left in C# |
| --- | --- | --- | --- |
| StockOnHand | (WarehouseId, ProductId) | OnHand ≥ 0; row only for an existing inventory position; one per warehouse and product | Availability assessment (never stored); missing row means Unknown |
| SalesOrder | SalesOrderId | (SourceSystem, ExternalOrderId) unique; customer exists; ship-to belongs to that customer; status `Received` only; IDs non-blank | Assigning SalesOrderId after validation; duplicate-submission comparison |
| SalesOrderLine | (SalesOrderId, SourceLineId) | LineSequence ≥ 1 and unique per order; quantity > 0; SKU and line ID non-blank; product exists | At least one line; SKU resolution; contiguous numbering |

`SourceSku` and `LineSequence` (D7) store what `OrderIntake` compares when the same order arrives again: SKU per line, position by position. No column was added beyond the approved scope. The database generates no identifiers, stores no availability results and has no order lifecycle beyond `Received`.

## Run on Freya — Windows PowerShell 5.1 and sqlcmd

From the repository root. Adjust `$Server` to Freya's instance name. Step 2 runs your verified local `910`.

```powershell
$Server   = 'Freya'
$Database = 'FrontierModel'

Push-Location .\db\sqlserver\erp
try {
    foreach ($step in 'install-erp-sales-inventory.sql',
                      '910-erp-verify-foundation.sql',
                      '920-erp-verify-sales-inventory.sql') {
        Write-Host "== $step"
        sqlcmd -S $Server -d $Database -E -I -b -W -s '|' -i $step
        if ($LASTEXITCODE -ne 0) { throw "$step failed (sqlcmd exit code $LASTEXITCODE)" }
    }
}
finally { Pop-Location }
```

Expected: the installer prints row counts 2, 1, 2. `910` still ends with `49/49 checks passed.`, because increment 2 adds no rows to the foundation tables and no foreign key outside `erp`. `920` prints the environment line (server collation, database collation, version), one row per check, and `920-erp-verify-sales-inventory: 38/38 checks passed.`. Every step exits with code 0.

The loop uses only Windows PowerShell 5.1 syntax: no `&&`, no ternary, no PowerShell 7 cmdlets. Run the installer from the `erp` folder, because `:r` resolves file names against the current directory. For a clean rebuild of increment 2 only, run `980-erp-remove-sales-inventory.sql`, then the installer again.

## Collation environment

- Every identifier and status column declares `Latin1_General_100_CI_AS`, so key and constraint behavior is the same whatever Freya's server or database default collation is (D9).
- `920` compares every catalog value with `COLLATE DATABASE_DEFAULT` on both sides. That includes `sys.objects.type`, the comparison corrected locally in `910`: catalog columns such as `type` carry a fixed system collation that can differ from the database default.
- Work tables that meet `erp` columns declare the `erp` collation. No `#temp` tables are used, so tempdb's collation never takes part.
- `920` prints the server and database collations for the record; they do not affect the result.
- The increment 2 SQL files are ASCII-only, so sqlcmd reads them the same under any Windows code page.

## What verification covers

| Area | Checks | What passes means |
| --- | --- | --- |
| S Structure | 7 | Foundation still present; 3 new tables; 19 named keys and checks; 3 indexes; 12 identifier and status columns case-insensitive; ship-to FK targets `UQ_CustomerLocation_Location_Customer`; no reference crosses the ERP boundary |
| D Data | 8 | Counts 2, 1, 2; WEB-10001 accepted as SO-0001 for C0001 at LOC-01, Received; snapshot reproduces the submitted lines in order; each SKU resolves to the stored product; lines numbered 1..n; stock equals the fixture |
| Q Readiness | 2 | SO-0001 at WH-01 assesses Available (3/10, 2/5), as `InventoryAvailability` expects; order quantities equal the Stage 2 dispatch sample (P000142 ×3, P000143 ×2) |
| C Constraints | 20 | 17 invalid writes rejected by the named constraint or column; 3 valid writes accepted; all rolled back |
| Z Repeatability | 1 | No rows left behind across all 11 ERP tables; no open transaction |

Q01 reproduces the expected availability outcome with a query; it is not an availability function, and nothing is stored.

## Traceability

| C# rule (`OrderIntake`, `InventoryAvailability`) | Database object | Verified by |
| --- | --- | --- |
| Source order key unique, case-insensitive; other source systems may reuse the number | `UQ_SalesOrder_Source` | C01, C08 |
| Order ID unique | `PK_SalesOrder` | C02 |
| Unknown customer rejected | `FK_SalesOrder_Customer` | C03 |
| Ship-to must belong to the customer | `FK_SalesOrder_ShipTo` → `UQ_CustomerLocation_Location_Customer` | C04, S06 |
| Missing ship-to rejected | NOT NULL | C05 |
| Status `Received` only | `CK_SalesOrder_Status` | C06 |
| Source system and external order ID required | `CK_SalesOrder_*_NotBlank` | C07 |
| Nonpositive quantity rejected | `CK_SalesOrderLine_Quantity_Positive` | C09 |
| Duplicate line IDs rejected, case-insensitive | `PK_SalesOrderLine` | C10 |
| Submitted line order and SKU retained (D7) | `LineSequence`, `SourceSku`, `UQ_SalesOrderLine_Sequence`, `CK_SalesOrderLine_LineSequence_Positive` | C11, C12, D05 |
| SKU and line ID required | `CK_SalesOrderLine_*_NotBlank` | C13 |
| Line product and order exist | `FK_SalesOrderLine_Product`, `FK_SalesOrderLine_SalesOrder` | C14, C15 |
| Repeated products allowed; demand aggregates at assessment | No uniqueness on product per order | C16, Q01 |
| Negative stock rejected | `CK_StockOnHand_OnHand_NonNegative` | C17 |
| Stock only for an inventory position at that warehouse | `FK_StockOnHand_InventoryPosition` | C18 |
| Duplicate stock snapshot rejected | `PK_StockOnHand` | C19 |
| Missing stock is Unknown, not zero | No default row or value; zero is a real quantity | C20, Q01 |
| At least one line; SKU resolves to the line's product | C# only | D06, D07 |

## Verification status

Not yet executed on SQL Server. Before delivery:

- Every T-SQL batch in increments 1 and 2 parsed with sqlglot (T-SQL dialect). Batches with THROW inside BEGIN...END were re-parsed with THROW neutralized, a known parser limitation.
- Every constraint, index and identifier-column name in 920 matches the DDL; every case's expected constraint exists.
- The increment 2 SQL files contain only ASCII.
- A SQLite emulation built both increments and loaded both seeds twice; the rerun added nothing. It ran all 910 and 920 cases plus the D, Q and R expectations: 55 of 55 passed, including the increment 1 cases with increment 2 present. Stock and SO-0001 equal the JSON fixtures.
- Not covered by that emulation: catalog checks S01–S07, SQL Server error numbers and message text, collation behavior on Freya, and the sqlcmd runner under PowerShell 5.1. The emulation used the original `910`, not your corrected copy; the correction touches only a catalog comparison the emulation does not run.

Your run on Freya is the authoritative Verify step.
