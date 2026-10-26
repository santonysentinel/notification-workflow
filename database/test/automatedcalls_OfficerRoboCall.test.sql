SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
IF DB_NAME() <> N'NotificationWorkflowVoiceTests'
    THROW 51000, 'Run officer call tests only in the owned scratch database; fixtures are not removed.', 1;

INSERT dbo.CallFlowTemplates(Name, CreatedBy) VALUES('officer-test', N'test');
DECLARE @Flow int = SCOPE_IDENTITY();
INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID, Version, TemplateJSON, TwiML, CreatedBy)
VALUES(@Flow, 1, N'{}', N'<CallFlow/>', N'test');
DECLARE @Version int = SCOPE_IDENTITY(), @Key uniqueidentifier = NEWID();
DECLARE @Options nvarchar(max) = N'{"historyId":81,"postRecordingTranscript":true,"waitForCompletion":true,
 "callDevices":[{"type":"CellPhone","phoneNumber":null},{"type":"Custom","phoneNumber":"+15555550101"}],
 "officer":{"selection":{"type":"RoleBased","roleType":"Custom duty role","roleId":7,"rolePOGroup":"OTHER"},"id":91,"name":"Officer Name"}}';
DECLARE @Jobs TABLE(SystemID int);
INSERT @Jobs EXEC dbo.activealarms_call_InsertJob 42, 'original-oid', @Flow,
    @VariablesJSON=N'{}', @EnqueueIdempotencyKey=@Key, @OptionsJSON=@Options, @POGroupNum='OFFICER', @FlowVersionId=@Version;
DECLARE @Root int = (SELECT SystemID FROM @Jobs);
DELETE @Jobs;
INSERT @Jobs EXEC dbo.activealarms_call_InsertJob 42, 'original-oid', @Flow,
    @VariablesJSON=N'{}', @EnqueueIdempotencyKey=@Key, @OptionsJSON=@Options, @POGroupNum='OFFICER', @FlowVersionId=@Version;
IF (SELECT SystemID FROM @Jobs) <> @Root THROW 51000, 'Officer enqueue replay must return the original job.', 1;

DECLARE @Conflicting nvarchar(max) = JSON_MODIFY(@Options, '$.officer.id', 92);
BEGIN TRY
    EXEC dbo.activealarms_call_InsertJob 42, 'original-oid', @Flow,
        @VariablesJSON=N'{}', @EnqueueIdempotencyKey=@Key, @OptionsJSON=@Conflicting, @POGroupNum='OFFICER';
    THROW 51000, 'Expected pinned officer conflict.', 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() <> 51001 THROW;
END CATCH;

DECLARE @Invalid TABLE(OptionsJSON nvarchar(max));
INSERT @Invalid VALUES
    (JSON_MODIFY(@Options, '$.officer.id', NULL)),
    (JSON_MODIFY(@Options, '$.officer.selection.roleId', 0)),
    (JSON_MODIFY(@Options, '$.officer.selection.rolePOGroup', '')),
    (REPLACE(@Options, '"type":"CellPhone"', '"type":"HomePhone"')),
    (JSON_MODIFY(@Options, '$.officer.selection.type', 'Unknown')),
    (JSON_MODIFY(@Options, '$.officer.selection.type', 'Custom'));
DECLARE @InvalidOptions nvarchar(max), @InvalidKey uniqueidentifier;
DECLARE InvalidOptions CURSOR LOCAL FAST_FORWARD FOR SELECT OptionsJSON FROM @Invalid;
OPEN InvalidOptions;
FETCH NEXT FROM InvalidOptions INTO @InvalidOptions;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @InvalidKey = NEWID();
    BEGIN TRY
        EXEC dbo.activealarms_call_InsertJob 42, 'original-oid', @Flow,
            @VariablesJSON=N'{}', @EnqueueIdempotencyKey=@InvalidKey, @OptionsJSON=@InvalidOptions;
        THROW 51999, 'Expected invalid officer options rejection.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    FETCH NEXT FROM InvalidOptions INTO @InvalidOptions;
END;
CLOSE InvalidOptions;
DEALLOCATE InvalidOptions;

DECLARE @Claims TABLE(SystemID int, ActiveAlarmID int, OID varchar(20), FlowId int, VariablesJSON nvarchar(max),
    EnqueueIdempotencyKey uniqueidentifier, AttemptCount int, MaxAttempts int, LeaseToken uniqueidentifier,
    LockedUntil datetime2(3), CallDeviceIndex int, OptionsJSON nvarchar(max));
INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1, 'officer-worker', 5;
DECLARE @Lease uniqueidentifier = (SELECT LeaseToken FROM @Claims WHERE SystemID=@Root);
EXEC dbo.automatedcalls_BeginAttempt @Root, @Lease, 'officer-worker', '+15555550100';
DECLARE @Call int = (SELECT CurrentAttemptId FROM dbo.AutomatedCallQueue WHERE SystemID=@Root), @Now datetime2(3)=SYSUTCDATETIME();
EXEC dbo.automatedcalls_Status @Call, 'CA11111111111111111111111111111111', '+15555550100', 'no-answer', 1, @Now;
DECLARE @Fallback int = (SELECT SystemID FROM dbo.AutomatedCallQueue WHERE RootQueueId=@Root AND CallDeviceIndex=1);
IF @Fallback IS NULL OR NOT EXISTS (
    SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Fallback AND FlowId=@Flow AND FlowVersionId=@Version AND OptionsJSON=@Options)
    THROW 51000, 'Fallback must preserve the selected officer, devices and pinned flow version.', 1;
IF EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind='RELEASE' AND QueueId=@Root)
    THROW 51000, 'The wait must not release before fallback completes.', 1;

DELETE @Claims;
INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1, 'officer-worker', 5;
SELECT @Lease=LeaseToken FROM @Claims WHERE SystemID=@Fallback;
EXEC dbo.automatedcalls_BeginAttempt @Fallback, @Lease, 'officer-worker', '+15555550101';
DECLARE @FallbackCall int = (SELECT CurrentAttemptId FROM dbo.AutomatedCallQueue WHERE SystemID=@Fallback);
EXEC dbo.automatedcalls_Status @FallbackCall, 'CA22222222222222222222222222222222', '+15555550101', 'completed', 1, @Now;
IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@FallbackCall AND PhoneType='Custom' AND FlowVersionId=@Version)
    THROW 51000, 'Fallback attempt must retain the chosen device and version.', 1;
IF (SELECT COUNT(*) FROM dbo.AutomatedCallNoteOutbox WHERE Kind='RELEASE' AND QueueId=@Root) <> 1
    THROW 51000, 'Completion must release the root wait exactly once without waiting for transcription.', 1;

DECLARE @Notes TABLE(SystemID bigint, DeliveryId uniqueidentifier, LeaseToken uniqueidentifier, CallId int, Kind varchar(20),
    OID varchar(20), HistoryID int, OccurredAt datetime2(3), RecordingId int, MessagesJSON nvarchar(max),
    QueueId int, ActiveAlarmID int, RecordingSource varchar(100), PhoneType varchar(20), MessageStepId nvarchar(200), OfficerJSON nvarchar(max));
INSERT @Notes EXEC dbo.automatedcalls_ClaimNote;
IF NOT EXISTS (SELECT 1 FROM @Notes WHERE Kind='DEVICE' AND JSON_VALUE(OfficerJSON,'$.id')='91')
    THROW 51000, 'Device notes must include the officer snapshot.', 1;
DECLARE @NoteId bigint, @NoteLease uniqueidentifier;
SELECT @NoteId=SystemID, @NoteLease=LeaseToken FROM @Notes;
EXEC dbo.automatedcalls_FinishNote @NoteId, @NoteLease, 1;
DELETE @Notes;
INSERT @Notes EXEC dbo.automatedcalls_ClaimNote;
IF NOT EXISTS (SELECT 1 FROM @Notes WHERE Kind='COMPLETED' AND JSON_VALUE(OfficerJSON,'$.id')='91' AND CallId=@FallbackCall)
    THROW 51000, 'Completion notes must resolve officer identity from the fallback call queue.', 1;
SELECT @NoteId=SystemID, @NoteLease=LeaseToken FROM @Notes;
EXEC dbo.automatedcalls_FinishNote @NoteId, @NoteLease, 1;
DELETE @Notes;
INSERT @Notes EXEC dbo.automatedcalls_ClaimNote;
IF NOT EXISTS (SELECT 1 FROM @Notes WHERE Kind='RELEASE' AND QueueId=@Root AND HistoryID=81 AND OID='original-oid'
    AND JSON_VALUE(OfficerJSON,'$.id')='91')
    THROW 51000, 'Release notes must preserve participant routing and root officer identity.', 1;

INSERT dbo.AutomatedCallRecordings(CallId, Provider, AccountSid, RecordingSid, RecordingStatus, RecordingSource)
VALUES(@FallbackCall,'Twilio','ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','REaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','completed','DialVerb');
DECLARE @WholeRecording int=SCOPE_IDENTITY();
INSERT dbo.AutomatedCallRecordings(CallId, Provider, AccountSid, RecordingSid, RecordingStatus, RecordingSource)
VALUES(@FallbackCall,'Twilio','ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','REbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','completed','RecordVerb');
DECLARE @MessageRecording int=SCOPE_IDENTITY();
INSERT dbo.AutomatedCallEvents(CallId, CallEventDateTime, EventType, EventJSON)
VALUES(@FallbackCall,@Now,'InputReceived',N'{"stepId":"message","inputType":"recording","input":{"recordingSid":"REbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}');
INSERT dbo.AutomatedCallNoteOutbox(CallId,Kind,SourceId,OID,HistoryID,OccurredAt,RecordingId,MessagesJSON)
VALUES(@FallbackCall,'TRANSCRIPT',201,'original-oid',81,@Now,@WholeRecording,N'[]'),
    (@FallbackCall,'TRANSCRIPT',202,'original-oid',81,@Now,@MessageRecording,
      N'[{"Sentence":0,"AudioChannel":0,"StartTimeMilliseconds":1000,"EndTimeMilliseconds":2000,"TranscriptText":"Hello"}]');
DELETE @Notes;
INSERT @Notes EXEC dbo.automatedcalls_ClaimNote;
IF NOT EXISTS (SELECT 1 FROM @Notes WHERE Kind='TRANSCRIPT' AND RecordingId=@MessageRecording
    AND RecordingSource='RecordVerb' AND MessageStepId='message' AND JSON_VALUE(OfficerJSON,'$.id')='91'
    AND JSON_VALUE(MessagesJSON,'$[0].StartTimeMilliseconds')='1000')
    THROW 51000, 'Record-step notes must carry officer identity, device, step and individual message timestamps.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE RecordingId=@WholeRecording AND Status='SKIPPED'
    AND LastErrorCode='RECORD_STEP_MESSAGE')
    OR (SELECT COUNT(*) FROM dbo.AutomatedCallRecordings WHERE CallId=@FallbackCall)<>2
    THROW 51000, 'Only the whole-call note is suppressed; both recordings must remain stored.', 1;

DECLARE @CustomOptions nvarchar(max)=N'{"historyId":81,"postRecordingTranscript":false,
 "callDevices":[{"type":"Custom","phoneNumber":"+15555550100"}],
 "officer":{"selection":{"type":"Custom","roleType":null,"roleId":null,"rolePOGroup":null},"id":null,"name":null}}';
DECLARE @CustomKey uniqueidentifier=NEWID();
DELETE @Jobs;
INSERT @Jobs EXEC dbo.activealarms_call_InsertJob 43, 'original-oid', @Flow,
    @VariablesJSON=N'{}', @EnqueueIdempotencyKey=@CustomKey, @OptionsJSON=@CustomOptions, @POGroupNum='OFFICER';
IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue q JOIN @Jobs j ON j.SystemID=q.SystemID
    WHERE JSON_VALUE(q.OptionsJSON,'$.officer.selection.type')='Custom' AND JSON_VALUE(q.OptionsJSON,'$.officer.id') IS NULL)
    THROW 51000, 'Custom officer calls must not require a resolved officer ID.', 1;
PRINT 'Officer call checks passed: immutable identity, validation, fallback, pinned version, completion, transcripts, custom numbers and root wait release.';
