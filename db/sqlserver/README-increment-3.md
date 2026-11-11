# Frontier Distribution — SQL Server Increment 3: Voxware warehouse fulfillment

This is a synthetic, executable best-guess data model, not Frontier's production schema.

## Package provenance

This package contains only Increment 3 files. It includes nothing from Increments 1 or 2 and no top-level `README.md`. Unzipping it at the repository root creates a new folder, `db/sqlserver/voxware/`, holding 8 SQL files, and adds this document. It overwrites nothing.

| File | SHA-256 |
| --- | --- |
| `voxware/010-voxware-schema.sql` | `47BF48FC897AC3E8734296408E7489FCE2F0F7591FDED56017050E5A98189565` |
| `voxware/020-voxware-pick-plan.sql` | `EFEEB0ABB01F0634B0C02B6D7A9619449D7B6CA8CA103E4501EE311EB153A08E` |
| `voxware/030-voxware-pick-confirmation.sql` | `168F34CE923FA871C8F6CECACE7B4E78A23B8995BE961A9377BB967022313173` |
| `voxware/040-voxware-packing.sql` | `0E5CD225EED134098E6DCB1F53DD19F5D1F2402E6FC10251C91C052706AA7618` |
| `voxware/110-voxware-seed-fulfillment.sql` | `3AB1B25762665B3719DEE6E93B4ED701314D806170521780C8A9854CC2DDA36E` |
| `voxware/910-voxware-verify-fulfillment.sql` | `6924F9B41175AEEFC200D81D32EAFEA126AB30CD69F3BCD3B3D7B0318634CC44` |
| `voxware/990-voxware-remove-fulfillment.sql` | `C44684175D6FB957F1D6A21569AA55F8CA4FE4228BA2E368C47547F3CE04057C` |
| `voxware/install-voxware-fulfillment.sql` | `812BF57AAC5C13479DEF89D82D727BD95911BDB782B7DB2F48326FDD78F20716` |

To check the hashes in Windows PowerShell 5.1, run this from the repository root:

```powershell
Get-FileHash .\db\sqlserver\voxware\*.sql |
    Format-Table Hash, @{ n = 'File'; e = { Split-Path $_.Path -Leaf } } -AutoSize
```

## Scope

Voxware owns Picking, PickExecution and Packing. This increment gives those three components their own data model: six tables in schema `voxware`, loaded with the Stage 2 fulfillment of ERP order SO-0001.

ERP stays the authority for sales orders, warehouses, products and inventory. `SalesOrderId`, `WarehouseId` and `ProductId` appear in Voxware only as plain values. Increment 3 has no foreign key, view, synonym or query that reaches the ERP model, and it changes nothing in ERP. The scripts name only the `voxware` schema and need nothing from ERP, so they install the same way into FrontierModel or into an empty database. A schema is one way to place the model; the logical separation does not depend on it.

| File | Step | Purpose |
| --- | --- | --- |
| `voxware/010-voxware-schema.sql` | Build | Schema `voxware` |
| `voxware/020-voxware-pick-plan.sql` | Build | PickPlan, PickPlanLine (`PickPlanning`) |
| `voxware/030-voxware-pick-confirmation.sql` | Build | PickConfirmation, PickConfirmationLine (`PickExecution`) |
| `voxware/040-voxware-packing.sql` | Build | PackingConfirmation, PackingConfirmationLine (`Packing`) |
| `voxware/110-voxware-seed-fulfillment.sql` | Execute | SO-0001 at WH-01: Ready plan, PICK-0001 Completed, PACK-0001 ReadyForShipment |
| `voxware/install-voxware-fulfillment.sql` | Build + Execute | sqlcmd runner for 010, 020, 030, 040, 110 |
| `voxware/910-voxware-verify-fulfillment.sql` | Verify | Runs 46 checks, is read-only, and raises error 50003 if any check fails |
| `voxware/990-voxware-remove-fulfillment.sql` | Reset | Drops the six Voxware tables and then the schema if it is empty; touches nothing else |

### Tables

Every record carries the correlation the C# components use: `(SalesOrderId, WarehouseId)`.

| Table | Key | Rules in the database | Rules left in C# (910 checks the stored data) |
| --- | --- | --- | --- |
| PickPlan | (SalesOrderId, WarehouseId) | Status is `Ready`, `BlockedShort` or `BlockedUnknown`; IDs are non-blank | Status derived from ERP's availability assessment (the assessment is not stored); blocked plans have no lines and Ready plans have at least one (D08) |
| PickPlanLine | (SalesOrderId, WarehouseId, ProductId) | Plan exists; one line per product (demand aggregated); quantity > 0 | The aggregation itself |
| PickConfirmation | (SalesOrderId, WarehouseId) | Plan exists; `ConfirmationId` is required and non-blank; outcome is `Completed` or `Partial` | Ready plans only; every planned product covered; 0 ≤ picked ≤ planned (D09); outcome derivation (D10) |
| PickConfirmationLine | (SalesOrderId, WarehouseId, ProductId) | Confirmation exists; product is on the plan; quantity ≥ 0 | See PickConfirmation |
| PackingConfirmation | (SalesOrderId, WarehouseId) | `PickConfirmationId` must be the same plan's `ConfirmationId` (composite foreign key); `PackingId` is required and non-blank; outcome is `ReadyForShipment` or `Incomplete` | ReadyForShipment only when the pick is complete and every packed quantity equals planned (D12) |
| PackingConfirmationLine | (SalesOrderId, WarehouseId, ProductId) | Packing exists; product has a picked line; quantity ≥ 0 | Every planned product covered; packed ≤ picked (D11) |

Design notes:

- **D8.** `ConfirmationId` and `PackingId` are required references, not global keys. Two plans may carry the same value, as the code allows (C15, C23, S05). `UQ_PickConfirmation_Reference` (SalesOrderId, WarehouseId, ConfirmationId) contains the primary key, so it adds no uniqueness. It exists only as the target of the packing's composite foreign key.
- **D9.** Identifiers match case-insensitively throughout (C01, C04, C18).
- **Replays.** C# returns `AlreadyRecorded` for an equivalent replay and rejects a conflicting one. The tables hold one confirmation and one packing per plan, so the primary key rejects a second, different record (C07, C16). The seed inserts only what is missing. The equivalence comparison itself stays in C#.
- **What is stored.** Only recorded outcomes are stored. A rejected attempt does not occupy the plan in C#, so `Rejected` and `AlreadyRecorded` are not stored statuses. Result messages are display text and are not stored either.
- **Rules left in C#.** Upper bounds, coverage and outcome derivation span tables. Enforcing them would take triggers or duplicated quantities, which no demonstrated requirement calls for. 910 checks them on the stored data instead, and each such check was shown to detect its violation (see Verification status).
- **D6 stays open.** A ReadyForShipment packing holds exactly what `Shipping.Create` takes: SalesOrderId, WarehouseId, PackingId, PickConfirmationId and product lines (I02). Nothing models how that reaches Route4Me, whether directly or through ERP. There is no shipment table, handoff status or dispatch data.

## Run on Freya — Windows PowerShell 5.1 and sqlcmd

Run from the repository root, and set `$Server` to Freya's instance name.

```powershell
$Server   = 'Freya'
$Database = 'FrontierModel'

Push-Location .\db\sqlserver\voxware
try {
    foreach ($step in 'install-voxware-fulfillment.sql',
                      '910-voxware-verify-fulfillment.sql') {
        Write-Host "== $step"
        sqlcmd -S $Server -d $Database -E -I -b -W -s '|' -i $step
        if ($LASTEXITCODE -ne 0) { throw "$step failed (sqlcmd exit code $LASTEXITCODE)" }
    }
}
finally { Pop-Location }
```

Expected results:

- **Installer.** It prints one message per script and the row counts PickPlan 1, PickPlanLine 2, PickConfirmation 1, PickConfirmationLine 2, PackingConfirmation 1, PackingConfirmationLine 2.
- **910.** It prints the environment line (server collation, database collation, version) and one row per check, then ends with `910-voxware-verify-fulfillment: 46/46 checks passed.`
- **Exit codes.** Both steps exit with code 0.

To confirm ERP is unaffected, run your verified ERP scripts afterwards. They should still report 49/49 and 38/38, because those checks are scoped to schema `erp`. Their boundary checks (S07) would also catch any foreign key or module reference from `voxware` into `erp`.

```powershell
Push-Location .\db\sqlserver\erp
try {
    foreach ($step in '910-erp-verify-foundation.sql', '920-erp-verify-sales-inventory.sql') {
        Write-Host "== $step"
        sqlcmd -S $Server -d $Database -E -I -b -W -s '|' -i $step
        if ($LASTEXITCODE -ne 0) { throw "$step failed (sqlcmd exit code $LASTEXITCODE)" }
    }
}
finally { Pop-Location }
```

Run notes:

- **PowerShell syntax.** The loops use only Windows PowerShell 5.1 syntax: no `&&`, no ternary and no PowerShell 7 cmdlets.
- **Working folder.** Run the installer from the `voxware` folder, because `:r` resolves file names against the current directory.
- **sqlcmd options.** `-b` makes any SQL error, including 910's error 50003, return a nonzero exit code. `-I` sets QUOTED_IDENTIFIER on.
- **Repeatability.** Running the installer and 910 again changes nothing and gives the same results.
- **Clean rebuild.** Run `990-voxware-remove-fulfillment.sql`, then the installer.

## Collation environment

- **Columns.** All 21 identifier, status and outcome columns declare `Latin1_General_100_CI_AS`. Keys, foreign keys and checks therefore behave the same whatever Freya's server or database default collation is (D9).
- **Catalog comparisons.** 910 compares catalog values with `COLLATE DATABASE_DEFAULT` on both sides, including `sys.objects.type`, which carries a fixed system collation. This is the same fix as your corrected ERP `910`.
- **Literals.** When a literal meets a Voxware column, it takes that column's collation, so the data checks give the same result under any database default.
- **No temp tables.** No `#temp` tables are used, so tempdb's collation never takes part.
- **Environment line.** 910 prints the server and database collations for the record. They do not affect the result.
- **Encoding.** Every Increment 3 SQL file is ASCII-only, so sqlcmd reads it the same under any Windows code page.

## What verification covers

| Area | Checks | What passes means |
| --- | --- | --- |
| S Structure | 7 | Schema present with exactly the 6 tables; 25 named keys, foreign keys and checks on their tables; 21 columns case-insensitive; the 3 headers keyed by (SalesOrderId, WarehouseId) with no global ID key; all 7 foreign keys supported by an index; no foreign key or module reference crosses the Voxware boundary |
| D Data | 12 | Row counts 1, 2, 1, 2, 1, 2. The Ready plan has P000142 ×3 and P000143 ×2. The C# rules not enforced by tables hold on the stored data: blocked plans have no lines; pick coverage and bounds; pick outcome; packing coverage with packed ≤ picked; ReadyForShipment conditions |
| I Integration references | 2 | Voxware carries only the ERP identifiers issued for SO-0001 (WH-01, P000142, P000143). The ReadyForShipment packing supplies the shipment contract the Shipping harness and `samples/stage2-dispatch.json` use: PACK-0001, PICK-0001, P000142 ×3, P000143 ×2 |
| C Constraints | 24 | 19 invalid writes are rejected by the named constraint, covering every foreign key and every status or outcome check. 5 valid writes are accepted. All are rolled back |
| Z Repeatability | 1 | No rows are left behind in the 6 tables and no transaction is left open |

The integration checks compare values; they never query ERP. The ERP model's own `920` verifies the same values on its side:

- **D04:** SO-0001 accepted.
- **D06:** P000142 and P000143 resolve.
- **Q01:** SO-0001 is Available at WH-01, which is why the plan is Ready.
- **Q02:** quantities are 3 and 2.

Matching by value on both sides is the integration evidence, and it couples no tables.

## Traceability

| C# rule (`PickPlanning`, `PickExecution`, `Packing`) | Database object | Verified by |
| --- | --- | --- |
| One plan per order and warehouse, case-insensitive | `PK_PickPlan` | C01, S05 |
| Plan status Ready, BlockedShort or BlockedUnknown | `CK_PickPlan_Status` | C02 |
| Plan lines belong to a plan | `FK_PickPlanLine_PickPlan` | C03 |
| Demand aggregated: one line per product, case-insensitive | `PK_PickPlanLine` | C04, D07 |
| Planned quantity positive | `CK_PickPlanLine_Quantity_Positive` | C05 |
| Blocked plans have no lines | C# only | C06, D08 |
| One pick confirmation per plan; a different one is rejected | `PK_PickConfirmation` | C07 |
| Pick confirmation needs a plan; Ready plans only | `FK_PickConfirmation_PickPlan`; Ready is C# only | C08, D09 |
| ConfirmationId required, not globally unique (D8) | `CK_PickConfirmation_ConfirmationId_NotBlank`; no global key | C09, C15, S05 |
| Pick outcome Completed or Partial | `CK_PickConfirmation_Outcome`; derivation is C# only | C10, D10 |
| Picked lines belong to the confirmation and cover planned products only | `FK_PickConfirmationLine_PickConfirmation`, `FK_PickConfirmationLine_PickPlanLine` | C11, C12 |
| Every planned product picked once, 0 ≤ picked ≤ planned | Lower bound `CK_PickConfirmationLine_Quantity_NonNegative`; coverage and upper bound C# only | C13, C14, D09 |
| One packing per plan; a different one is rejected | `PK_PackingConfirmation` | C16 |
| Packing references the same plan's pick confirmation, case-insensitive | `FK_PackingConfirmation_PickConfirmation` → `UQ_PickConfirmation_Reference` | C17, C18 |
| Packed lines belong to the packing and to picked products | `FK_PackingConfirmationLine_PackingConfirmation`, `FK_PackingConfirmationLine_PickConfirmationLine` | C19, C20 |
| Every planned product packed; 0 ≤ packed ≤ picked | Lower bound `CK_PackingConfirmationLine_Quantity_NonNegative`; coverage and upper bound C# only | C21, D11 |
| Packing outcome ReadyForShipment or Incomplete | `CK_PackingConfirmation_Outcome`; derivation is C# only | C22, D12 |
| PackingId required, not globally unique (D8) | `CK_PackingConfirmation_PackingId_NotBlank`; no global key | C23, C24, S05 |
| ReadyForShipment supplies the shipment contract | Packing tables; how it reaches Route4Me is open (D6) | I02 |

## Verification status

This has not yet been run on SQL Server. Checks done before delivery:

- **Parsing.** Every T-SQL batch in Increment 3 parsed with sqlglot (T-SQL dialect). Batches with THROW inside BEGIN...END were re-parsed with THROW neutralized, which works around a known parser limitation.
- **Names.** Every constraint name in 910 matches the DDL, table and type, and every case's expected constraint exists. The 21 columns that S04 expects are exactly the DDL's CI_AS columns. Every foreign key is exercised by a case.
- **Encoding and installer.** Every SQL file is ASCII-only. The installer runs 010, 020, 030, 040, 110 in that order under `:on error exit`.
- **SQLite emulation, 55 of 55 passed:**
  - It built the tables and loaded the seed twice; the rerun added nothing.
  - All 24 cases ran. Foreign-key cases were also matched to the exact parent table.
  - The D and I expectations held on the seeded data.
  - 15 deliberate violations were each detected by their D or I check.
  - 990 dropped all six tables in a foreign-key-safe order.
  - The packed lines equal `samples/stage2-dispatch.json`.
- **Not covered by the emulation:**
  - catalog checks S01–S07;
  - SQL Server error numbers and message text;
  - collation behavior on Freya;
  - the sqlcmd runner under PowerShell 5.1.

Your run on Freya is the authoritative Verify step.
