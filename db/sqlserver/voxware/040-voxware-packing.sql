/*
    Frontier Distribution working model - Voxware Business Application
    Increment 3: Voxware warehouse fulfillment
    Script 040: Packing confirmations (Frontier.Packing)

    Source records: PackingConfirmation, PackedLine, PackingResult in
    src/Frontier.Orders/Packing.cs

    PackingConfirmation
      - One per pick plan, keyed by (SalesOrderId, WarehouseId) exactly as
        Packing records it.
      - PickConfirmationId must be the ConfirmationId of the same plan's pick
        confirmation: composite foreign key to UQ_PickConfirmation_Reference.
      - PackingId is a required reference, not a key: two plans may carry the
        same value, as the code allows.
      - Outcome ReadyForShipment or Incomplete, the recorded PackingResult status.
        ReadyForShipment requires a completed pick and every packed quantity equal
        to the planned quantity: a C# rule, checked on stored data by 910 (D12).
    PackingConfirmationLine
      - Exactly one line per planned product (C# rule); a product must have a
        picked line (foreign key to PickConfirmationLine). Packed quantity 0 or more.
      - Packed quantity at most the picked quantity stays a C# rule, checked on
        stored data by 910 (D11).

    A ReadyForShipment packing supplies the existing shipment contract
    (SalesOrderId, WarehouseId, PackingId, PickConfirmationId, product lines).
    How that reaches Route4Me, directly or through ERP, is open (D6); nothing
    here models the exchange.

    Identifier and outcome columns declare Latin1_General_100_CI_AS (D9).
    Requires 030-voxware-pick-confirmation.sql. Rerunnable.
    Synthetic model. Not Frontier's production database.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'voxware.PickConfirmationLine', N'U') IS NULL
    THROW 50001, N'Pick confirmation tables are missing. Run 030-voxware-pick-confirmation.sql first.', 1;

IF OBJECT_ID(N'voxware.PackingConfirmation', N'U') IS NULL
CREATE TABLE voxware.PackingConfirmation
(
    SalesOrderId        varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId         varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    PackingId           varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    PickConfirmationId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Outcome             varchar(20) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CONSTRAINT PK_PackingConfirmation PRIMARY KEY CLUSTERED (SalesOrderId, WarehouseId),
    CONSTRAINT FK_PackingConfirmation_PickConfirmation FOREIGN KEY (SalesOrderId, WarehouseId, PickConfirmationId)
        REFERENCES voxware.PickConfirmation (SalesOrderId, WarehouseId, ConfirmationId),
    CONSTRAINT CK_PackingConfirmation_PackingId_NotBlank CHECK (LEN(PackingId) > 0),
    CONSTRAINT CK_PackingConfirmation_Outcome CHECK (Outcome IN ('ReadyForShipment', 'Incomplete'))
);

IF OBJECT_ID(N'voxware.PackingConfirmationLine', N'U') IS NULL
CREATE TABLE voxware.PackingConfirmationLine
(
    SalesOrderId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId   varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId     varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Quantity      int         NOT NULL,
    CONSTRAINT PK_PackingConfirmationLine PRIMARY KEY CLUSTERED (SalesOrderId, WarehouseId, ProductId),
    CONSTRAINT FK_PackingConfirmationLine_PackingConfirmation FOREIGN KEY (SalesOrderId, WarehouseId)
        REFERENCES voxware.PackingConfirmation (SalesOrderId, WarehouseId),
    CONSTRAINT FK_PackingConfirmationLine_PickConfirmationLine FOREIGN KEY (SalesOrderId, WarehouseId, ProductId)
        REFERENCES voxware.PickConfirmationLine (SalesOrderId, WarehouseId, ProductId),
    CONSTRAINT CK_PackingConfirmationLine_Quantity_NonNegative CHECK (Quantity >= 0)
);

PRINT N'040-voxware-packing: voxware.PackingConfirmation and voxware.PackingConfirmationLine present.';
GO
