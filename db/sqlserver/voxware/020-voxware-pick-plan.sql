/*
    Frontier Distribution working model - Voxware Business Application
    Increment 3: Voxware warehouse fulfillment
    Script 020: Pick plans (Frontier.Picking)

    Source records: PickPlan, PickLine in src/Frontier.Orders/PickPlanning.cs

    PickPlan
      - Identified by (SalesOrderId, WarehouseId), the correlation every later
        Voxware record uses. Both are ERP identifiers carried as plain values:
        no foreign key to the ERP model.
      - Status Ready, BlockedShort or BlockedUnknown, derived in C# from ERP's
        availability assessment. The assessment itself is not stored.
    PickPlanLine
      - One line per product: order demand aggregated by product, so the
        product is part of the key. ProductId is an ERP identifier.
      - Blocked plans carry no lines and Ready plans at least one; both stay C#
        rules, checked on stored data by 910 (D08).

    Identifier and status columns declare Latin1_General_100_CI_AS (D9).
    Requires 010-voxware-schema.sql. Rerunnable.
    Synthetic model. Not Frontier's production database.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF SCHEMA_ID(N'voxware') IS NULL
    THROW 50001, N'Schema voxware is missing. Run 010-voxware-schema.sql first.', 1;

IF OBJECT_ID(N'voxware.PickPlan', N'U') IS NULL
CREATE TABLE voxware.PickPlan
(
    SalesOrderId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId   varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Status        varchar(20) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CONSTRAINT PK_PickPlan PRIMARY KEY CLUSTERED (SalesOrderId, WarehouseId),
    CONSTRAINT CK_PickPlan_SalesOrderId_NotBlank CHECK (LEN(SalesOrderId) > 0),
    CONSTRAINT CK_PickPlan_WarehouseId_NotBlank CHECK (LEN(WarehouseId) > 0),
    CONSTRAINT CK_PickPlan_Status CHECK (Status IN ('Ready', 'BlockedShort', 'BlockedUnknown'))
);

IF OBJECT_ID(N'voxware.PickPlanLine', N'U') IS NULL
CREATE TABLE voxware.PickPlanLine
(
    SalesOrderId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId   varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId     varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Quantity      int         NOT NULL,
    CONSTRAINT PK_PickPlanLine PRIMARY KEY CLUSTERED (SalesOrderId, WarehouseId, ProductId),
    CONSTRAINT FK_PickPlanLine_PickPlan FOREIGN KEY (SalesOrderId, WarehouseId)
        REFERENCES voxware.PickPlan (SalesOrderId, WarehouseId),
    CONSTRAINT CK_PickPlanLine_ProductId_NotBlank CHECK (LEN(ProductId) > 0),
    CONSTRAINT CK_PickPlanLine_Quantity_Positive CHECK (Quantity > 0)
);

PRINT N'020-voxware-pick-plan: voxware.PickPlan and voxware.PickPlanLine present.';
GO
