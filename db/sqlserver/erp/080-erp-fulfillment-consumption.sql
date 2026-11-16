/* Q25.4: ERP stock consumption on confirmed departure from warehouse custody.
   Additive/rerunnable. This is ERP state; external departure reference is correlation only. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'erp.StockReservation', N'U') IS NULL OR OBJECT_ID(N'erp.StockOnHand', N'U') IS NULL
    THROW 50001, N'ERP migrations through 070 required.', 1;
IF OBJECT_ID(N'erp.FulfillmentConsumption', N'U') IS NULL
BEGIN
    CREATE TABLE erp.FulfillmentConsumption (
        SalesOrderId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        WarehouseId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        DepartureId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        PackingId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        ConsumedUtc datetime2(3) NOT NULL CONSTRAINT DF_FulfillmentConsumption_ConsumedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_FulfillmentConsumption PRIMARY KEY (SalesOrderId),
        CONSTRAINT UQ_FulfillmentConsumption_DepartureId UNIQUE (DepartureId),
        CONSTRAINT FK_FulfillmentConsumption_OrderCommitment FOREIGN KEY (SalesOrderId) REFERENCES erp.OrderCommitment(SalesOrderId),
        CONSTRAINT CK_FulfillmentConsumption_DepartureId CHECK (LEN(DepartureId) > 0),
        CONSTRAINT CK_FulfillmentConsumption_PackingId CHECK (LEN(PackingId) > 0)
    );
END;
IF OBJECT_ID(N'erp.ConsumedReservationLine', N'U') IS NULL
BEGIN
    CREATE TABLE erp.ConsumedReservationLine (
        SalesOrderId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        WarehouseId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        ProductId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        Quantity int NOT NULL,
        CONSTRAINT PK_ConsumedReservationLine PRIMARY KEY (SalesOrderId, ProductId),
        CONSTRAINT FK_ConsumedReservationLine_Consumption FOREIGN KEY (SalesOrderId) REFERENCES erp.FulfillmentConsumption(SalesOrderId),
        CONSTRAINT CK_ConsumedReservationLine_Quantity CHECK (Quantity > 0)
    );
END;
PRINT N'080-erp-fulfillment-consumption: consumption ledger present.';
GO
