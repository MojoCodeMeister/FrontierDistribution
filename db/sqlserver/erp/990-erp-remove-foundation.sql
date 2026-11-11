/*
    Frontier Distribution working model — ERP Business Application
    Increment 1: ERP foundation
    Script 990: Remove the ERP foundation (for a clean rebuild)

    Drops the 8 foundation tables and their synthetic data, then the erp schema
    if nothing else remains in it. All-or-nothing: if a later increment's table
    still references these tables, the drop fails and nothing is removed;
    remove the later increment first.
    Synthetic model only. Never run against a database holding real data.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;
DROP TABLE IF EXISTS erp.ProductIdentity;
DROP TABLE IF EXISTS erp.InventoryPosition;
DROP TABLE IF EXISTS erp.SupplierProduct;
DROP TABLE IF EXISTS erp.CustomerLocation;
DROP TABLE IF EXISTS erp.Warehouse;
DROP TABLE IF EXISTS erp.Supplier;
DROP TABLE IF EXISTS erp.Product;
DROP TABLE IF EXISTS erp.Customer;
COMMIT TRANSACTION;

IF SCHEMA_ID(N'erp') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID(N'erp'))
    EXEC (N'DROP SCHEMA erp;');

PRINT N'990-erp-remove-foundation: ERP foundation removed.';
GO
