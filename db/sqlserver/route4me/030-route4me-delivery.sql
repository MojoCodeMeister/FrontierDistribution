/*
    Frontier Distribution working model - Route4Me Business Application
    Increment 4: Route4Me shipment, dispatch and delivery
    Script 030: Delivery execution and confirmation (Frontier.Delivery)

    Source records: DispatchedShipment, ShipmentLine, DeliveryEvent in
    src/Frontier.Delivery/Delivery.cs

    DeliveryShipment
      - Frontier.Delivery's own boundary record of a shipment handed over by
        Shipping (DISPATCH handoff, a component handoff inside Route4Me). It keeps
        ShipmentId, SalesOrderId, WarehouseId, products and quantities as received.
      - No foreign key to Shipment: Delivery registers shipments on its own terms,
        as tested, and the handoff stays a component contract. 910 checks that the
        stored handoff preserved the shipment's values (D10).
      - Status is stored as received. Delivery accepts any status at
        registration and requires Dispatched only to record an outcome, so no
        CHECK restricts it; 910 checks outcomes against it (D11).
    DeliveryShipmentLine
      - One line per product, positive quantity, never changed after
        registration. At least one line stays a C# rule, checked by 910 (D09).
    DeliveryEvent
      - EventId is unique: PK_DeliveryEvent.
      - One terminal outcome per shipment: UQ_DeliveryEvent_Shipment. A
        different second outcome is rejected; an identical replay is answered by
        C# without a write.
      - Outcome Delivered with a non-blank ProofReference, or Exception with a
        non-blank ExceptionReason: CK_DeliveryEvent_Outcome. The other field is
        not restricted, as in the code.
      - OccurredAt is required: NOT NULL, and not the .NET default value
        (0001-01-01T00:00:00+00:00), which Delivery rejects.

    D9: identifier columns declare Latin1_General_100_CI_AS, as everywhere else
    in the model. Frontier.Delivery compares ordinally in C#; that difference is
    known and deferred, and no column here is case-sensitive.
    D6 is not modeled: how a ready shipment reaches Route4Me, and how outcomes
    reach ERP later, are open or future. Nothing here records either exchange.
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

IF OBJECT_ID(N'route4me.DeliveryShipment', N'U') IS NULL
CREATE TABLE route4me.DeliveryShipment
(
    ShipmentId    varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    SalesOrderId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId   varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Status        varchar(20) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CONSTRAINT PK_DeliveryShipment PRIMARY KEY CLUSTERED (ShipmentId),
    CONSTRAINT CK_DeliveryShipment_ShipmentId_NotBlank CHECK (LEN(ShipmentId) > 0),
    CONSTRAINT CK_DeliveryShipment_SalesOrderId_NotBlank CHECK (LEN(SalesOrderId) > 0),
    CONSTRAINT CK_DeliveryShipment_WarehouseId_NotBlank CHECK (LEN(WarehouseId) > 0)
);

IF OBJECT_ID(N'route4me.DeliveryShipmentLine', N'U') IS NULL
CREATE TABLE route4me.DeliveryShipmentLine
(
    ShipmentId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId   varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Quantity    int         NOT NULL,
    CONSTRAINT PK_DeliveryShipmentLine PRIMARY KEY CLUSTERED (ShipmentId, ProductId),
    CONSTRAINT FK_DeliveryShipmentLine_DeliveryShipment FOREIGN KEY (ShipmentId)
        REFERENCES route4me.DeliveryShipment (ShipmentId),
    CONSTRAINT CK_DeliveryShipmentLine_ProductId_NotBlank CHECK (LEN(ProductId) > 0),
    CONSTRAINT CK_DeliveryShipmentLine_Quantity_Positive CHECK (Quantity > 0)
);

IF OBJECT_ID(N'route4me.DeliveryEvent', N'U') IS NULL
CREATE TABLE route4me.DeliveryEvent
(
    EventId          varchar(64)       COLLATE Latin1_General_100_CI_AS NOT NULL,
    ShipmentId       varchar(64)       COLLATE Latin1_General_100_CI_AS NOT NULL,
    Outcome          varchar(20)       COLLATE Latin1_General_100_CI_AS NOT NULL,
    OccurredAt       datetimeoffset(7) NOT NULL,
    ProofReference   varchar(200)      COLLATE Latin1_General_100_CI_AS NULL,
    ExceptionReason  nvarchar(500)     NULL,
    CONSTRAINT PK_DeliveryEvent PRIMARY KEY CLUSTERED (EventId),
    CONSTRAINT UQ_DeliveryEvent_Shipment UNIQUE (ShipmentId),
    CONSTRAINT FK_DeliveryEvent_DeliveryShipment FOREIGN KEY (ShipmentId)
        REFERENCES route4me.DeliveryShipment (ShipmentId),
    CONSTRAINT CK_DeliveryEvent_EventId_NotBlank CHECK (LEN(EventId) > 0),
    CONSTRAINT CK_DeliveryEvent_Outcome CHECK
        (   (Outcome = 'Delivered' AND ProofReference IS NOT NULL AND LEN(ProofReference) > 0)
         OR (Outcome = 'Exception' AND ExceptionReason IS NOT NULL AND LEN(ExceptionReason) > 0)),
    CONSTRAINT CK_DeliveryEvent_OccurredAt_Required CHECK
        (OccurredAt <> CAST('0001-01-01T00:00:00+00:00' AS datetimeoffset(7)))
);

PRINT N'030-route4me-delivery: route4me.DeliveryShipment, route4me.DeliveryShipmentLine and route4me.DeliveryEvent present.';
GO
