/*
    Frontier Distribution working model - Voxware Business Application
    Increment 3: Voxware warehouse fulfillment
    Script 010: Voxware schema

    Voxware is the primary logical owner of Frontier.Picking,
    Frontier.PickExecution and Frontier.Packing. This schema holds the Voxware
    data model only; it creates and references nothing in any other model.
    The local realization (schema voxware in FrontierModel) does not assert
    Frontier's actual database topology. Rerunnable.
    Synthetic model. Not Frontier's production database.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'voxware') IS NULL
    EXEC (N'CREATE SCHEMA voxware AUTHORIZATION dbo;');
GO

PRINT N'010-voxware-schema: schema voxware present.';
GO
