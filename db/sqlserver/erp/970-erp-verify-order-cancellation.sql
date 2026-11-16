SET NOCOUNT ON;
IF OBJECT_ID(N'erp.OrderCancellation',N'U') IS NULL
 OR OBJECT_ID(N'erp.ReleasedReservationLine',N'U') IS NULL
 OR NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'erp.SalesOrder') AND name=N'CK_SalesOrder_Status' AND definition LIKE N'%Cancelled%')
    THROW 50025, N'Q25.1 migration is incomplete', 1;
PRINT N'PASS Q25.1 cancellation structure';
GO
