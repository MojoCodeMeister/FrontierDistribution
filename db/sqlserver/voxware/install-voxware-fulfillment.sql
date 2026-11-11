/*
    Frontier Distribution working model - Voxware Business Application
    Increment 3: Voxware warehouse fulfillment - installer

    sqlcmd mode is required (:r). Run from this folder, against the database
    chosen for the Voxware data model (locally: FrontierModel):

        sqlcmd -S <server> -d <database> -E -I -b -i install-voxware-fulfillment.sql

    Builds schema voxware and its six tables (010-040), then loads the synthetic
    data (110). Needs nothing from the ERP model. Every script is rerunnable.
    Verify afterwards with 910-voxware-verify-fulfillment.sql.
*/
:on error exit
:r 010-voxware-schema.sql
:r 020-voxware-pick-plan.sql
:r 030-voxware-pick-confirmation.sql
:r 040-voxware-packing.sql
:r 110-voxware-seed-fulfillment.sql
