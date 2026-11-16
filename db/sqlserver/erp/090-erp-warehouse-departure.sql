/* Q25.5b: ERP-owned physical-departure record and durable transportation dispatch intent.
   Additive and rerunnable. No Route4Me cross-schema foreign keys or external calls. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'erp.FulfillmentConsumption', N'U') IS NULL
    THROW 50001, N'ERP migration 080 required.', 1;
IF OBJECT_ID(N'erp.WarehouseDeparture', N'U') IS NULL
BEGIN
    CREATE TABLE erp.WarehouseDeparture (
        DepartureId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        SalesOrderId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        WarehouseId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        PackingId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        AttestedBy varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        RecordedUtc datetime2(3) NOT NULL CONSTRAINT DF_WarehouseDeparture_RecordedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WarehouseDeparture PRIMARY KEY (DepartureId),
        CONSTRAINT UQ_WarehouseDeparture_Order UNIQUE (SalesOrderId),
        CONSTRAINT FK_WarehouseDeparture_Consumption FOREIGN KEY (SalesOrderId)
            REFERENCES erp.FulfillmentConsumption(SalesOrderId),
        CONSTRAINT CK_WarehouseDeparture_Actor CHECK (LEN(AttestedBy) > 0)
    );
END;
IF OBJECT_ID(N'erp.DispatchIntent', N'U') IS NULL
BEGIN
    CREATE TABLE erp.DispatchIntent (
        DispatchRequestId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        DepartureId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        Status varchar(16) NOT NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_DispatchIntent_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_DispatchIntent PRIMARY KEY (DispatchRequestId),
        CONSTRAINT UQ_DispatchIntent_Departure UNIQUE (DepartureId),
        CONSTRAINT FK_DispatchIntent_Departure FOREIGN KEY (DepartureId)
            REFERENCES erp.WarehouseDeparture(DepartureId),
        CONSTRAINT CK_DispatchIntent_Status CHECK (Status IN ('Pending','Accepted','Failed'))
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'erp.DispatchIntent') AND name=N'IX_DispatchIntent_Status')
    CREATE INDEX IX_DispatchIntent_Status ON erp.DispatchIntent(Status, CreatedUtc);
PRINT N'090-erp-warehouse-departure: ERP departure and dispatch intent tables present.';
GO
