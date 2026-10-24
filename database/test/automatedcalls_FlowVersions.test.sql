SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
IF DB_NAME() <> N'NotificationWorkflowVoiceTests'
    THROW 51000, 'Run flow version tests only in an owned scratch database; fixtures are not removed.', 1;
BEGIN TRY
    DECLARE @Family uniqueidentifier = NEWID();
    DECLARE @Published TABLE (FlowID uniqueidentifier, SystemID int, Version int, VersionID int, Active bit);
    DECLARE @Activated TABLE (FlowID uniqueidentifier, SystemID int, Version int, VersionID int);

    INSERT @Published EXEC dbo.AutoCall_PublishFlow @Family, 0, N'{"v":1}', N'<CallFlow v="1"/>', N'test', @Name = 'versions', @Platform = 'P1';
    DECLARE @Template int = (SELECT SystemID FROM @Published WHERE Version = 1), @V1 int = (SELECT VersionID FROM @Published WHERE Version = 1);
    IF @Template IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.CallFlowTemplates WHERE SystemID = @Template AND FlowID = @Family AND Name = 'versions')
        THROW 51000, 'First publication must create the template and version 1.', 1;

    INSERT @Published EXEC dbo.AutoCall_PublishFlow @Family, 1, N'{"v":2}', N'<CallFlow v="2"/>', N'test', @Activate = 0;
    DECLARE @V2 int = (SELECT VersionID FROM @Published WHERE Version = 2);
    IF (SELECT SystemID FROM @Published WHERE Version = 2) <> @Template
        OR NOT EXISTS (SELECT 1 FROM dbo.CallFlowTemplateVersions WHERE SystemID = @V2 AND DeletedDateTime IS NOT NULL)
        OR NOT EXISTS (SELECT 1 FROM dbo.CallFlowTemplateVersions WHERE SystemID = @V1 AND DeletedDateTime IS NULL)
        THROW 51000, 'An inactive publication must keep the current active version.', 1;

    BEGIN TRY
        EXEC dbo.AutoCall_PublishFlow @Family, 1, N'{"v":3}', N'<CallFlow v="3"/>', N'test';
        THROW 51000, 'Expected stale expected-version rejection.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51001 THROW;
    END CATCH;

    -- A job enqueued now pins version 1, even after version 2 is activated.
    DECLARE @Result TABLE (SystemID int);
    INSERT @Result EXEC dbo.activealarms_call_InsertJob 42, 'original-oid', @Template, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = '6f1d0c62-0000-0000-0000-000000000001';
    DECLARE @PinnedJob int = (SELECT SystemID FROM @Result);
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID = @PinnedJob AND FlowId = @Template AND FlowVersionId = @V1)
        THROW 51000, 'Enqueue without a version must pin the active version.', 1;

    INSERT @Activated EXEC dbo.AutoCall_ActivateFlowVersion @Family, 2, N'activator';
    IF NOT EXISTS (SELECT 1 FROM dbo.CallFlowTemplateVersions WHERE SystemID = @V2 AND DeletedDateTime IS NULL AND UpdatedBy = N'activator')
        OR NOT EXISTS (SELECT 1 FROM dbo.CallFlowTemplateVersions WHERE SystemID = @V1 AND DeletedDateTime IS NOT NULL AND DeletedBy = N'activator')
        OR (SELECT COUNT(*) FROM dbo.CallFlowTemplateVersions WHERE CallFlowTemplateID = @Template AND DeletedDateTime IS NULL) <> 1
        THROW 51000, 'Activation must swap the single active version.', 1;

    DECLARE @Catalog TABLE (SystemID int, FlowID uniqueidentifier, Name varchar(200), Version int, POGroupNum varchar(20), [Platform] varchar(20),
        TemplateJSON nvarchar(max), TwiML nvarchar(max), CreatedDateTime datetime2(3), UpdatedDateTime datetime2(3), DeletedDateTime datetime2(3),
        CreatedBy nvarchar(200), UpdatedBy nvarchar(200), DeletedBy nvarchar(200), VersionID int);
    INSERT @Catalog EXEC dbo.automatedcalls_GetAllCallFlowsByPlatform 'P1';
    IF (SELECT COUNT(*) FROM @Catalog) <> 1 OR NOT EXISTS (SELECT 1 FROM @Catalog WHERE SystemID = @Template AND Version = 2 AND VersionID = @V2 AND TemplateJSON = N'{"v":2}')
        THROW 51000, 'The catalog must list the flow once, with its active version.', 1;

    DELETE @Result;
    INSERT @Result EXEC dbo.activealarms_call_InsertJob 43, 'original-oid', @Template, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = '6f1d0c62-0000-0000-0000-000000000002', @FlowVersionId = @V1;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue q JOIN @Result r ON r.SystemID = q.SystemID WHERE q.FlowVersionId = @V1)
        THROW 51000, 'A caller-pinned superseded version must be accepted.', 1;

    INSERT dbo.CallFlowTemplates (Name, CreatedBy) VALUES ('other', N'test');
    DECLARE @Other int = SCOPE_IDENTITY();
    BEGIN TRY
        EXEC dbo.activealarms_call_InsertJob 44, 'original-oid', @Other, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = '6f1d0c62-0000-0000-0000-000000000003', @FlowVersionId = @V2;
        THROW 51000, 'Expected rejection of a version from another template.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 OR ERROR_MESSAGE() <> 'An existing published call flow is required.' THROW;
    END CATCH;

    -- The pinned job starts its attempt with version 1 artifacts.
    UPDATE dbo.AutomatedCallQueue SET Priority = 1000 WHERE SystemID = @PinnedJob;
    DECLARE @Claims TABLE (SystemID int, ActiveAlarmID int, OID varchar(20), FlowId int, VariablesJSON nvarchar(max), EnqueueIdempotencyKey uniqueidentifier,
        AttemptCount int, MaxAttempts int, LeaseToken uniqueidentifier, LockedUntil datetime2(3), CallDeviceIndex int, OptionsJSON nvarchar(max));
    INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1, 'version-worker', 5;
    DECLARE @Lease uniqueidentifier = (SELECT LeaseToken FROM @Claims WHERE SystemID = @PinnedJob);
    IF @Lease IS NULL THROW 51000, 'The pinned job must be claimed first.', 1;
    EXEC dbo.automatedcalls_BeginAttempt @PinnedJob, @Lease, 'version-worker', '+15555550100';
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId = @PinnedJob AND FlowId = @Template AND FlowVersionId = @V1)
        THROW 51000, 'The attempt must keep the pinned version.', 1;

    INSERT @Activated EXEC dbo.AutoCall_ActivateFlowVersion @Family, 1, N'rollback';
    IF NOT EXISTS (SELECT 1 FROM dbo.CallFlowTemplateVersions WHERE SystemID = @V1 AND DeletedDateTime IS NULL)
        OR NOT EXISTS (SELECT 1 FROM dbo.CallFlowTemplateVersions WHERE SystemID = @V2 AND DeletedDateTime IS NOT NULL)
        THROW 51000, 'Rollback activation must restore version 1.', 1;
    BEGIN TRY
        EXEC dbo.AutoCall_ActivateFlowVersion @Family, 9, N'rollback';
        THROW 51000, 'Expected missing version rejection.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51003 THROW;
    END CATCH;

    UPDATE dbo.CallFlowTemplates SET DeletedDateTime = SYSUTCDATETIME(), DeletedBy = N'test' WHERE SystemID = @Template;
    BEGIN TRY
        EXEC dbo.AutoCall_PublishFlow @Family, 2, N'{"v":3}', N'<CallFlow v="3"/>', N'test';
        THROW 51000, 'Expected deleted flow rejection.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51001 THROW;
    END CATCH;

    PRINT 'Flow version checks passed: publish, inactive publish, activation, rollback, pinning, catalog, deleted flow.';
END TRY
BEGIN CATCH
    THROW;
END CATCH;
