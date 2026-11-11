/*
    Frontier Distribution working model - ERP Business Application
    Increment 2: ERP sales orders and inventory
    Script 050: Sales orders

    Source records: SalesOrder, SalesOrderLine, IncomingOrder in src/Frontier.Orders/OrderIntake.cs
    Owner:          ERP (Frontier.Orders; approved 2026-11-10)

    SalesOrder
      - SalesOrderId is the ERP order identifier (SO-0001). OrderIntake assigns it
        after every check passes; the database stores it and generates nothing.
      - (SourceSystem, ExternalOrderId) is the source idempotency key, compared
        case-insensitively. It keeps the nopCommerce order number (WEB-10001).
      - The ship-to location must belong to the order's customer: composite foreign
        key to UQ_CustomerLocation_Location_Customer (increment 1).
      - Status: 'Received' is the only implemented value.

    SalesOrderLine
      - One row per accepted source line; key (SalesOrderId, SourceLineId).
      - SourceSku and LineSequence (decision D7) keep the submitted SKU and line
        order, which OrderIntake compares when a duplicate submission arrives.
      - ProductId is the product the SKU resolved to. Several lines may share a product.
      - At least one line per order, and SKU-to-product resolution, stay C# rules.

    Identifier and status columns declare Latin1_General_100_CI_AS (decision D9).
    Requires increment 1 (ERP foundation). Rerunnable.
    Synthetic model. Not Frontier's production database.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'erp.Product', N'U') IS NULL
   OR OBJECT_ID(N'erp.CustomerLocation', N'U') IS NULL
   OR NOT EXISTS (SELECT 1 FROM sys.key_constraints
                  WHERE parent_object_id = OBJECT_ID(N'erp.CustomerLocation')
                    AND name = N'UQ_CustomerLocation_Location_Customer')
    THROW 50001, N'ERP foundation is missing. Install increment 1 (install-erp-foundation.sql) first.', 1;

/* SalesOrder ------------------------------------------------------------- */
IF OBJECT_ID(N'erp.SalesOrder', N'U') IS NULL
CREATE TABLE erp.SalesOrder
(
    SalesOrderId      varchar(64)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    SourceSystem      varchar(80)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    ExternalOrderId   varchar(100) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CustomerId        varchar(64)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    ShipToLocationId  varchar(64)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    Status            varchar(20)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    CONSTRAINT PK_SalesOrder PRIMARY KEY CLUSTERED (SalesOrderId),
    CONSTRAINT UQ_SalesOrder_Source UNIQUE (SourceSystem, ExternalOrderId),
    CONSTRAINT FK_SalesOrder_Customer FOREIGN KEY (CustomerId)
        REFERENCES erp.Customer (CustomerId),
    CONSTRAINT FK_SalesOrder_ShipTo FOREIGN KEY (ShipToLocationId, CustomerId)
        REFERENCES erp.CustomerLocation (LocationId, CustomerId),
    CONSTRAINT CK_SalesOrder_SalesOrderId_NotBlank CHECK (LEN(SalesOrderId) > 0),
    CONSTRAINT CK_SalesOrder_SourceSystem_NotBlank CHECK (LEN(SourceSystem) > 0),
    CONSTRAINT CK_SalesOrder_ExternalOrderId_NotBlank CHECK (LEN(ExternalOrderId) > 0),
    CONSTRAINT CK_SalesOrder_Status CHECK (Status IN ('Received'))
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'erp.SalesOrder') AND name = N'IX_SalesOrder_CustomerId')
    CREATE INDEX IX_SalesOrder_CustomerId ON erp.SalesOrder (CustomerId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'erp.SalesOrder') AND name = N'IX_SalesOrder_ShipTo')
    CREATE INDEX IX_SalesOrder_ShipTo ON erp.SalesOrder (ShipToLocationId, CustomerId);

/* SalesOrderLine --------------------------------------------------------- */
IF OBJECT_ID(N'erp.SalesOrderLine', N'U') IS NULL
CREATE TABLE erp.SalesOrderLine
(
    SalesOrderId  varchar(64)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    SourceLineId  varchar(64)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    LineSequence  int          NOT NULL,
    SourceSku     varchar(150) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId     varchar(64)  COLLATE Latin1_General_100_CI_AS NOT NULL,
    Quantity      int          NOT NULL,
    CONSTRAINT PK_SalesOrderLine PRIMARY KEY CLUSTERED (SalesOrderId, SourceLineId),
    CONSTRAINT UQ_SalesOrderLine_Sequence UNIQUE (SalesOrderId, LineSequence),
    CONSTRAINT FK_SalesOrderLine_SalesOrder FOREIGN KEY (SalesOrderId)
        REFERENCES erp.SalesOrder (SalesOrderId),
    CONSTRAINT FK_SalesOrderLine_Product FOREIGN KEY (ProductId)
        REFERENCES erp.Product (ProductId),
    CONSTRAINT CK_SalesOrderLine_SourceLineId_NotBlank CHECK (LEN(SourceLineId) > 0),
    CONSTRAINT CK_SalesOrderLine_SourceSku_NotBlank CHECK (LEN(SourceSku) > 0),
    CONSTRAINT CK_SalesOrderLine_LineSequence_Positive CHECK (LineSequence >= 1),
    CONSTRAINT CK_SalesOrderLine_Quantity_Positive CHECK (Quantity > 0)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'erp.SalesOrderLine') AND name = N'IX_SalesOrderLine_ProductId')
    CREATE INDEX IX_SalesOrderLine_ProductId ON erp.SalesOrderLine (ProductId);

PRINT N'050-erp-sales-orders: erp.SalesOrder and erp.SalesOrderLine present.';
GO
