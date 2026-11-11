/*
    Frontier Distribution working model - ERP Business Application
    Increment 2: ERP sales orders and inventory
    Script 040: Stock on hand

    Source record: StockQuantity in src/Frontier.Orders/InventoryAvailability.cs
    Owner:         ERP (Frontier.Inventory; decision D5, approved 2026-11-11)

    On-hand snapshot only: no reservation, allocation or availability columns
    (Q24 stays open). A missing row means Unknown, never zero, so there is no
    default quantity. Stock exists only for an existing inventory position.
    Availability assessment stays in C# and is not stored.

    Identifier columns declare Latin1_General_100_CI_AS (decision D9).
    Requires increment 1 (ERP foundation). Rerunnable.
    Synthetic model. Not Frontier's production database.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'erp.InventoryPosition', N'U') IS NULL
    THROW 50001, N'ERP foundation is missing. Install increment 1 (install-erp-foundation.sql) first.', 1;

IF OBJECT_ID(N'erp.StockOnHand', N'U') IS NULL
CREATE TABLE erp.StockOnHand
(
    WarehouseId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId    varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    OnHand       int         NOT NULL,
    CONSTRAINT PK_StockOnHand PRIMARY KEY CLUSTERED (WarehouseId, ProductId),
    CONSTRAINT FK_StockOnHand_InventoryPosition FOREIGN KEY (WarehouseId, ProductId)
        REFERENCES erp.InventoryPosition (WarehouseId, ProductId),
    CONSTRAINT CK_StockOnHand_OnHand_NonNegative CHECK (OnHand >= 0)
);

PRINT N'040-erp-stock-on-hand: erp.StockOnHand present.';
GO
