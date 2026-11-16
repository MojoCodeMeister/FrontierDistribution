/* Structural smoke checks only; NOT a substitute for C# SQL concurrency regression. */
SET NOCOUNT ON;
IF OBJECT_ID(N'erp.OrderCommitment',N'U') IS NULL THROW 51001, N'OrderCommitment missing', 1;
IF OBJECT_ID(N'erp.StockReservation',N'U') IS NULL THROW 51002, N'StockReservation missing', 1;
IF EXISTS (
    SELECT 1 FROM erp.StockReservation r
    LEFT JOIN erp.OrderCommitment c ON c.SalesOrderId=r.SalesOrderId
    WHERE c.SalesOrderId IS NULL OR c.WarehouseId<>r.WarehouseId
) THROW 51003, N'Reservation and commitment warehouse mismatch', 1;
IF EXISTS (
    SELECT 1 FROM erp.StockReservation r
    GROUP BY r.WarehouseId,r.ProductId
    HAVING SUM(CONVERT(bigint,r.Quantity)) >
       (SELECT s.OnHand FROM erp.StockOnHand s WHERE s.WarehouseId=r.WarehouseId AND s.ProductId=r.ProductId)
) THROW 51004, N'Reservations exceed current on-hand', 1;
PRINT N'ERP commitment structural checks PASS';
