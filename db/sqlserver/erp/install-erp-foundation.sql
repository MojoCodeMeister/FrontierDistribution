/*
    Frontier Distribution working model — ERP Business Application
    Increment 1: ERP foundation — installer

    sqlcmd mode is required (:r). Run from this folder, against the database
    chosen for the ERP data model:

        sqlcmd -S <server> -d <database> -E -I -b -i install-erp-foundation.sql

    Builds the schema and tables (010–030), then loads the synthetic data (110).
    Every script is rerunnable. Verify afterwards with 910-erp-verify-foundation.sql.
*/
:on error exit
:r 010-erp-schema.sql
:r 020-erp-master-data.sql
:r 030-erp-product-identity.sql
:r 110-erp-seed-foundation.sql
