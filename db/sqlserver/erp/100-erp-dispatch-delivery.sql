/* Q25.5c — ERP dispatch-intent delivery state. Additive, rerunnable.
   No Route4Me writes; dispatch request identity remains the Q25.5b key. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'erp.DispatchIntent', N'U') IS NULL THROW 50001, N'ERP migration 090 required.', 1;
IF COL_LENGTH('erp.DispatchIntent','AttemptCount') IS NULL
    ALTER TABLE erp.DispatchIntent ADD AttemptCount int NOT NULL CONSTRAINT DF_DispatchIntent_AttemptCount DEFAULT (0);
IF COL_LENGTH('erp.DispatchIntent','NextAttemptUtc') IS NULL
    ALTER TABLE erp.DispatchIntent ADD NextAttemptUtc datetime2(3) NOT NULL CONSTRAINT DF_DispatchIntent_NextAttemptUtc DEFAULT SYSUTCDATETIME();
IF COL_LENGTH('erp.DispatchIntent','LeaseToken') IS NULL
    ALTER TABLE erp.DispatchIntent ADD LeaseToken uniqueidentifier NULL;
IF COL_LENGTH('erp.DispatchIntent','LeaseUntilUtc') IS NULL
    ALTER TABLE erp.DispatchIntent ADD LeaseUntilUtc datetime2(3) NULL;
IF COL_LENGTH('erp.DispatchIntent','LastAttemptUtc') IS NULL
    ALTER TABLE erp.DispatchIntent ADD LastAttemptUtc datetime2(3) NULL;
IF COL_LENGTH('erp.DispatchIntent','LastError') IS NULL
    ALTER TABLE erp.DispatchIntent ADD LastError nvarchar(512) NULL;
IF COL_LENGTH('erp.DispatchIntent','AcceptedUtc') IS NULL
    ALTER TABLE erp.DispatchIntent ADD AcceptedUtc datetime2(3) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'erp.DispatchIntent') AND name=N'CK_DispatchIntent_AttemptCount')
    ALTER TABLE erp.DispatchIntent ADD CONSTRAINT CK_DispatchIntent_AttemptCount CHECK (AttemptCount >= 0);
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'erp.DispatchIntent') AND name=N'CK_DispatchIntent_LeasePair')
    ALTER TABLE erp.DispatchIntent ADD CONSTRAINT CK_DispatchIntent_LeasePair CHECK ((LeaseToken IS NULL AND LeaseUntilUtc IS NULL) OR (LeaseToken IS NOT NULL AND LeaseUntilUtc IS NOT NULL));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'erp.DispatchIntent') AND name=N'IX_DispatchIntent_Eligible')
    CREATE INDEX IX_DispatchIntent_Eligible ON erp.DispatchIntent(Status, NextAttemptUtc, LeaseUntilUtc, CreatedUtc) INCLUDE (DepartureId,AttemptCount);
PRINT N'100-erp-dispatch-delivery: ERP dispatch leasing and recovery fields present.';
GO
