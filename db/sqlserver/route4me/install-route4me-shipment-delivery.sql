/*
    Frontier Distribution working model - Route4Me Business Application
    Increment 4: Route4Me shipment, dispatch and delivery - installer

    sqlcmd mode is required (:r). Run from this folder, against the database
    chosen for the Route4Me data model (locally: FrontierModel):

        sqlcmd -S <server> -d <database> -E -I -b -i install-route4me-shipment-delivery.sql

    Builds schema route4me and its five tables (010-030), then loads the
    synthetic data (110). Needs nothing from the ERP or Voxware models. Every
    script is rerunnable. Verify afterwards with
    910-route4me-verify-shipment-delivery.sql.
*/
:on error exit
:r 010-route4me-schema.sql
:r 020-route4me-shipment.sql
:r 030-route4me-delivery.sql
:r 110-route4me-seed-shipment-delivery.sql
