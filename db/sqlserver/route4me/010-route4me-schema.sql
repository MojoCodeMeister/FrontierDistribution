/*
    Frontier Distribution working model - Route4Me Business Application
    Increment 4: Route4Me shipment, dispatch and delivery
    Script 010: Route4Me schema

    Route4Me is the primary logical owner of Frontier.Shipping and
    Frontier.Delivery. This schema holds the Route4Me data model only; it creates
    and references nothing in any other model. Frontier.Shipping's placement in
    the Frontier.Orders assembly does not make its records ERP's.
    The local realization (schema route4me in FrontierModel) does not assert
    Frontier's actual database topology. Rerunnable.
    Synthetic model. Not Frontier's production database.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'route4me') IS NULL
    EXEC (N'CREATE SCHEMA route4me AUTHORIZATION dbo;');
GO

PRINT N'010-route4me-schema: schema route4me present.';
GO
