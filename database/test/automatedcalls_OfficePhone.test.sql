SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
IF DB_NAME() <> N'NotificationWorkflowVoiceTests'
    THROW 51000, 'Run officer phone tests only in the owned scratch database; fixtures are not removed.', 1;

INSERT dbo.CallFlowTemplates(Name, CreatedBy) VALUES('office-phone-test', N'test');
DECLARE @Flow int = SCOPE_IDENTITY();
INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID, Version, TemplateJSON, TwiML, CreatedBy)
VALUES(@Flow, 1, N'{}', N'<CallFlow/>', N'test');
DECLARE @Version int = SCOPE_IDENTITY(), @Case int = 0;
DECLARE @Jobs TABLE(SystemID int);
DECLARE @Claims TABLE(SystemID int, ActiveAlarmID int, OID varchar(20), FlowId int, VariablesJSON nvarchar(max),
    EnqueueIdempotencyKey uniqueidentifier, AttemptCount int, MaxAttempts int, LeaseToken uniqueidentifier,
    LockedUntil datetime2(3), CallDeviceIndex int, OptionsJSON nvarchar(max));
WHILE @Case < 2
BEGIN
    DECLARE @Primary varchar(20) = CASE WHEN @Case = 0 THEN 'OfficePhone' ELSE 'CellPhone' END,
        @Secondary varchar(20) = CASE WHEN @Case = 0 THEN 'CellPhone' ELSE 'OfficePhone' END;
    DECLARE @Options nvarchar(max) = N'{"historyId":81,"postRecordingTranscript":true,"waitForCompletion":true,
        "callDevices":[{"type":"' + @Primary + N'","phoneNumber":null},{"type":"' + @Secondary + N'","phoneNumber":null}],
        "officer":{"selection":{"type":"Primary","roleType":null,"roleId":null,"rolePOGroup":null},"id":91,"name":"Officer"}}';
    DECLARE @Key uniqueidentifier = NEWID();
    DELETE @Jobs;
    INSERT @Jobs EXEC dbo.activealarms_call_InsertJob 42, 'original-oid', @Flow,
        @VariablesJSON=N'{}', @EnqueueIdempotencyKey=@Key, @OptionsJSON=@Options, @FlowVersionId=@Version;
    DECLARE @Root int = (SELECT SystemID FROM @Jobs);
    DELETE @Claims;
    INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1, 'office-worker', 5;
    DECLARE @Lease uniqueidentifier = (SELECT LeaseToken FROM @Claims WHERE SystemID=@Root);
    EXEC dbo.automatedcalls_BeginAttempt @Root, @Lease, 'office-worker', '+15555550100';
    DECLARE @Call int = (SELECT CurrentAttemptId FROM dbo.AutomatedCallQueue WHERE SystemID=@Root),
        @Now datetime2(3) = SYSUTCDATETIME();
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND PhoneType=@Primary)
        THROW 51000, 'The primary officer device must be saved on its attempt.', 1;
    EXEC dbo.automatedcalls_Status @Call, 'CA11111111111111111111111111111111', '+15555550100', 'no-answer', 1, @Now;
    DECLARE @Fallback int = (SELECT SystemID FROM dbo.AutomatedCallQueue WHERE RootQueueId=@Root AND CallDeviceIndex=1);
    IF @Fallback IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Fallback
        AND OptionsJSON=@Options AND FlowVersionId=@Version)
        THROW 51000, 'Office/cell fallback must preserve the officer snapshot and version.', 1;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1, 'office-worker', 5;
    SELECT @Lease=LeaseToken FROM @Claims WHERE SystemID=@Fallback;
    EXEC dbo.automatedcalls_BeginAttempt @Fallback, @Lease, 'office-worker', '+15555550102';
    SELECT @Call=CurrentAttemptId FROM dbo.AutomatedCallQueue WHERE SystemID=@Fallback;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND PhoneType=@Secondary)
        THROW 51000, 'The secondary officer device must be saved on its attempt.', 1;
    EXEC dbo.automatedcalls_Status @Call, 'CA22222222222222222222222222222222', '+15555550102', 'completed', 1, @Now;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallNoteOutbox WHERE Kind='RELEASE' AND QueueId=@Root) <> 1
        THROW 51000, 'Office/cell fallback completion must release the root wait once.', 1;
    SET @Case += 1;
END;

DECLARE @Invalid TABLE(OptionsJSON nvarchar(max));
INSERT @Invalid VALUES
    (JSON_MODIFY(@Options, '$.officer', NULL)),
    (JSON_MODIFY(@Options, '$.callDevices[1].phoneNumber', '+15555550102')),
    (JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(@Options, '$.officer.selection.type', 'Custom'),
        'strict $.officer.id', NULL), 'strict $.officer.name', NULL));
DECLARE @InvalidOptions nvarchar(max);
DECLARE InvalidOptions CURSOR LOCAL FAST_FORWARD FOR SELECT OptionsJSON FROM @Invalid;
OPEN InvalidOptions;
FETCH NEXT FROM InvalidOptions INTO @InvalidOptions;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @Key=NEWID();
    BEGIN TRY
        EXEC dbo.activealarms_call_InsertJob 42, 'original-oid', @Flow,
            @VariablesJSON=N'{}', @EnqueueIdempotencyKey=@Key, @OptionsJSON=@InvalidOptions, @FlowVersionId=@Version;
        THROW 51999, 'Expected invalid office-phone options rejection.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    FETCH NEXT FROM InvalidOptions INTO @InvalidOptions;
END;
CLOSE InvalidOptions;
DEALLOCATE InvalidOptions;
PRINT 'OfficePhone checks passed: primary/secondary, fallback, attempt device, pinned identity/version, root wait and participant/custom isolation.';
