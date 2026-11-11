/*
    Frontier Distribution working model - Voxware Business Application
    Increment 3: Voxware warehouse fulfillment
    Script 990: Remove the Voxware model (for a clean rebuild)

    Drops the six Voxware tables and their synthetic data, then schema voxware if
    nothing else remains in it. Touches nothing in any other model.
    All-or-nothing: if anything still references these tables, the drop fails and
    nothing is removed.
    Synthetic model only. Never run against a database holding real data.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;
DROP TABLE IF EXISTS voxware.PackingConfirmationLine;
DROP TABLE IF EXISTS voxware.PackingConfirmation;
DROP TABLE IF EXISTS voxware.PickConfirmationLine;
DROP TABLE IF EXISTS voxware.PickConfirmation;
DROP TABLE IF EXISTS voxware.PickPlanLine;
DROP TABLE IF EXISTS voxware.PickPlan;
COMMIT TRANSACTION;

IF SCHEMA_ID(N'voxware') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID(N'voxware'))
    EXEC (N'DROP SCHEMA voxware;');

PRINT N'990-voxware-remove-fulfillment: Voxware model removed; other models untouched.';
GO
