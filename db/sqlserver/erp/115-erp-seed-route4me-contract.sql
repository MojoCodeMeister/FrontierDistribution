/* Q25.5d2 synthetic address supplement; run after 105 and foundation seed 110. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF COL_LENGTH('erp.CustomerLocation','Address1') IS NULL
    THROW 50001, N'105-erp-route4me-contract.sql required.', 1;
/* Q25.5d2: test-only routable addresses for the established synthetic locations.
   Requires 105-erp-route4me-contract.sql; never backfill actual customer addresses. */
BEGIN
    UPDATE erp.CustomerLocation SET
        Address1 = N'100 Example Drive', City = N'Oxford', Region = N'MI',
        PostalCode = N'48371', CountryCode = 'US', ContactName = N'Synthetic receiver'
    WHERE LocationId = 'LOC-01' AND Address1 IS NULL;
    UPDATE erp.CustomerLocation SET
        Address1 = N'200 Example Drive', City = N'Oxford', Region = N'MI',
        PostalCode = N'48371', CountryCode = 'US', ContactName = N'Synthetic receiver'
    WHERE LocationId = 'LOC-02' AND Address1 IS NULL;
END;

PRINT N'115-erp-seed-route4me-contract: synthetic addresses populated.';
GO
