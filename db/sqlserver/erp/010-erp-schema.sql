/*
    Frontier Distribution working model — ERP Business Application
    Increment 1: ERP foundation
    Script 010: ERP schema

    Synthetic model. Not Frontier's production database.
    Run in whichever database hosts the ERP data model (placement is open:
    one shared database, an ERP-only database, or a separate instance).
    Rerunnable.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'erp') IS NULL
    EXEC (N'CREATE SCHEMA erp AUTHORIZATION dbo;');
GO

PRINT N'010-erp-schema: schema erp present.';
GO
