/* Q25.1 ERP order cancellation and stock release. Additive, rerunnable migration. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'erp.OrderCommitment', N'U') IS NULL OR OBJECT_ID(N'erp.StockReservation', N'U') IS NULL
    THROW 50001, N'Install ERP migration 060 first.', 1;
IF OBJECT_ID(N'erp.OrderCancellation', N'U') IS NULL
BEGIN
    CREATE TABLE erp.OrderCancellation (
        SalesOrderId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        WarehouseId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        CancelledUtc datetime2(3) NOT NULL CONSTRAINT DF_OrderCancellation_CancelledUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_OrderCancellation PRIMARY KEY (SalesOrderId),
        CONSTRAINT FK_OrderCancellation_Commitment FOREIGN KEY (SalesOrderId) REFERENCES erp.OrderCommitment (SalesOrderId)
    );
END;
IF OBJECT_ID(N'erp.ReleasedReservationLine', N'U') IS NULL
BEGIN
    CREATE TABLE erp.ReleasedReservationLine (
        SalesOrderId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        ProductId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        WarehouseId varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
        Quantity int NOT NULL,
        CONSTRAINT PK_ReleasedReservationLine PRIMARY KEY (SalesOrderId, ProductId),
        CONSTRAINT FK_ReleasedReservationLine_Cancellation FOREIGN KEY (SalesOrderId) REFERENCES erp.OrderCancellation(SalesOrderId),
        CONSTRAINT CK_ReleasedReservationLine_Quantity CHECK (Quantity > 0)
    );
END;
/* The earlier check constraint permits only Received. Extend it without losing the constraint. */
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID(N'erp.SalesOrder')
                 AND name = N'CK_SalesOrder_Status' AND definition LIKE N'%Cancelled%')
BEGIN
    ALTER TABLE erp.SalesOrder DROP CONSTRAINT CK_SalesOrder_Status;
    ALTER TABLE erp.SalesOrder WITH CHECK ADD CONSTRAINT CK_SalesOrder_Status
        CHECK (Status IN ('Received', 'Cancelled'));
END;
PRINT N'070-erp-order-cancellation: cancellation ledger and status supported.';
GO
