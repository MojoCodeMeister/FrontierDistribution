# Frontier Distribution — SQL Server Increment 4: Route4Me shipment, dispatch and delivery

This is a synthetic, executable best-guess data model, not Frontier's production schema. Increment 4 completes the proposed 22-table initial structure: ERP 11, Voxware 6, Route4Me 5, nopCommerce 0. That does not mean C# persistence or downstream ERP processing is done.

## Package provenance

This package contains only Increment 4 files. It includes no ERP or Voxware script and no top-level `README.md`. Unzipping at the repository root creates a new folder, `db/sqlserver/route4me/`, with 7 SQL files and adds this document. Nothing is overwritten.

| File | SHA-256 |
| --- | --- |
| `route4me/010-route4me-schema.sql` | `5FD4B612DED1C87CE93E97DCDB2ED8F1C4A4A0F29A006C68B564E738EA8B839E` |
| `route4me/020-route4me-shipment.sql` | `970CD7F77E1C4E83883C9540C931EBE9AA5F15F9E92AAFB4F6560A174B55696E` |
| `route4me/030-route4me-delivery.sql` | `80CF284C875D2BDCEF1AE2C90F59D45A657CAE4CBDBE7536EF48ED41D671E642` |
| `route4me/110-route4me-seed-shipment-delivery.sql` | `DB6B34AE67C55FBD9F5E9D3CE23B05F99421B9154053FA4228AB441A89E45554` |
| `route4me/910-route4me-verify-shipment-delivery.sql` | `DDEE4E71C09F333DB90FCF3CCB20316DAFD26E0864E191B8470100F6CF516A48` |
| `route4me/990-route4me-remove-shipment-delivery.sql` | `EA03F9348BCA2EF08D7AEA997E381693B63DE0F5A76597BE17E76C0592026019` |
| `route4me/install-route4me-shipment-delivery.sql` | `6CA8B4C672F182A724A5F554E4C04E951691B82F3BC5D9ABF5F525561F96ECE4` |

To check the hashes in Windows PowerShell 5.1, run this from the repository root:

```powershell
Get-FileHash .\db\sqlserver\route4me\*.sql |
    Format-Table Hash, @{ n = 'File'; e = { Split-Path $_.Path -Leaf } } -AutoSize
```

## Scope

Route4Me owns Frontier.Shipping and Frontier.Delivery. Shipping sits in the `Frontier.Orders` assembly, but that placement does not make its records ERP's. This increment adds Route4Me's data model: five tables in schema `route4me`, loaded with SHIP-0001 for ERP order SO-0001. The data continues the Voxware fulfillment from Increment 3.

| File | Step | Purpose |
| --- | --- | --- |
| `route4me/010-route4me-schema.sql` | Build | Schema `route4me` |
| `route4me/020-route4me-shipment.sql` | Build | Shipment (with dispatch), ShipmentLine (`Shipping`) |
| `route4me/030-route4me-delivery.sql` | Build | DeliveryShipment, DeliveryShipmentLine, DeliveryEvent (`DeliveryExecution`) |
| `route4me/110-route4me-seed-shipment-delivery.sql` | Execute | SHIP-0001 dispatched as DISP-0001; handed to Delivery; DEL-0001 Delivered with POD-0001 |
| `route4me/install-route4me-shipment-delivery.sql` | Build + Execute | sqlcmd runner for 010, 020, 030, 110 |
| `route4me/910-route4me-verify-shipment-delivery.sql` | Verify | Runs **53** checks, is read-only, and raises error 50003 if any check fails or the count differs |
| `route4me/990-route4me-remove-shipment-delivery.sql` | Reset | Drops the five Route4Me tables and then the schema if it is empty; touches nothing else |

### Dependencies

- **Database.** None on ERP or Voxware. No foreign key, view, procedure, synonym or query reaches another model. Increment 4 installs and verifies on its own, and installs the same way into an empty database.
- **Data, by value only.** The seed carries ERP identifiers (SO-0001, WH-01, P000142, P000143) and Voxware references (PACK-0001, PICK-0001) as plain values. They match the Increment 2 and 3 data, and each model's own verification checks its side.
- **Tools.** SQL Server 2016 or later (`DROP TABLE IF EXISTS`, filtered indexes, `datetimeoffset`). sqlcmd. Windows PowerShell 5.1.

### Tables

| Table | Key | Rules in the database | Rules left in C# (910 checks the stored data) |
| --- | --- | --- | --- |
| Shipment | ShipmentId | One shipment per (SalesOrderId, WarehouseId) (`UQ_Shipment_Order`). ShipmentId is never reused. IDs and references are non-blank. `Prepared` has no DispatchId; `Dispatched` has a non-blank one (`CK_Shipment_Status`). DispatchId is never reused (`UX_Shipment_DispatchId`, filtered to non-null) | Created only from a ReadyForShipment packing, a check that crosses the Voxware boundary; replay comparison; dispatch only from Prepared, so a dispatched shipment keeps its DispatchId; at least one line (D09) |
| ShipmentLine | (ShipmentId, ProductId) | Shipment exists; one line per product; quantity > 0 | Lines copied from the packed lines (I02) |
| DeliveryShipment | ShipmentId | IDs non-blank. Status is stored as received, with no CHECK, because Delivery accepts any status at registration | Outcome only when Dispatched (D11); handoff values preserved (D10); at least one line (D09) |
| DeliveryShipmentLine | (ShipmentId, ProductId) | Delivery shipment exists; one line per product; quantity > 0 | Never changed after registration |
| DeliveryEvent | EventId | EventId unique; one terminal outcome per shipment (`UQ_DeliveryEvent_Shipment`); delivery shipment exists. `Delivered` needs a non-blank ProofReference and `Exception` a non-blank ExceptionReason (`CK_DeliveryEvent_Outcome`). OccurredAt is required: NOT NULL and not the .NET default `0001-01-01T00:00:00+00:00` | Full-record replay equivalence |

Design notes:

- **Dispatch has no table of its own.** It is recorded as `Status` + `DispatchId` on Shipment, as `Shipping.Dispatch` records it. One dispatch per shipment follows from the single column. Prepared → Dispatched is an update (C08).
- **DeliveryShipment stays separate.** It is Frontier.Delivery's own boundary record. It has no foreign key to Shipment, as the approved r2 assessment specified: Delivery registers shipments on its own terms, as tested (C15), and Shipping → Delivery is a component handoff inside Route4Me. D10 shows the stored handoff kept ShipmentId, SalesOrderId, WarehouseId, products and quantities.
- **D9: case-insensitive throughout.** All 19 identifier, status, outcome and reference columns are `Latin1_General_100_CI_AS`, and there is no BIN2 (S06). `Frontier.Delivery` compares ordinally in C#, so C# would register `ship-0001` beside `SHIP-0001`, but the database rejects it (C14). That is the known, deferred comparer discrepancy. C# is unchanged.
- **Replays.** Identical replays of shipment, dispatch and delivery are answered by C#. In the database:
  - a conflicting second shipment or outcome is rejected by `UQ_Shipment_Order` and `UQ_DeliveryEvent_Shipment` (C01, C20);
  - a DispatchId already used by another shipment is rejected by `UX_Shipment_DispatchId` (C09);
  - re-dispatching an already dispatched shipment under a different DispatchId is refused by C#, and a writer dispatches with a conditional update from Prepared (r2 conventions);
  - the seed inserts only what is missing.
- **What is stored.** Only recorded outcomes. There are no Rejected or AlreadyRecorded statuses, no messages, and no audit, outbox or messaging tables.
- **Not restricted.** A Delivered event may also carry an ExceptionReason, and an Exception a ProofReference, as in the code. `ExceptionReason` is free text (`nvarchar(500)`) and keeps the database default collation, like the ERP text columns.
- **D6 stays open.** Shipment holds what the existing Packing → Shipping contract supplies (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, lines; I02). Nothing records how a ready shipment reaches Route4Me, whether directly or through ERP.
- **Delivery outcome to ERP is future.** DeliveryEvent holds what that exchange would carry (EventId, ShipmentId, outcome, proof or reason), but nothing models the exchange.

## Run on Freya — Windows PowerShell 5.1 and sqlcmd

Run from the repository root, and set `$Server` to Freya's instance name.

```powershell
$Server   = 'Freya'
$Database = 'FrontierModel'

Push-Location .\db\sqlserver\route4me
try {
    foreach ($step in 'install-route4me-shipment-delivery.sql',
                      '910-route4me-verify-shipment-delivery.sql') {
        Write-Host "== $step"
        sqlcmd -S $Server -d $Database -E -I -b -W -s '|' -i $step
        if ($LASTEXITCODE -ne 0) { throw "$step failed (sqlcmd exit code $LASTEXITCODE)" }
    }
}
finally { Pop-Location }
```

Expected results:

- **Installer.** It prints one message per script and the row counts Shipment 1, ShipmentLine 2, DeliveryShipment 1, DeliveryShipmentLine 2, DeliveryEvent 1.
- **910.** It prints the environment line (server collation, database collation, version) and 53 rows, then ends with `910-route4me-verify-shipment-delivery: 53/53 checks passed.`
- **Exit codes.** Both steps exit with code 0.

### Full regression: all four models, 22 tables

Your verified ERP and Voxware scripts are not in this package. The verification scripts are scoped to their own schema, so running all four after the install shows that no model disturbed another.

```powershell
$Server   = 'Freya'
$Database = 'FrontierModel'

$runs = @(
    @{ Folder = '.\db\sqlserver\erp';      Script = '910-erp-verify-foundation.sql' },
    @{ Folder = '.\db\sqlserver\erp';      Script = '920-erp-verify-sales-inventory.sql' },
    @{ Folder = '.\db\sqlserver\voxware';  Script = '910-voxware-verify-fulfillment.sql' },
    @{ Folder = '.\db\sqlserver\route4me'; Script = '910-route4me-verify-shipment-delivery.sql' }
)
foreach ($run in $runs) {
    Push-Location $run.Folder
    try {
        Write-Host "== $($run.Script)"
        sqlcmd -S $Server -d $Database -E -I -b -W -s '|' -i $run.Script
        if ($LASTEXITCODE -ne 0) { throw "$($run.Script) failed (sqlcmd exit code $LASTEXITCODE)" }
    }
    finally { Pop-Location }
}
```

Expected: 49/49, 38/38, 46/46 and 53/53, which is **186/186** across 22 tables, and every step exits with code 0.

Run notes:

- **PowerShell syntax.** The loops use only Windows PowerShell 5.1 syntax: no `&&`, no ternary and no PowerShell 7 cmdlets.
- **Working folder.** Run the installer from the `route4me` folder, because `:r` resolves file names against the current directory.
- **sqlcmd options.** `-b` makes any SQL error, including 910's error 50003, return a nonzero exit code. `-I` sets QUOTED_IDENTIFIER on, which the filtered index needs for writes.
- **Repeatability.** Running the installer and 910 again changes nothing and gives the same results.
- **Clean rebuild.** Run `990-route4me-remove-shipment-delivery.sql`, then the installer.

## Collation environment

Freya's server and database collation is `SQL_Latin1_General_CP1_CI_AS`, and the approved identifier collation is `Latin1_General_100_CI_AS`.

- **Columns.** All 19 identifier, status, outcome and reference columns declare `Latin1_General_100_CI_AS`, so keys, unique indexes and checks behave the same under any server or database default.
- **Catalog comparisons.** 910 compares catalog values with `COLLATE DATABASE_DEFAULT` on both sides. That covers names, `sys.objects.type`, and key and collation names. The DDL's index-existence test does the same. This is the Increment 1 issue, not reintroduced.
- **Literals.** When a literal meets a Route4Me column, it takes that column's collation. Table variables meet only catalog values, always with explicit collation on both sides. No `#temp` tables are used.
- **Environment line.** 910 prints the server and database collations for the record. They do not affect the result.
- **Encoding.** Every Increment 4 SQL file is ASCII-only.

## What verification covers — 53 checks

| Area | Checks | What passes means |
| --- | --- | --- |
| S Structure | 9 | Schema present with exactly the 5 tables. 26 named keys, foreign keys and checks are on their tables. 8 unique keys have their designed columns. The DispatchId index is unique and filtered. 19 columns are case-insensitive. Exactly 3 foreign keys, none between the Shipping and Delivery tables, all supported by an index. Nothing crosses the Route4Me boundary |
| D Data | 11 | Row counts 1, 2, 1, 2, 1. SHIP-0001 is Dispatched as DISP-0001. Delivery holds SHIP-0001 exactly as `samples/stage2-dispatch.json` hands it over. DEL-0001 is Delivered with POD-0001 at 2026-11-09 12:00 −05:00, offset kept. Every shipment has lines. The handoff is preserved. Outcomes exist only for Dispatched delivery shipments |
| I Integration references | 2 | Only the ERP identifiers issued for SO-0001 appear. The shipment keeps PACK-0001, PICK-0001 and the accepted quantities (P000142 ×3, P000143 ×2) |
| C Constraints | 30 | 26 invalid writes are rejected by the named constraint, index or column, covering every foreign key. 4 valid writes are accepted. All are rolled back |
| Z Repeatability | 1 | No rows are left behind in the 5 tables and no transaction is left open |

The integration checks compare values; they never query another model. The other side of the same values:

- **Voxware 910 I02:** the ReadyForShipment packing PACK-0001 / PICK-0001 with P000142 ×3 and P000143 ×2.
- **ERP 920 D04 and Q02:** SO-0001, with order quantities 3 and 2.

## Traceability

| C# rule (`Shipping`, `DeliveryExecution`) | Database object | Verified by |
| --- | --- | --- |
| One shipment per order and warehouse, case-insensitive | `UQ_Shipment_Order` | C01, S04 |
| ShipmentId unique; cannot be reused for another order | `PK_Shipment` | C02 |
| Shipment, packing and pick IDs required | `CK_Shipment_*_NotBlank` | C03, C04 |
| Status Prepared or Dispatched; one dispatch per shipment | `CK_Shipment_Status`; single DispatchId column | C05, C06, C07, C08 |
| DispatchId never reused, case-insensitive | `UX_Shipment_DispatchId` (filtered) | C09, C10, S05 |
| Lines: distinct products, positive quantities | `PK_ShipmentLine`, `CK_ShipmentLine_Quantity_Positive`, `FK_ShipmentLine_Shipment` | C11, C12, C13 |
| Created from a ReadyForShipment packing; references and quantities preserved | Carried values only (crosses Voxware) | I02 |
| Delivery ShipmentId registered once | `PK_DeliveryShipment` (CI per D9) | C14 |
| Registration independent of Shipping; any status accepted | No FK to Shipment; no CHECK on Status | C15, S07 |
| Delivery IDs required; lines distinct and positive | `CK_DeliveryShipment_*`, `PK_DeliveryShipmentLine`, `CK_DeliveryShipmentLine_Quantity_Positive`, `FK_DeliveryShipmentLine_DeliveryShipment` | C16–C19 |
| Values preserved across the handoff | C# contract; checked on stored data | D07, D10 |
| One terminal outcome per shipment | `UQ_DeliveryEvent_Shipment` | C20 |
| EventId unique | `PK_DeliveryEvent` | C21 |
| Outcome only for a registered shipment | `FK_DeliveryEvent_DeliveryShipment` | C22 |
| Outcome only for a Dispatched shipment | C# only (cross-row) | D11 |
| Delivered needs proof; Exception needs reason; outcome Delivered or Exception | `CK_DeliveryEvent_Outcome` | C23–C27 |
| OccurredAt required | NOT NULL; `CK_DeliveryEvent_OccurredAt_Required` | C28, C29 |
| EventId required | `CK_DeliveryEvent_EventId_NotBlank` | C30 |

## Verification status

This has not yet been run on SQL Server. Checks done before delivery:

- **Parsing.** Every T-SQL batch in Increment 4 parsed with sqlglot (T-SQL dialect). Batches with THROW inside BEGIN...END were re-parsed with THROW neutralized, which works around a known parser limitation.
- **Names and coverage.** 910's 26 constraints, 8 unique keys (with columns) and 19 CI columns match the DDL exactly. Every case's expected constraint or index exists, and every foreign key is exercised.
- **Encoding and installer.** Every SQL file is ASCII-only. The installer runs 010, 020, 030, 110 in order under `:on error exit`.
- **SQLite emulation, 60 of 60 passed:**
  - The emulation stood in for case-insensitive collation and the filtered unique index.
  - It loaded the seed twice; the rerun added nothing.
  - All 30 cases ran. Unique-key cases matched the exact key columns, and foreign-key cases the exact parent table.
  - The D and I expectations held.
  - 15 deliberate violations were each caught by their D or I check.
  - 990 dropped all five tables in a foreign-key-safe order.
  - Delivery's SHIP-0001 equals `samples/stage2-dispatch.json`.
- **Not covered by the emulation:**
  - catalog checks S01–S09;
  - SQL Server error numbers and message text;
  - `datetimeoffset` offset handling (D08's `TZOFFSET`);
  - collation behavior on Freya;
  - the sqlcmd runner under PowerShell 5.1.

Your run on Freya is the authoritative Verify step.
