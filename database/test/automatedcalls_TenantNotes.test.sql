SET NOCOUNT ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT OFF;
IF DB_NAME() <> N'NotificationWorkflowTenantNoteTests'
    THROW 51000, 'Run tenant-note tests only in the owned scratch database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.CallFlowTemplates(Name, CreatedBy) VALUES('note-test', N'test');
    DECLARE @FlowId int = SCOPE_IDENTITY(), @Key uniqueidentifier = NEWID();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID, Version, TemplateJSON, TwiML, CreatedBy) VALUES(@FlowId, 1, N'{}', N'<CallFlow/>', N'test');
    DECLARE @FlowIdVersion int = SCOPE_IDENTITY();
    DECLARE @Queue TABLE (SystemID int);
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 42, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @Key,
        @POGroupNum = 'note-group', @OptionsJSON = N'{"historyId":81,"postRecordingTranscript":true,"callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
    DECLARE @QueueId int = (SELECT SystemID FROM @Queue);
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 42, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @Key,
        @POGroupNum = 'note-group', @OptionsJSON = N'{"postRecordingTranscript":true,"historyId":81,"callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
    IF (SELECT COUNT(DISTINCT SystemID) FROM @Queue) <> 1
        THROW 51000, 'Equivalent immutable options must replay the same queue identity.', 1;
    DECLARE @Lease uniqueidentifier = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease WHERE SystemID = @QueueId;
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, QueueLeaseToken, Provider, OID, PhoneE164, FlowId, FlowVersionId, CallStatus)
    VALUES (@QueueId, @Lease, 'Twilio', 'original-oid', '+15555550100', @FlowId, @FlowIdVersion, 'in-progress');
    DECLARE @CallId int = SCOPE_IDENTITY(), @End datetime2(3) = '2026-10-20T23:50:04.123';
    UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId = @CallId WHERE SystemID = @QueueId;

    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus)
    VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'completed');
    DECLARE @RecordingId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON)
    VALUES (@RecordingId, 1, N'{}');
    DECLARE @JobId int = SCOPE_IDENTITY();
    DECLARE @Correlation uniqueidentifier = NEWID(), @Hash varbinary(32) = HASHBYTES('SHA2_256', 'note-test');
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId, RecordingId, AttemptNumber, CorrelationId, CallbackTokenHash, CallbackDeadlineDateTime)
    VALUES (@JobId, @RecordingId, 1, @Correlation, @Hash, DATEADD(day, 1, SYSUTCDATETIME()));
    DECLARE @SubmissionId int = SCOPE_IDENTITY(), @RequestId varchar(200) = CONVERT(varchar(36), @Correlation);
    EXEC dbo.transcription_CompleteCallback @CorrelationId = @Correlation, @RecordingId = @RecordingId,
        @TokenHash = @Hash, @RequestId = @RequestId, @PayloadHash = @Hash, @Success = 1, @ResponseJSON = N'{}',
        @TranscriptsJSON = N'[{"sentence":0,"channel":1,"startTimeMilliseconds":1250,"endTimeMilliseconds":2500,"transcriptText":"Hello"}]';
    EXEC dbo.transcription_QueueTranscriptNote @JobId;

    DECLARE @Claim TABLE
    (
        SystemID bigint, DeliveryId uniqueidentifier, LeaseToken uniqueidentifier, CallId int,
        Kind varchar(20), OID varchar(20), HistoryID int, OccurredAt datetime2(3),
        RecordingId int, MessagesJSON nvarchar(max), QueueId int, ActiveAlarmID int,
        RecordingSource varchar(100), PhoneType varchar(20), MessageStepId nvarchar(200)
    );
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF EXISTS (SELECT 1 FROM @Claim)
        THROW 51000, 'Transcript must wait for a completed call and delivered completion note.', 1;
    EXEC dbo.automatedcalls_Status @CallId, 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '+15555550100',
        'completed', 1, @End, 30;
    EXEC dbo.automatedcalls_Status @CallId, 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '+15555550100',
        'completed', 1, @End, 30;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallNoteOutbox) <> 2
        THROW 51000, 'Callback replay must not duplicate completion or transcript tasks.', 1;

    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF NOT EXISTS (SELECT 1 FROM @Claim WHERE Kind = 'COMPLETED' AND OID = 'original-oid' AND HistoryID = 81 AND OccurredAt = @End)
        THROW 51000, 'Completion delivery must preserve original routing, history and callback timestamp.', 1;
    DECLARE @Id bigint, @Token uniqueidentifier, @Delivery uniqueidentifier;
    SELECT @Id = SystemID, @Token = LeaseToken, @Delivery = DeliveryId FROM @Claim;
    EXEC dbo.automatedcalls_FinishNote @Id, @Token, 0;
    UPDATE dbo.AutomatedCallNoteOutbox SET AvailableAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE SystemID = @Id;
    DELETE @Claim;
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF NOT EXISTS (SELECT 1 FROM @Claim WHERE DeliveryId = @Delivery AND LeaseToken <> @Token)
        THROW 51000, 'Retries must retain delivery identity with a fresh lease.', 1;
    SELECT @Token = LeaseToken FROM @Claim;
    EXEC dbo.automatedcalls_FinishNote @Id, @Token, 1;
    DELETE @Claim;
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF NOT EXISTS
    (
        SELECT 1 FROM @Claim WHERE Kind = 'TRANSCRIPT' AND RecordingId = @RecordingId
          AND JSON_VALUE(MessagesJSON, '$[0].TranscriptText') = 'Hello'
          AND JSON_VALUE(MessagesJSON, '$[0].StartTimeMilliseconds') = '1250'
    )
        THROW 51000, 'Transcript delivery must contain the winning message snapshot and offsets.', 1;
    SELECT @Id = SystemID, @Token = LeaseToken FROM @Claim;
    UPDATE dbo.AutomatedCallNoteOutbox SET LockedUntil = DATEADD(second, -1, SYSUTCDATETIME()) WHERE SystemID = @Id;
    DELETE @Claim;
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF NOT EXISTS (SELECT 1 FROM @Claim WHERE SystemID = @Id AND LeaseToken <> @Token)
        THROW 51000, 'Expired delivery leases must be reclaimable.', 1;
    SELECT @Token = LeaseToken FROM @Claim;
    EXEC dbo.automatedcalls_FinishNote @Id, @Token, 1;

    -- No options means no tenant work, including failed and legacy calls.
    INSERT dbo.AutomatedCalls (Provider, CallStatus) VALUES ('Twilio', 'in-progress');
    DECLARE @LegacyCall int = SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCalls SET CallStatus = 'completed', EndedDateTime = @End WHERE SystemID = @LegacyCall;
    EXEC dbo.automatedcalls_QueueCompletionNote @LegacyCall;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallNoteOutbox) <> 2
        THROW 51000, 'Legacy calls must not guess a tenant HistoryID.', 1;
    UPDATE dbo.AutomatedCallNoteOutbox SET Status = 'PROCESSING', AttemptCount = 10,
        LockedUntil = DATEADD(second, -1, SYSUTCDATETIME()) WHERE SystemID = @Id;
    DELETE @Claim;
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF EXISTS (SELECT 1 FROM @Claim) OR NOT EXISTS
        (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE SystemID = @Id AND Status = 'DEAD_LETTER')
        THROW 51000, 'Exhausted expired leases must dead-letter instead of retrying forever.', 1;

    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, OID, PhoneE164, FlowId, FlowVersionId, CallStatus)
    VALUES (@QueueId, 'Twilio', 'original-oid', '+15555550100', @FlowId, @FlowIdVersion, 'failed');
    DECLARE @FailedCall int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallNoteOutbox (CallId, Kind, SourceId, OID, HistoryID, OccurredAt, RecordingId, MessagesJSON)
    VALUES (@FailedCall, 'TRANSCRIPT', @JobId, 'original-oid', 81, SYSUTCDATETIME(), @RecordingId, N'[]');
    EXEC dbo.automatedcalls_QueueCompletionNote @FailedCall;
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF EXISTS (SELECT 1 FROM @Claim) OR EXISTS
        (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE CallId = @FailedCall AND Status <> 'SKIPPED')
        THROW 51000, 'Failed calls must skip transcripts and never receive a completion note.', 1;

    SAVE TRANSACTION CallbackAtomicity;
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, OID, PhoneE164, FlowId, FlowVersionId, CallStatus)
    VALUES (@QueueId, 'Twilio', 'original-oid', '+15555550100', @FlowId, @FlowIdVersion, 'in-progress');
    DECLARE @RollbackCall int = SCOPE_IDENTITY();
    EXEC dbo.automatedcalls_Status @RollbackCall, 'CAbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', '+15555550100',
        'completed', 1, @End, 30;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE CallId = @RollbackCall)
        THROW 51000, 'Applied callbacks must create their durable completion task.', 1;
    ROLLBACK TRANSACTION CallbackAtomicity;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE CallId = @RollbackCall)
        THROW 51000, 'Rolling back callback state must also roll back its outbox task.', 1;

    DECLARE @DisabledKey uniqueidentifier = NEWID();
    DELETE @Queue;
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 43, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @DisabledKey,
        @POGroupNum = 'note-group', @OptionsJSON = N'{"historyId":81,"postRecordingTranscript":false,"callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
    DECLARE @DisabledQueue int = (SELECT SystemID FROM @Queue);
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, OID, PhoneE164, FlowId, FlowVersionId, CallStatus, EndedDateTime)
    VALUES (@DisabledQueue, 'Twilio', 'original-oid', '+15555550100', @FlowId, @FlowIdVersion, 'completed', @End);
    DECLARE @DisabledCall int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus)
    VALUES (@DisabledCall, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'completed');
    DECLARE @DisabledRecording int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON)
    VALUES (@DisabledRecording, 1, N'{}');
    DECLARE @DisabledJob int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId, RecordingId, AttemptNumber, CallbackTokenHash, CallbackDeadlineDateTime, Status)
    VALUES (@DisabledJob, @DisabledRecording, 1, @Hash, DATEADD(day, 1, SYSUTCDATETIME()), 'COMPLETED');
    DECLARE @DisabledSubmission int = SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = 'COMPLETED', CompletedSubmissionId = @DisabledSubmission,
        CompletedDateTime = SYSUTCDATETIME(), CompletionOrigin = 'PROVIDER' WHERE SystemID = @DisabledJob;
    EXEC dbo.transcription_QueueTranscriptNote @DisabledJob;
    EXEC dbo.automatedcalls_QueueCompletionNote @DisabledCall;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallNoteOutbox WHERE CallId = @DisabledCall) <> 1
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE CallId = @DisabledCall AND Kind <> 'COMPLETED')
        THROW 51000, 'Disabling transcripts must retain only the completion note.', 1;

    -- A record-step message replaces the whole-call transcript note; the whole-call recording is untouched.
    UPDATE dbo.AutomatedCallNoteOutbox SET Status = 'SKIPPED' WHERE Status IN ('READY', 'PROCESSING');
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, OID, PhoneE164, FlowId, FlowVersionId, CallStatus, EndedDateTime, PhoneType)
    VALUES (@QueueId, 'Twilio', 'original-oid', '+15555550100', @FlowId, @FlowIdVersion, 'completed', @End, 'CellPhone');
    DECLARE @MessageCall int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, RecordingSource)
    VALUES (@MessageCall, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REcccccccccccccccccccccccccccccccc', 'completed', 'OutboundAPI');
    DECLARE @WholeRecording int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, RecordingSource)
    VALUES (@MessageCall, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REdddddddddddddddddddddddddddddddd', 'completed', 'RecordVerb');
    DECLARE @MessageRecording int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallEvents (CallId, CallEventDateTime, EventType, EventJSON, IdempotencyKey)
    VALUES (@MessageCall, SYSUTCDATETIME(), 'InputReceived',
        N'{"stepId":"leave-message","inputType":"recording","input":{"digits":"#","recordingSid":"REdddddddddddddddddddddddddddddddd"}}',
        'test:record:' + CONVERT(varchar(36), NEWID()));
    INSERT dbo.AutomatedCallNoteOutbox (CallId, Kind, SourceId, OID, HistoryID, OccurredAt, Status)
    VALUES (@MessageCall, 'COMPLETED', @MessageCall, 'original-oid', 81, @End, 'DELIVERED');
    INSERT dbo.AutomatedCallNoteOutbox (CallId, Kind, SourceId, OID, HistoryID, OccurredAt, RecordingId, MessagesJSON)
    VALUES (@MessageCall, 'TRANSCRIPT', @WholeRecording, 'original-oid', 81, @End, @WholeRecording, N'[]'),
           (@MessageCall, 'TRANSCRIPT', @MessageRecording, 'original-oid', 81, @End, @MessageRecording, N'[]');
    DELETE @Claim;
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF NOT EXISTS (SELECT 1 FROM @Claim WHERE RecordingId = @MessageRecording AND RecordingSource = 'RecordVerb'
                   AND PhoneType = 'CellPhone' AND MessageStepId = 'leave-message')
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE RecordingId = @WholeRecording
                       AND Status = 'SKIPPED' AND LastErrorCode = 'RECORD_STEP_MESSAGE')
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE SystemID = @WholeRecording)
        THROW 51000, 'A record-step message must post with its device and step and skip only the whole-call note.', 1;
    SELECT @Id = SystemID, @Token = LeaseToken FROM @Claim;
    EXEC dbo.automatedcalls_FinishNote @Id, @Token, 1;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE')
        THROW 51000, 'Jobs enqueued without waitForCompletion must never release a workflow wait.', 1;

    -- Robo Call waits: every terminal path queues exactly one RELEASE for its queue job.
    UPDATE dbo.AutomatedCallNoteOutbox SET Status = 'SKIPPED' WHERE Status IN ('READY', 'PROCESSING');
    DECLARE @WaitOptions nvarchar(max) = N'{"historyId":82,"postRecordingTranscript":false,"waitForCompletion":true,"callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
    DECLARE @WaitKey uniqueidentifier = NEWID(), @Worker varchar(100) = 'wait-test';
    DECLARE @FirstWaitKey uniqueidentifier = @WaitKey;
    DELETE @Queue;
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 44, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @WaitKey,
        @POGroupNum = 'note-group', @OptionsJSON = @WaitOptions;
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 44, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @WaitKey, @POGroupNum = 'note-group',
        @OptionsJSON = N'{"waitForCompletion":true,"historyId":82,"postRecordingTranscript":false,"callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
    IF (SELECT COUNT(DISTINCT SystemID) FROM @Queue) <> 1
        THROW 51000, 'Equivalent wait options must replay the same queue identity.', 1;
    DECLARE @WaitQueue int = (SELECT TOP (1) SystemID FROM @Queue);
    SET @Lease = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @WaitQueue;
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, QueueLeaseToken, Provider, OID, PhoneE164, FlowId, FlowVersionId, CallStatus)
    VALUES (@WaitQueue, @Lease, 'Twilio', 'original-oid', '+15555550101', @FlowId, @FlowIdVersion, 'in-progress');
    DECLARE @WaitCall int = SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId = @WaitCall WHERE SystemID = @WaitQueue;
    EXEC dbo.automatedcalls_Status @WaitCall, 'CAcccccccccccccccccccccccccccccccc', '+15555550101',
        'ringing', 1, @End, NULL;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE')
        THROW 51000, 'Non-terminal callbacks must not release the workflow wait.', 1;
    EXEC dbo.automatedcalls_Status @WaitCall, 'CAcccccccccccccccccccccccccccccccc', '+15555550101',
        'no-answer', 2, @End, 0;
    EXEC dbo.automatedcalls_Status @WaitCall, 'CAcccccccccccccccccccccccccccccccc', '+15555550101',
        'no-answer', 2, @End, 0;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE') <> 1 OR NOT EXISTS
    (
        SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @WaitQueue
          AND SourceId = @WaitQueue AND ActiveAlarmID = 44 AND HistoryID = 82 AND OID = 'original-oid'
          AND CallId = @WaitCall AND Status = 'READY'
    )
        THROW 51000, 'Any final call outcome must queue one release with the original routing.', 1;
    DELETE @Claim;
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF NOT EXISTS (SELECT 1 FROM @Claim WHERE Kind = 'DEVICE' AND QueueId = @WaitQueue AND CallId = @WaitCall
            AND JSON_VALUE(MessagesJSON, '$.phoneType') = 'CellPhone' AND JSON_VALUE(MessagesJSON, '$.status') = 'no-answer'
            AND JSON_VALUE(MessagesJSON, '$.nextPhoneType') IS NULL)
        OR NOT EXISTS (SELECT 1 FROM @Claim WHERE Kind = 'RELEASE' AND QueueId = @WaitQueue AND ActiveAlarmID = 44
            AND HistoryID = 82 AND OID = 'original-oid')
        THROW 51000, 'The last device reports its outcome, then the release is claimable with queue and alarm identity.', 1;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE RootQueueId = @WaitQueue)
        THROW 51000, 'A job without another device must not create a follow-on queue row.', 1;
    DECLARE @Finish CURSOR;
    SET @Finish = CURSOR LOCAL FAST_FORWARD FOR SELECT SystemID, LeaseToken FROM @Claim;
    OPEN @Finish;
    FETCH NEXT FROM @Finish INTO @Id, @Token;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.automatedcalls_FinishNote @Id, @Token, 1;
        FETCH NEXT FROM @Finish INTO @Id, @Token;
    END;
    CLOSE @Finish;

    -- Preparation failure releases without a provider call attempt.
    SET @WaitKey = NEWID();
    DELETE @Queue;
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 45, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @WaitKey,
        @POGroupNum = 'note-group', @OptionsJSON = @WaitOptions;
    DECLARE @PrepQueue int = (SELECT SystemID FROM @Queue);
    SET @Lease = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @PrepQueue;
    EXEC dbo.automatedcalls_FailPreparation @PrepQueue, @Lease, @Worker;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @PrepQueue
        AND CallId IS NULL AND ActiveAlarmID = 45)
        THROW 51000, 'Preparation failures must release the workflow wait.', 1;

    -- Recovery: WAIT keeps the job alive; DEAD_LETTER is final and releases.
    SET @WaitKey = NEWID();
    DELETE @Queue;
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 46, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @WaitKey,
        @POGroupNum = 'note-group', @OptionsJSON = @WaitOptions;
    DECLARE @DeadQueue int = (SELECT SystemID FROM @Queue);
    SET @Lease = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @DeadQueue;
    EXEC dbo.automatedcalls_RecoveryDisposition @DeadQueue, @Lease, @Worker, 'WAIT', 'TEST', 1, 3, 3, 24;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @DeadQueue)
        THROW 51000, 'Recoverable dispositions must not release the workflow wait.', 1;
    SET @Lease = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @DeadQueue;
    EXEC dbo.automatedcalls_RecoveryDisposition @DeadQueue, @Lease, @Worker, 'DEAD_LETTER', 'TEST', 1, 3, 3, 24;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @DeadQueue AND ActiveAlarmID = 46)
        THROW 51000, 'Giving up on a queue job must release the workflow wait.', 1;

    -- Reconciliation: a non-terminal observation keeps waiting; a terminal one releases.
    SET @WaitKey = NEWID();
    DELETE @Queue;
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 47, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @WaitKey,
        @POGroupNum = 'note-group', @OptionsJSON = @WaitOptions;
    DECLARE @ObservedQueue int = (SELECT SystemID FROM @Queue);
    SET @Lease = NEWID();
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, QueueLeaseToken, Provider, providerCallId, OID, PhoneE164, FlowId, FlowVersionId, CallStatus)
    VALUES (@ObservedQueue, @Lease, 'Twilio', 'CAdddddddddddddddddddddddddddddddd', 'original-oid', '+15555550102', @FlowId, @FlowIdVersion, 'in-progress');
    DECLARE @ObservedCall int = SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker, CurrentAttemptId = @ObservedCall,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @ObservedQueue;
    EXEC dbo.automatedcalls_Observe @ObservedQueue, @Lease, @Worker, @ObservedCall, 'CAdddddddddddddddddddddddddddddddd', 'in-progress';
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @ObservedQueue)
        THROW 51000, 'In-progress reconciliation must not release the workflow wait.', 1;
    SET @Lease = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @ObservedQueue;
    EXEC dbo.automatedcalls_Observe @ObservedQueue, @Lease, @Worker, @ObservedCall, 'CAdddddddddddddddddddddddddddddddd', 'busy';
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @ObservedQueue
        AND CallId = @ObservedCall AND ActiveAlarmID = 47)
        THROW 51000, 'Terminal reconciliation must release the workflow wait.', 1;

    -- Call devices: an unsuccessful primary queues the secondary immediately; the root is released after the last device.
    DECLARE @TwoDevices nvarchar(max) = N'{"historyId":83,"postRecordingTranscript":true,"waitForCompletion":true,"callDevices":[{"type":"CellPhone","phoneNumber":null},{"type":"Custom","phoneNumber":"+15555550199"}]}';
    SET @WaitKey = NEWID();
    DELETE @Queue;
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 49, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @WaitKey,
        @POGroupNum = 'note-group', @OptionsJSON = @TwoDevices;
    DECLARE @RootQueue int = (SELECT SystemID FROM @Queue);
    SET @Lease = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @RootQueue;
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, QueueLeaseToken, Provider, OID, PhoneE164, FlowId, FlowVersionId, CallStatus, PhoneType, DeviceIndex)
    VALUES (@RootQueue, @Lease, 'Twilio', 'original-oid', '+15555550103', @FlowId, @FlowIdVersion, 'ringing', 'CellPhone', 0);
    DECLARE @PrimaryCall int = SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId = @PrimaryCall WHERE SystemID = @RootQueue;
    EXEC dbo.automatedcalls_Status @PrimaryCall, 'CAeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', '+15555550103',
        'no-answer', 1, @End, 0;
    DECLARE @SecondQueue int = (SELECT SystemID FROM dbo.AutomatedCallQueue WHERE RootQueueId = @RootQueue);
    IF @SecondQueue IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID = @SecondQueue
            AND CallDeviceIndex = 1 AND Status = 'READY' AND ActiveAlarmID = 49 AND FlowId = @FlowId
            AND OptionsJSON = @TwoDevices AND AvailableAt <= DATEADD(millisecond, 1, SYSUTCDATETIME()))
        OR (SELECT Status FROM dbo.AutomatedCallQueue WHERE SystemID = @RootQueue) <> 'FAILED'
        THROW 51000, 'A no-answer primary must end its row and queue the secondary immediately.', 1;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @RootQueue)
        THROW 51000, 'The wait must not be released while another device remains.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'DEVICE' AND QueueId = @RootQueue AND CallId = @PrimaryCall
            AND HistoryID = 83 AND ActiveAlarmID = 49 AND JSON_VALUE(MessagesJSON, '$.phoneType') = 'CellPhone'
            AND JSON_VALUE(MessagesJSON, '$.status') = 'no-answer' AND JSON_VALUE(MessagesJSON, '$.nextPhoneType') = 'Custom'
            AND JSON_VALUE(MessagesJSON, '$.messageLeft') = 'false')
        THROW 51000, 'An unsuccessful device must queue a DEVICE note naming the next device.', 1;
    EXEC dbo.automatedcalls_Status @PrimaryCall, 'CAeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', '+15555550103',
        'no-answer', 2, @End, 0;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallQueue WHERE RootQueueId = @RootQueue) <> 1
        OR (SELECT COUNT(*) FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'DEVICE' AND QueueId = @RootQueue) <> 1
        THROW 51000, 'Replayed terminal callbacks must not queue another device.', 1;

    -- The secondary reaches voicemail: detection marks the machine, the flow left a message, and the root wait is released.
    SET @Lease = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @SecondQueue;
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, QueueLeaseToken, Provider, OID, PhoneE164, FlowId, FlowVersionId, CallStatus, PhoneType, DeviceIndex)
    VALUES (@SecondQueue, @Lease, 'Twilio', 'original-oid', '+15555550199', @FlowId, @FlowIdVersion, 'in-progress', 'Custom', 1);
    DECLARE @SecondCall int = SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId = @SecondCall WHERE SystemID = @SecondQueue;
    INSERT dbo.AutomatedCallEvents (CallId, CallEventDateTime, EventType, EventJSON, IdempotencyKey)
    VALUES (@SecondCall, @End, 'CallStarted', N'{"stepId":"voicemail"}', 'voice:start');
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus)
    VALUES (@SecondCall, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REffffffffffffffffffffffffffffffff', 'completed');
    DECLARE @MachineRecording int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallNoteOutbox (CallId, Kind, SourceId, OID, HistoryID, OccurredAt, RecordingId, MessagesJSON)
    VALUES (@SecondCall, 'TRANSCRIPT', 99, 'original-oid', 83, @End, @MachineRecording, N'[]');
    EXEC dbo.automatedcalls_Status @SecondCall, 'CAffffffffffffffffffffffffffffffff', '+15555550199',
        'completed', 1, @End, 20, NULL, N'{"AnsweredBy":"machine_end_beep"}';
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @SecondCall AND AnsweredBy = 'machine_end_beep')
        THROW 51000, 'Status callbacks must record who answered.', 1;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE CallId = @SecondCall AND Kind = 'COMPLETED')
        THROW 51000, 'A machine-answered call must not post the completion note.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'DEVICE' AND QueueId = @SecondQueue AND CallId = @SecondCall
            AND JSON_VALUE(MessagesJSON, '$.phoneType') = 'Custom' AND JSON_VALUE(MessagesJSON, '$.status') = 'completed'
            AND JSON_VALUE(MessagesJSON, '$.answeredBy') = 'machine_end_beep' AND JSON_VALUE(MessagesJSON, '$.messageLeft') = 'true'
            AND JSON_VALUE(MessagesJSON, '$.nextPhoneType') IS NULL)
        THROW 51000, 'A machine answer must report the device, the machine and the left message.', 1;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId IN (@RootQueue, @SecondQueue)) <> 1
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @RootQueue
            AND SourceId = @RootQueue AND ActiveAlarmID = 49 AND HistoryID = 83)
        THROW 51000, 'The last device must release the wait on the root queue row.', 1;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE RootQueueId = @RootQueue AND CallDeviceIndex > 1)
        THROW 51000, 'There is no device after the secondary.', 1;
    IF (SELECT Status FROM dbo.AutomatedCallQueue WHERE SystemID = @SecondQueue) <> 'COMPLETED'
        THROW 51000, 'A machine-answered call still completes its queue row.', 1;
    UPDATE dbo.AutomatedCallNoteOutbox SET Status = 'SKIPPED' WHERE Status = 'READY' AND Kind IN ('DEVICE', 'RELEASE');
    DELETE @Claim;
    INSERT @Claim EXEC dbo.automatedcalls_ClaimNote;
    IF EXISTS (SELECT 1 FROM @Claim) OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox
            WHERE CallId = @SecondCall AND Kind = 'TRANSCRIPT' AND Status = 'SKIPPED' AND LastErrorCode = 'CALL_NOT_COMPLETED')
        THROW 51000, 'Machine-answered calls must skip their transcripts.', 1;

    -- A device without a phone number is skipped without dialling and moves on to the next device.
    SET @WaitKey = NEWID();
    DELETE @Queue;
    INSERT @Queue EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 50, @OID = 'original-oid',
        @FlowId = @FlowId, @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @WaitKey, @POGroupNum = 'note-group',
        @OptionsJSON = N'{"historyId":84,"postRecordingTranscript":false,"waitForCompletion":true,"callDevices":[{"type":"WorkPhone","phoneNumber":null},{"type":"HomePhone","phoneNumber":null}]}';
    DECLARE @SkipQueue int = (SELECT SystemID FROM @Queue);
    SET @Lease = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @SkipQueue;
    EXEC dbo.automatedcalls_SkipDevice @SkipQueue, @Lease, @Worker;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID = @SkipQueue AND Status = 'FAILED'
            AND LastErrorCode = 'NO_PHONE_NUMBER' AND LeaseToken IS NULL)
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE RootQueueId = @SkipQueue AND CallDeviceIndex = 1 AND Status = 'READY')
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'DEVICE' AND QueueId = @SkipQueue AND CallId IS NULL
            AND JSON_VALUE(MessagesJSON, '$.phoneType') = 'WorkPhone' AND JSON_VALUE(MessagesJSON, '$.status') = 'no-phone-number'
            AND JSON_VALUE(MessagesJSON, '$.nextPhoneType') = 'HomePhone')
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @SkipQueue)
        THROW 51000, 'A skipped device must end its row, queue the next device and report the skip.', 1;
    DECLARE @LastSkip int = (SELECT SystemID FROM dbo.AutomatedCallQueue WHERE RootQueueId = @SkipQueue);
    SET @Lease = NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status = 'PROCESSING', LeaseToken = @Lease, LockedBy = @Worker,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @LastSkip;
    EXEC dbo.automatedcalls_SkipDevice @LastSkip, @Lease, @Worker;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'RELEASE' AND QueueId = @SkipQueue AND ActiveAlarmID = 50)
        OR (SELECT COUNT(*) FROM dbo.AutomatedCallNoteOutbox WHERE Kind = 'DEVICE' AND QueueId IN (@SkipQueue, @LastSkip)) <> 2
        THROW 51000, 'Skipping the last device must release the root wait.', 1;
    COMMIT;

    BEGIN TRY
        DECLARE @WrongLease uniqueidentifier = NEWID();
        EXEC dbo.automatedcalls_SkipDevice @LastSkip, @WrongLease, @Worker;
        THROW 51003, 'Skipping requires the current lease.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51002 THROW;
    END CATCH;

    BEGIN TRY
        EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 44, @OID = 'original-oid', @FlowId = @FlowId,
            @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @FirstWaitKey, @POGroupNum = 'note-group',
            @OptionsJSON = N'{"historyId":82,"postRecordingTranscript":false,"callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
        THROW 51002, 'Enqueue replay must not change wait policy.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51001 THROW;
    END CATCH;
    BEGIN TRY
        DECLARE @BadWaitKey uniqueidentifier = NEWID();
        EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 48, @OID = 'original-oid', @FlowId = @FlowId,
            @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @BadWaitKey, @POGroupNum = 'note-group',
            @OptionsJSON = N'{"historyId":82,"postRecordingTranscript":false,"waitForCompletion":"yes","callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
        THROW 51002, 'Wait policy must be a JSON boolean.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;

    BEGIN TRY
        EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 42, @OID = 'original-oid', @FlowId = @FlowId,
            @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @Key, @POGroupNum = 'note-group',
            @OptionsJSON = N'{"historyId":81,"postRecordingTranscript":false,"callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
        THROW 51002, 'Enqueue replay must not change transcript policy.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51001 THROW;
    END CATCH;
    BEGIN TRY
        EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 42, @OID = 'original-oid', @FlowId = @FlowId,
            @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @Key, @POGroupNum = 'note-group',
            @OptionsJSON = N'{"historyId":0,"postRecordingTranscript":true,"callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
        THROW 51002, 'Invalid original history must be rejected.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    BEGIN TRY
        EXEC dbo.activealarms_call_InsertJob @ActiveAlarmID = 42, @OID = 'original-oid', @FlowId = @FlowId,
            @VariablesJSON = N'{}', @EnqueueIdempotencyKey = @Key, @POGroupNum = 'note-group',
            @OptionsJSON = N'{"HistoryId":81,"PostRecordingTranscript":true,"callDevices":[{"type":"CellPhone","phoneNumber":null}]}';
        THROW 51002, 'Options must use the exact property names used by callback extraction.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    PRINT 'Tenant note outbox SQL regression checks passed.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK;
    THROW;
END CATCH;
