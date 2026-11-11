/*
    Frontier Distribution working model - Voxware Business Application
    Increment 3: Voxware warehouse fulfillment
    Script 030: Pick confirmations (Frontier.PickExecution)

    Source records: PickConfirmation, PickedLine, PickExecutionResult in
    src/Frontier.Orders/PickExecution.cs

    PickConfirmation
      - One per pick plan, keyed by (SalesOrderId, WarehouseId) exactly as
        PickExecution records it. A second, different confirmation for the same
        plan is rejected; an identical replay is answered by C# without a write.
      - ConfirmationId is a required reference, not a key: two plans may carry the
        same value, as the code allows. UQ_PickConfirmation_Reference contains
        the primary key, so it adds no rule; it is the target of the packing
        foreign key that ties a packing to its plan's own confirmation.
      - Outcome Completed or Partial, the recorded PickExecutionResult status.
    PickConfirmationLine
      - Exactly one line per planned product (C# rule); a product must be on the
        plan (foreign key to PickPlanLine). Picked quantity 0 or more.
      - Picked quantity at most the planned quantity, coverage of every planned
        product and outcome consistency stay C# rules, checked on stored data by
        910 (D09, D10).

    Identifier and outcome columns declare Latin1_General_100_CI_AS (D9).
    Requires 020-voxware-pick-plan.sql. Rerunnable.
    Synthetic model. Not Frontier's production database.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'voxware.PickPlanLine', N'U') IS NULL
    THROW 50001, N'Pick plan tables are missing. Run 020-voxware-pick-plan.sql first.', 1;

IF OBJECT_ID(N'voxware.PickConfirmation', N'U') IS NULL
CREATE TABLE voxware.PickConfirmation
(
    SalesOrderId    varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId     varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ConfirmationId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Outcome         varchar(20) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CONSTRAINT PK_PickConfirmation PRIMARY KEY CLUSTERED (SalesOrderId, WarehouseId),
    CONSTRAINT UQ_PickConfirmation_Reference UNIQUE (SalesOrderId, WarehouseId, ConfirmationId),
    CONSTRAINT FK_PickConfirmation_PickPlan FOREIGN KEY (SalesOrderId, WarehouseId)
        REFERENCES voxware.PickPlan (SalesOrderId, WarehouseId),
    CONSTRAINT CK_PickConfirmation_ConfirmationId_NotBlank CHECK (LEN(ConfirmationId) > 0),
    CONSTRAINT CK_PickConfirmation_Outcome CHECK (Outcome IN ('Completed', 'Partial'))
);

IF OBJECT_ID(N'voxware.PickConfirmationLine', N'U') IS NULL
CREATE TABLE voxware.PickConfirmationLine
(
    SalesOrderId  varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    WarehouseId   varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    ProductId     varchar(64) COLLATE Latin1_General_100_CI_AS NOT NULL,
    Quantity      int         NOT NULL,
    CONSTRAINT PK_PickConfirmationLine PRIMARY KEY CLUSTERED (SalesOrderId, WarehouseId, ProductId),
    CONSTRAINT FK_PickConfirmationLine_PickConfirmation FOREIGN KEY (SalesOrderId, WarehouseId)
        REFERENCES voxware.PickConfirmation (SalesOrderId, WarehouseId),
    CONSTRAINT FK_PickConfirmationLine_PickPlanLine FOREIGN KEY (SalesOrderId, WarehouseId, ProductId)
        REFERENCES voxware.PickPlanLine (SalesOrderId, WarehouseId, ProductId),
    CONSTRAINT CK_PickConfirmationLine_Quantity_NonNegative CHECK (Quantity >= 0)
);

PRINT N'030-voxware-pick-confirmation: voxware.PickConfirmation and voxware.PickConfirmationLine present.';
GO
