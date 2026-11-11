/*
    Frontier Distribution working model - Route4Me Business Application
    Increment 4: Route4Me shipment, dispatch and delivery
    Script 020: Shipments and dispatch (Frontier.Shipping)

    Source records: ShipmentRecord, ShipmentLine, DispatchConfirmation in
    src/Frontier.Orders/Shipping.cs

    Shipment
      - ShipmentId identifies the shipment and is never reused, for any order.
      - Exactly one shipment per (SalesOrderId, WarehouseId): UQ_Shipment_Order.
        A second, different shipment is rejected; an identical replay is answered
        by C# without a write.
      - SalesOrderId, WarehouseId (ERP) and PackingId, PickConfirmationId
        (Voxware) are references carried as plain values, as received from the
        ReadyForShipment packing result. No foreign key leaves the Route4Me model.
        That the packing was ReadyForShipment is checked by Shipping in C#; it
        crosses the Voxware boundary and is not enforced here.
      - Dispatch is recorded on the shipment, as the code records it: Status moves
        from Prepared to Dispatched and DispatchId is set. CK_Shipment_Status keeps
        the two consistent: Prepared has no DispatchId, Dispatched has a non-blank
        one. One dispatch per shipment follows from the single column.
      - DispatchId is never reused across shipments: filtered unique index
        UX_Shipment_DispatchId over the non-null values.
    ShipmentLine
      - One line per product, positive quantity, copied from the packed lines.
        At least one line per shipment stays a C# rule, checked by 910 (D09).

    Identifier and status columns declare Latin1_General_100_CI_AS (D9).
    Requires 010-route4me-schema.sql. Rerunnable.
    Synthetic model. Not Frontier's production database.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF SCHEMA_ID(N'route4me') IS NULL
    THROW 50001, N'Schema route4me is missing. Run 010-route4me-schema.sql first.', 1;

IF OBJECT_ID(N'route4me.Shipment', N'U') IS NULL
CREATE TABLE route4me.Shipment
(
    ShipmentId          varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    SalesOrderId        varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId         varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    PackingId           varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    PickConfirmationId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Status              varchar(20) COLLATE Latin1_General_100_CI_AS NOT NULL,
    DispatchId          varchar(64) COLLATE Latin1_General_100_CI_AS NULL,
    CONSTRAINT PK_Shipment PRIMARY KEY CLUSTERED (ShipmentId),
    CONSTRAINT UQ_Shipment_Order UNIQUE (SalesOrderId, WarehouseId),
    CONSTRAINT CK_Shipment_ShipmentId_NotBlank CHECK (LEN(ShipmentId) > 0),
    CONSTRAINT CK_Shipment_SalesOrderId_NotBlank CHECK (LEN(SalesOrderId) > 0),
    CONSTRAINT CK_Shipment_WarehouseId_NotBlank CHECK (LEN(WarehouseId) > 0),
    CONSTRAINT CK_Shipment_PackingId_NotBlank CHECK (LEN(PackingId) > 0),
    CONSTRAINT CK_Shipment_PickConfirmationId_NotBlank CHECK (LEN(PickConfirmationId) > 0),
    CONSTRAINT CK_Shipment_Status CHECK
        (   (Status = 'Prepared'   AND DispatchId IS NULL)
         OR (Status = 'Dispatched' AND DispatchId IS NOT NULL AND LEN(DispatchId) > 0))
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'route4me.Shipment')
                 AND name COLLATE DATABASE_DEFAULT = N'UX_Shipment_DispatchId' COLLATE DATABASE_DEFAULT)
    CREATE UNIQUE INDEX UX_Shipment_DispatchId ON route4me.Shipment (DispatchId) WHERE DispatchId IS NOT NULL;

IF OBJECT_ID(N'route4me.ShipmentLine', N'U') IS NULL
CREATE TABLE route4me.ShipmentLine
(
    ShipmentId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId   varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Quantity    int         NOT NULL,
    CONSTRAINT PK_ShipmentLine PRIMARY KEY CLUSTERED (ShipmentId, ProductId),
    CONSTRAINT FK_ShipmentLine_Shipment FOREIGN KEY (ShipmentId)
        REFERENCES route4me.Shipment (ShipmentId),
    CONSTRAINT CK_ShipmentLine_ProductId_NotBlank CHECK (LEN(ProductId) > 0),
    CONSTRAINT CK_ShipmentLine_Quantity_Positive CHECK (Quantity > 0)
);

PRINT N'020-route4me-shipment: route4me.Shipment and route4me.ShipmentLine present.';
GO
