/*
    Frontier Distribution working model - ERP Business Application
    Increment 2: ERP sales orders and inventory
    Script 980: Remove increment 2 (for a clean rebuild)

    Drops erp.SalesOrderLine, erp.SalesOrder and erp.StockOnHand with their
    synthetic data. The ERP foundation (increment 1) is left untouched.
    All-or-nothing: if a later increment still references these tables, the drop
    fails and nothing is removed; remove the later increment first.
    Synthetic model only. Never run against a database holding real data.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;
DROP TABLE IF EXISTS erp.SalesOrderLine;
DROP TABLE IF EXISTS erp.SalesOrder;
DROP TABLE IF EXISTS erp.StockOnHand;
COMMIT TRANSACTION;

PRINT N'980-erp-remove-sales-inventory: increment 2 removed; ERP foundation untouched.';
GO
