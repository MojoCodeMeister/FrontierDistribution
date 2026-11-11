/*
    Frontier Distribution working model - ERP Business Application
    Increment 2: ERP sales orders and inventory - installer

    sqlcmd mode is required (:r). Run from this folder, against the database
    that already holds the ERP foundation (increment 1):

        sqlcmd -S <server> -d <database> -E -I -b -i install-erp-sales-inventory.sql

    Builds erp.StockOnHand (040), erp.SalesOrder and erp.SalesOrderLine (050),
    then loads the synthetic data (120). Every script is rerunnable.
    Verify afterwards with 910-erp-verify-foundation.sql and
    920-erp-verify-sales-inventory.sql.
*/
:on error exit
:r 040-erp-stock-on-hand.sql
:r 050-erp-sales-orders.sql
:r 120-erp-seed-sales-inventory.sql
