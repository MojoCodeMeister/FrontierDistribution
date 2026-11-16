/* Frontier working model — ERP-owned transactional inventory commitment.
   Additive migration; does not alter existing sales order intake or historical records.
   Existing Received orders require an explicit acceptance operation. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'erp.SalesOrder', N'U') IS NULL OR OBJECT_ID(N'erp.StockOnHand', N'U') IS NULL
    THROW 50001, N'ERP sales/inventory foundation must be installed first.', 1;
IF OBJECT_ID(N'erp.OrderCommitment', N'U') IS NULL
CREATE TABLE erp.OrderCommitment (
    SalesOrderId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CommittedUtc datetime2(3) NOT NULL CONSTRAINT DF_OrderCommitment_CommittedUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_OrderCommitment PRIMARY KEY (SalesOrderId),
    CONSTRAINT FK_OrderCommitment_Order FOREIGN KEY (SalesOrderId) REFERENCES erp.SalesOrder(SalesOrderId),
    CONSTRAINT FK_OrderCommitment_Warehouse FOREIGN KEY (WarehouseId) REFERENCES erp.Warehouse(WarehouseId)
);
IF OBJECT_ID(N'erp.StockReservation', N'U') IS NULL
CREATE TABLE erp.StockReservation (
    SalesOrderId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Quantity int NOT NULL,
    CONSTRAINT PK_StockReservation PRIMARY KEY (SalesOrderId, ProductId),
    CONSTRAINT FK_StockReservation_Commitment FOREIGN KEY (SalesOrderId) REFERENCES erp.OrderCommitment(SalesOrderId),
    CONSTRAINT FK_StockReservation_Position FOREIGN KEY (WarehouseId, ProductId)
        REFERENCES erp.InventoryPosition(WarehouseId, ProductId),
    CONSTRAINT CK_StockReservation_Quantity CHECK (Quantity > 0)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'erp.StockReservation') AND name = N'IX_StockReservation_Position')
    CREATE INDEX IX_StockReservation_Position ON erp.StockReservation(WarehouseId, ProductId) INCLUDE(Quantity);
PRINT N'060-erp-inventory-commitment: ERP commitment tables present.';
GO
