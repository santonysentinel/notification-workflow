SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'NotificationWorkflowVoiceTests'
    THROW 51000, 'Run only in the isolated NotificationWorkflowVoiceTests database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @QueueId int, @CallId int, @FlowId int, @Lease uniqueidentifier = NEWID(),
            @Sid varchar(200) = 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', @Phone varchar(16) = '+15551234567',
            @Timestamp datetime2(3) = '2026-10-08T10:00:00.000',
            @Source uniqueidentifier = NEWID(), @Next uniqueidentifier = NEWID(), @Candidate uniqueidentifier = NEWID(),
            @Bundle nvarchar(max) = N'<CallFlow initialStep="opening"><Step id="opening"><Response><Say>Hello.</Say></Response></Step><Step id="end"><Response><Hangup/></Response></Step></CallFlow>',
            @Graph nvarchar(max) = N'{"schemaVersion":1,"initialStep":"opening","steps":[{"id":"opening","type":"redirect","transitions":{"next":"end"}},{"id":"end","type":"terminal"}]}';
    DECLARE @Result TABLE (Outcome varchar(30), Applied bit, QueueFinalized bit, IgnoreReason varchar(40));
    DECLARE @StepResult TABLE (Outcome varchar(30), ResponseTwiML nvarchar(max), StepId nvarchar(200), ExecutionId uniqueidentifier, Replayed bit);

    INSERT INTO dbo.CallFlowTemplates (TemplateJSON, TwiML, CreatedBy) VALUES (@Graph, @Bundle, 'isolated-test');
    SET @FlowId = SCOPE_IDENTITY();
    INSERT INTO dbo.AutomatedCallQueue (ActiveAlarmID, Status, AttemptCount, LeaseToken, LockedBy, LockedUntil)
    VALUES (42, 'PROCESSING', 1, @Lease, 'test-worker', DATEADD(minute, 5, SYSUTCDATETIME()));
    SET @QueueId = SCOPE_IDENTITY();
    INSERT INTO dbo.AutomatedCalls (AutomatedCallQueueId, QueueLeaseToken, Provider, PhoneE164, FlowId, TwiML)
    VALUES (@QueueId, @Lease, 'Twilio', @Phone, @FlowId, @Bundle);
    SET @CallId = SCOPE_IDENTITY();

    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'initiated', 0, @Timestamp,
        @ParametersJSON = N'{"CallbackSource":"call-progress-events","Extra":"preserved"}';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'recorded' AND Applied = 1 AND QueueFinalized = 0)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND providerCallId = @Sid AND CallStatus = 'initiated' AND StartedDateTime IS NULL)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND EventType = 'CallStatusReceived' AND JSON_VALUE(EventJSON, '$.parameters.Extra') = 'preserved')
        THROW 51000, 'Early callback did not bind SID and persist progress without starting instructions.', 1;
    DELETE FROM @Result;
    INSERT INTO @StepResult EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone,
        @ExpectedTwiML = @Bundle, @StepId = 'opening', @ExecutionId = @Source, @ResponseTwiML = N'<Response><Say>Hello.</Say></Response>';
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND CallStatus = 'in-progress' AND LastStatusSequenceNumber = 0)
        THROW 51000, 'Start did not advance the early callback status.', 1;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'ringing', 1, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 0 AND IgnoreReason = 'status-regression')
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND JSON_VALUE(EventJSON, '$.sequenceNumber') = '1' AND JSON_VALUE(EventJSON, '$.applied') = 'false')
        THROW 51000, 'Delayed ringing callback regressed state or was not audited.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'in-progress', 7, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 1) THROW 51000, 'Answered callback was not applied.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'ringing', 6, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 0 AND IgnoreReason = 'older-sequence')
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND LastStatusSequenceNumber = 7 AND CallStatus = 'in-progress')
        THROW 51000, 'Out-of-order callback changed the status watermark.', 1;
    DELETE FROM @Result;
    DELETE FROM @StepResult;
    INSERT INTO @StepResult EXEC dbo.automatedcalls_Next @CallId, @Source, @Sid, @Phone, N'{}',
        'redirect', @Bundle, @Graph, 'opening', 'end', @Next, N'<Response><Hangup/></Response>';
    IF NOT EXISTS (SELECT 1 FROM @StepResult WHERE Outcome = 'advanced') THROW 51000, 'Next fixture failed.', 1;

    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'completed', 8, @Timestamp, 25, 200;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'recorded' AND Applied = 1 AND QueueFinalized = 1)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND CallStatus = 'completed' AND EndedDateTime = @Timestamp AND CallDurationSeconds = 25 AND LastStatusSequenceNumber = 8 AND LastStatusDateTime = @Timestamp)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID = @QueueId AND Status = 'COMPLETED' AND CompletedAt = @Timestamp AND FailedAt IS NULL AND LeaseToken IS NULL AND LockedBy IS NULL AND LockedUntil IS NULL)
        THROW 51000, 'Completed call did not atomically finalize reporting and owned queue.', 1;
    DELETE FROM @Result;
    DECLARE @UpdatedAt datetime2(3) = (SELECT UpdatedDateTime FROM dbo.AutomatedCalls WHERE SystemID = @CallId),
            @EventCount int = (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId);
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'failed', 8, @Timestamp, 99;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'duplicate' AND Applied = 0)
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> @EventCount
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND UpdatedDateTime = @UpdatedAt AND CallDurationSeconds = 25)
        THROW 51000, 'Duplicate callback mutated the first committed outcome.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'busy', 9, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 0 AND IgnoreReason = 'already-terminal')
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND CallStatus = 'completed' AND LastStatusSequenceNumber = 8 AND CallDurationSeconds = 25)
        THROW 51000, 'Terminal outcome was not sticky.', 1;
    DELETE FROM @StepResult;
    INSERT INTO @StepResult EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone;
    IF NOT EXISTS (SELECT 1 FROM @StepResult WHERE Outcome = 'started' AND Replayed = 1 AND ExecutionId = @Source)
        THROW 51000, 'Terminal call lost its saved start replay.', 1;
    DELETE FROM @StepResult;
    INSERT INTO @StepResult EXEC dbo.automatedcalls_Next @CallId, @Source, @Sid, @Phone, N'{}';
    IF NOT EXISTS (SELECT 1 FROM @StepResult WHERE Outcome = 'advanced' AND Replayed = 1 AND ExecutionId = @Next)
        THROW 51000, 'Terminal call lost its saved transition replay.', 1;
    DELETE FROM @StepResult;
    INSERT INTO @StepResult EXEC dbo.automatedcalls_Next @CallId, @Next, @Sid, @Phone, N'{}',
        'redirect', @Bundle, @Graph, 'end', 'opening', @Candidate, N'<Response><Say>Hello.</Say></Response>';
    IF NOT EXISTS (SELECT 1 FROM @StepResult WHERE Outcome = 'call-ended' AND ResponseTwiML IS NULL)
        THROW 51000, 'Terminal call issued a new transition.', 1;

    DECLARE @TerminalStatuses TABLE (Status varchar(20));
    INSERT INTO @TerminalStatuses VALUES ('completed'), ('busy'), ('failed'), ('no-answer'), ('canceled');
    DECLARE @Status varchar(20), @Duration int;
    WHILE EXISTS (SELECT 1 FROM @TerminalStatuses)
    BEGIN
        SELECT TOP (1) @Status = Status FROM @TerminalStatuses;
        SET @Lease = NEWID();
        INSERT INTO dbo.AutomatedCallQueue (ActiveAlarmID, Status, AttemptCount, LeaseToken, LockedBy)
        VALUES (42, 'PROCESSING', 1, @Lease, 'test-worker');
        SET @QueueId = SCOPE_IDENTITY();
        INSERT INTO dbo.AutomatedCalls (AutomatedCallQueueId, QueueLeaseToken, Provider, PhoneE164, TwiML)
        VALUES (@QueueId, @Lease, 'Twilio', @Phone, @Bundle);
        SET @CallId = SCOPE_IDENTITY();
        SET @Duration = CASE WHEN @Status = 'completed' THEN 0 END;
        DELETE FROM @Result;
        INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, @Status, 0, @Timestamp, @Duration;
        IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 1 AND QueueFinalized = 1)
           OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND CallStatus = @Status AND providerCallId = @Sid AND StartedDateTime IS NULL AND EndedDateTime = @Timestamp AND (CallDurationSeconds = @Duration OR (CallDurationSeconds IS NULL AND @Duration IS NULL)))
           OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID = @QueueId AND Status = CASE WHEN @Status = 'completed' THEN 'COMPLETED' ELSE 'FAILED' END AND (FailedAt = @Timestamp OR CompletedAt = @Timestamp) AND LeaseToken IS NULL)
            THROW 51000, 'Early terminal callback did not correctly close its owned queue.', 1;
        DELETE FROM @StepResult;
        INSERT INTO @StepResult EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone;
        IF NOT EXISTS (SELECT 1 FROM @StepResult WHERE Outcome = 'call-ended') THROW 51000, 'Early terminal attempt accepted first instructions.', 1;
        DELETE FROM @TerminalStatuses WHERE Status = @Status;
    END;

    SET @Lease = NEWID();
    INSERT INTO dbo.AutomatedCallQueue (ActiveAlarmID, Status, AttemptCount, LeaseToken) VALUES (42, 'PROCESSING', 2, @Lease);
    SET @QueueId = SCOPE_IDENTITY();
    INSERT INTO dbo.AutomatedCalls (AutomatedCallQueueId, QueueLeaseToken, Provider, PhoneE164) VALUES (@QueueId, NEWID(), 'Twilio', @Phone);
    SET @CallId = SCOPE_IDENTITY();
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'completed', 0, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 1 AND QueueFinalized = 0)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID = @QueueId AND Status = 'PROCESSING' AND LeaseToken = @Lease)
        THROW 51000, 'Old attempt closed a re-claimed queue job.', 1;
    INSERT INTO dbo.AutomatedCalls (AutomatedCallQueueId, Provider, PhoneE164) VALUES (@QueueId, 'Twilio', @Phone);
    SET @CallId = SCOPE_IDENTITY();
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'failed', 0, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 1 AND QueueFinalized = 0)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID = @QueueId AND Status = 'PROCESSING' AND LeaseToken = @Lease)
        THROW 51000, 'Attempt missing lease ownership modified the queue.', 1;

    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, 'CAbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', @Phone, 'ringing', 1, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'call-sid-mismatch') THROW 51000, 'SID mismatch was accepted.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, '+15557654321', 'ringing', 1, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'destination-mismatch') THROW 51000, 'Destination mismatch was accepted.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status -1, @Sid, @Phone, 'ringing', 1, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'not-found') THROW 51000, 'Missing attempt was accepted.', 1;
    UPDATE dbo.AutomatedCalls SET Provider = 'other' WHERE SystemID = @CallId;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'ringing', 1, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'provider-mismatch') THROW 51000, 'Provider mismatch was accepted.', 1;

    INSERT INTO dbo.AutomatedCalls (Provider, PhoneE164) VALUES ('Twilio', @Phone);
    SET @CallId = SCOPE_IDENTITY();
    DECLARE @Progress TABLE (SequenceNumber int PRIMARY KEY, Status varchar(20));
    INSERT INTO @Progress VALUES (0, 'queued'), (1, 'initiated'), (2, 'ringing'), (3, 'in-progress');
    DECLARE @Sequence int;
    WHILE EXISTS (SELECT 1 FROM @Progress)
    BEGIN
        SELECT TOP (1) @Sequence = SequenceNumber, @Status = Status FROM @Progress ORDER BY SequenceNumber;
        DELETE FROM @Result;
        INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, @Status, @Sequence, @Timestamp, 42;
        IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 1 AND QueueFinalized = 0)
           OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND CallStatus = @Status AND LastStatusSequenceNumber = @Sequence AND EndedDateTime IS NULL AND CallDurationSeconds IS NULL)
            THROW 51000, 'Nonterminal progression or duration handling failed.', 1;
        DELETE FROM @Progress WHERE SequenceNumber = @Sequence;
    END;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Status @CallId, @Sid, @Phone, 'answered', 4, @Timestamp;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-status')
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 4
        THROW 51000, 'Invalid direct SQL input changed state.', 1;

    ROLLBACK TRANSACTION;
    PRINT 'automatedcalls_Status SQL regression checks passed; test data rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

IF OBJECT_ID('dbo.VoiceStatusRollbackTest', 'TR') IS NOT NULL
    THROW 51000, 'Rollback test trigger already exists; do not replace it.', 1;

DECLARE @RollbackCallId int, @RollbackQueueId int, @RollbackLease uniqueidentifier = NEWID(),
        @TriggerCreated bit = 0, @FailureNumber int;
BEGIN TRY
    INSERT INTO dbo.AutomatedCallQueue (ActiveAlarmID, Status, AttemptCount, LeaseToken) VALUES (42, 'PROCESSING', 1, @RollbackLease);
    SET @RollbackQueueId = SCOPE_IDENTITY();
    INSERT INTO dbo.AutomatedCalls (AutomatedCallQueueId, QueueLeaseToken, Provider, PhoneE164)
    VALUES (@RollbackQueueId, @RollbackLease, 'Twilio', '+15551234567');
    SET @RollbackCallId = SCOPE_IDENTITY();
    DECLARE @TriggerSQL nvarchar(max) = N'CREATE TRIGGER dbo.VoiceStatusRollbackTest ON dbo.AutomatedCallEvents AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE CallId = '
        + CONVERT(varchar(11), @RollbackCallId) + N') THROW 51001, ''Forced event persistence failure.'', 1; END;';
    EXEC(@TriggerSQL);
    SET @TriggerCreated = 1;
    BEGIN TRY
        EXEC dbo.automatedcalls_Status @RollbackCallId, 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '+15551234567', 'completed', 0, '2026-10-08T10:00:00', 25;
    END TRY
    BEGIN CATCH
        SET @FailureNumber = ERROR_NUMBER();
    END CATCH;
    IF @FailureNumber IS NULL OR @FailureNumber <> 51001
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @RollbackCallId AND providerCallId IS NULL AND CallStatus IS NULL AND EndedDateTime IS NULL AND UpdatedDateTime IS NULL)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID = @RollbackQueueId AND Status = 'PROCESSING' AND LeaseToken = @RollbackLease)
       OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @RollbackCallId)
        THROW 51000, 'Event persistence failure did not roll back SID, reporting, and queue updates.', 1;
    DROP TRIGGER dbo.VoiceStatusRollbackTest;
    SET @TriggerCreated = 0;
    DELETE FROM dbo.AutomatedCalls WHERE SystemID = @RollbackCallId;
    DELETE FROM dbo.AutomatedCallQueue WHERE SystemID = @RollbackQueueId;
    PRINT 'Status event failure rollback check passed; fixtures cleaned up.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    IF @TriggerCreated = 1 DROP TRIGGER dbo.VoiceStatusRollbackTest;
    DELETE FROM dbo.AutomatedCallEvents WHERE CallId = @RollbackCallId;
    DELETE FROM dbo.AutomatedCalls WHERE SystemID = @RollbackCallId;
    DELETE FROM dbo.AutomatedCallQueue WHERE SystemID = @RollbackQueueId;
    THROW;
END CATCH;