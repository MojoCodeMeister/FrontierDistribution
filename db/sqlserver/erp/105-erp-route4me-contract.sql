/* Q25.5d2 — ERP-owned delivery address and remote dispatch correlation.
   Additive, rerunnable. No Route4Me-schema writes. Run after 100. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'erp.CustomerLocation', N'U') IS NULL OR OBJECT_ID(N'erp.DispatchIntent', N'U') IS NULL
    THROW 50001, N'ERP master data and dispatch delivery migrations required.', 1;
IF COL_LENGTH('erp.CustomerLocation','Address1') IS NULL
    ALTER TABLE erp.CustomerLocation ADD Address1 nvarchar(200) NULL;
IF COL_LENGTH('erp.CustomerLocation','Address2') IS NULL
    ALTER TABLE erp.CustomerLocation ADD Address2 nvarchar(200) NULL;
IF COL_LENGTH('erp.CustomerLocation','City') IS NULL
    ALTER TABLE erp.CustomerLocation ADD City nvarchar(100) NULL;
IF COL_LENGTH('erp.CustomerLocation','Region') IS NULL
    ALTER TABLE erp.CustomerLocation ADD Region nvarchar(100) NULL;
IF COL_LENGTH('erp.CustomerLocation','PostalCode') IS NULL
    ALTER TABLE erp.CustomerLocation ADD PostalCode nvarchar(32) NULL;
IF COL_LENGTH('erp.CustomerLocation','CountryCode') IS NULL
    ALTER TABLE erp.CustomerLocation ADD CountryCode char(2) NULL;
IF COL_LENGTH('erp.CustomerLocation','ContactName') IS NULL
    ALTER TABLE erp.CustomerLocation ADD ContactName nvarchar(200) NULL;
IF COL_LENGTH('erp.CustomerLocation','ContactPhone') IS NULL
    ALTER TABLE erp.CustomerLocation ADD ContactPhone nvarchar(50) NULL;
IF COL_LENGTH('erp.DispatchIntent','RemoteSubmissionAttemptedUtc') IS NULL
    ALTER TABLE erp.DispatchIntent ADD RemoteSubmissionAttemptedUtc datetime2(3) NULL;
IF COL_LENGTH('erp.DispatchIntent','RemoteShipmentId') IS NULL
    ALTER TABLE erp.DispatchIntent ADD RemoteShipmentId nvarchar(200) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'erp.DispatchIntent') AND name=N'CK_DispatchIntent_RemoteShipmentId')
    ALTER TABLE erp.DispatchIntent ADD CONSTRAINT CK_DispatchIntent_RemoteShipmentId CHECK (RemoteShipmentId IS NULL OR LEN(LTRIM(RTRIM(RemoteShipmentId))) > 0);
PRINT N'105-erp-route4me-contract: ERP destination and remote correlation columns present.';
GO
