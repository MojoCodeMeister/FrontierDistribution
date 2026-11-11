/*
    Frontier Distribution working model - Route4Me Business Application
    Increment 4: Route4Me shipment, dispatch and delivery
    Script 990: Remove the Route4Me model (for a clean rebuild)

    Drops the five Route4Me tables, their index and their synthetic data, then
    schema route4me if nothing else remains in it. Touches nothing in any other
    model. All-or-nothing: if anything still references these tables, the drop
    fails and nothing is removed.
    Synthetic model only. Never run against a database holding real data.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;
DROP TABLE IF EXISTS route4me.DeliveryEvent;
DROP TABLE IF EXISTS route4me.DeliveryShipmentLine;
DROP TABLE IF EXISTS route4me.DeliveryShipment;
DROP TABLE IF EXISTS route4me.ShipmentLine;
DROP TABLE IF EXISTS route4me.Shipment;
COMMIT TRANSACTION;

IF SCHEMA_ID(N'route4me') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID(N'route4me'))
    EXEC (N'DROP SCHEMA route4me;');

PRINT N'990-route4me-remove-shipment-delivery: Route4Me model removed; other models untouched.';
GO
