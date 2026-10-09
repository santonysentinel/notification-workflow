SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() NOT IN (N'NotificationWorkflowVoiceTests', N'NotificationWorkflowTranscriptionSchemaTests')
    THROW 51000, 'Run only in an owned scratch database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @CallId int, @OtherCallId int, @UnboundCallId int, @QueueId int, @RecordingId int,
            @Sid varchar(200) = 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            @AccountSid varchar(34) = 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            @RecordingSid varchar(34) = 'REaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            @Timestamp datetime2(3) = '2026-10-08T10:00:00.000',
            @Url nvarchar(2048) = N'https://api.twilio.com/2010-04-01/Accounts/ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/Recordings/REaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
    DECLARE @Result TABLE (Outcome varchar(30), RecordingId int, Applied bit, IgnoreReason varchar(40));
    INSERT INTO dbo.AutomatedCallQueue (ActiveAlarmID, Status, AttemptCount) VALUES (42, 'COMPLETED', 1);
    SET @QueueId = SCOPE_IDENTITY();
    INSERT INTO dbo.AutomatedCalls (AutomatedCallQueueId, Provider, providerCallId, PhoneE164, CallStatus, EndedDateTime, UpdatedDateTime, LastStatusSequenceNumber)
    VALUES (@QueueId, 'Twilio', @Sid, '+15551234567', 'completed', @Timestamp, @Timestamp, 3);
    SET @CallId = SCOPE_IDENTITY();
    DECLARE @CallBefore nvarchar(max) = (SELECT calls.* FROM dbo.AutomatedCalls AS calls WHERE SystemID = @CallId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
            @QueueBefore nvarchar(max) = (SELECT queue.* FROM dbo.AutomatedCallQueue AS queue WHERE SystemID = @QueueId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES);

    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, @RecordingSid, 'in-progress',
        @RecordingStartTime = @Timestamp, @RecordingSource = 'OutboundAPI', @RecordingTrack = 'both', @ParametersJSON = N'{"Extra":"preserved"}';
    SELECT @RecordingId = RecordingId FROM @Result;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'recorded' AND Applied = 1 AND RecordingId IS NOT NULL)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId AND CallId = @CallId AND RecordingStatus = 'in-progress' AND RecordingUrl IS NULL AND DurationSeconds IS NULL AND Channels IS NULL AND RecordingStartTime = @Timestamp)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND EventType = 'RecordingStatusReceived' AND IdempotencyKey = 'recording:status:' + @RecordingSid + ':in-progress' AND JSON_VALUE(EventJSON, '$.parameters.Extra') = 'preserved' AND JSON_VALUE(EventJSON, '$.applied') = 'true')
        THROW 51000, 'Recording start callback failed after call termination.', 1;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId)
        THROW 51000, 'In-progress recording must not enqueue transcription.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, @RecordingSid, 'completed', @Url, 25, 2;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'recorded' AND Applied = 1 AND RecordingId = @RecordingId)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId AND RecordingStatus = 'completed' AND RecordingUrl = @Url AND DurationSeconds = 25 AND Channels = 2 AND RecordingStartTime = @Timestamp AND RecordingSource = 'OutboundAPI' AND RecordingTrack = 'both')
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallRecordings WHERE CallId = @CallId) <> 1
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 2
        THROW 51000, 'Completed callback did not update the same recording and preserve omitted metadata.', 1;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId) <> 1
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId
            AND Provider='Deepgram' AND ConfigurationVersion=1 AND Status='READY' AND AttemptCount=0
            AND LockedBy IS NULL AND LockedUntil IS NULL AND LeaseToken IS NULL AND CompletedSubmissionId IS NULL
            AND JSON_VALUE(ConfigurationJSON,'$.model')='nova-3' AND JSON_VALUE(ConfigurationJSON,'$.utterances')='true'
            AND JSON_VALUE(ConfigurationJSON,'$.multichannel')='true' AND AvailableAt=CreatedDateTime)
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WHERE RecordingId=@RecordingId)
        THROW 51000, 'Completion must enqueue one unclaimed job with a configuration snapshot and no submission.', 1;
    DECLARE @JobBefore nvarchar(max)=(SELECT * FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId FOR JSON PATH);

    DECLARE @SavedEvent nvarchar(max) = (SELECT EventJSON FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND IdempotencyKey = 'recording:status:' + @RecordingSid + ':completed'),
            @UpdatedAt datetime2(3) = (SELECT UpdatedDateTime FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId);
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, @RecordingSid, 'completed', @Url, 99, 1,
        @ParametersJSON = N'{"Extra":"different retry"}';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'duplicate' AND Applied = 0)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId AND DurationSeconds = 25 AND Channels = 2 AND UpdatedDateTime = @UpdatedAt)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND IdempotencyKey = 'recording:status:' + @RecordingSid + ':completed' AND EventJSON COLLATE Latin1_General_100_BIN2 = @SavedEvent COLLATE Latin1_General_100_BIN2)
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 2
        THROW 51000, 'Duplicate callback changed metadata or the first event payload.', 1;
    IF @JobBefore<>(SELECT * FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId FOR JSON PATH)
        THROW 51000, 'Duplicate callback must not mutate or recreate the transcription job.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, @RecordingSid, 'absent';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 0 AND IgnoreReason = 'terminal-conflict')
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId AND RecordingStatus = 'completed' AND UpdatedDateTime = @UpdatedAt)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND JSON_VALUE(EventJSON, '$.recordingStatus') = 'absent' AND JSON_VALUE(EventJSON, '$.ignoreReason') = 'terminal-conflict')
        THROW 51000, 'Conflicting terminal callback was not audited without overwriting state.', 1;

    SET @RecordingSid = 'REbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
    SET @Url = REPLACE(@Url, 'REaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', @RecordingSid);
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, @RecordingSid, 'completed', @Url, 0, 1,
        @TranscriptionConfigurationVersion=3, @TranscriptionConfigurationJSON=N'{"model":"nova-3","utterances":true,"language":"en"}';
    SELECT @RecordingId = RecordingId FROM @Result;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 1)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId AND DurationSeconds = 0 AND RecordingStatus = 'completed')
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallRecordings WHERE CallId = @CallId) <> 2
        THROW 51000, 'Completion-first or multiple-recording behavior failed.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId AND ConfigurationVersion=3
        AND ConfigurationJSON=N'{"model":"nova-3","utterances":true,"language":"en"}' AND Status='READY')
        THROW 51000, 'Completion-first must snapshot its explicit configuration version.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, @RecordingSid, 'completed', @Url, 0, 1,
        @TranscriptionConfigurationVersion=4;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId)<>1
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId AND ConfigurationVersion=4)
        THROW 51000, 'Duplicate callback with a changed configuration must not request retranscription.', 1;
    SET @UpdatedAt = (SELECT UpdatedDateTime FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId);
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, @RecordingSid, 'in-progress';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 0 AND IgnoreReason = 'already-terminal')
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId AND RecordingStatus = 'completed' AND UpdatedDateTime = @UpdatedAt)
        THROW 51000, 'Late in-progress callback regressed completion.', 1;

    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, 'REcccccccccccccccccccccccccccccccc', 'absent';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 1)
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE RecordingSid = 'REcccccccccccccccccccccccccccccccc' AND RecordingStatus = 'absent' AND RecordingUrl IS NULL AND DurationSeconds IS NULL)
        THROW 51000, 'Absent callback required unavailable media fields.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, 'REdddddddddddddddddddddddddddddddd', 'failed';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied = 1) THROW 51000, 'Failed callback was not accepted.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, 'REdddddddddddddddddddddddddddddddd', 'completed', @Url, 5, 1;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied=0 AND IgnoreReason='terminal-conflict')
        OR (SELECT COUNT(*) FROM dbo.AutomatedCallTranscriptionJobs j INNER JOIN dbo.AutomatedCallRecordings r ON r.SystemID=j.RecordingId WHERE r.CallId=@CallId)<>2
        THROW 51000, 'Absent, failed and ignored completion callbacks must not enqueue jobs.', 1;

    INSERT dbo.AutomatedCallRecordings (CallId,Provider,AccountSid,RecordingSid,RecordingStatus)
    VALUES (@CallId,'Twilio',@AccountSid,'RE77777777777777777777777777777777','in-progress');
    DECLARE @ExistingJobRecording int=SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId,ConfigurationVersion,ConfigurationJSON,Status,AttemptCount)
    VALUES (@ExistingJobRecording,1,N'{"model":"nova-2","utterances":true}','FAILED',1);
    SET @JobBefore=(SELECT * FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@ExistingJobRecording FOR JSON PATH);
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId,@Sid,@AccountSid,'RE77777777777777777777777777777777','completed',@Url,5,1;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Applied=1)
        OR @JobBefore<>(SELECT * FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@ExistingJobRecording FOR JSON PATH)
        THROW 51000, 'Accepted completion must preserve an existing job snapshot and lifecycle state.', 1;

    DECLARE @EventCount int = (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId);
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, 'REeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 'completed';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-recording') THROW 51000, 'Incomplete completed input was accepted.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, @AccountSid, 'REeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 'in-progress', @DurationSeconds = -1;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-recording') THROW 51000, 'Negative duration was accepted.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, @Sid, 'bad', @RecordingSid, 'in-progress';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-recording') THROW 51000, 'Invalid account SID was accepted.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @CallId, 'CAbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', @AccountSid, @RecordingSid, 'in-progress';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'call-sid-mismatch') THROW 51000, 'Call SID mismatch was accepted.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus -1, @Sid, @AccountSid, @RecordingSid, 'in-progress';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'not-found') THROW 51000, 'Unknown call was accepted.', 1;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> @EventCount
        THROW 51000, 'Rejected callback inserted an event.', 1;

    INSERT INTO dbo.AutomatedCalls (Provider, providerCallId) VALUES ('Twilio', @Sid);
    SET @OtherCallId = SCOPE_IDENTITY();
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @OtherCallId, @Sid, @AccountSid, @RecordingSid, 'in-progress';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'recording-conflict')
       OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @OtherCallId)
        THROW 51000, 'Recording was reassigned to another call.', 1;
    UPDATE dbo.AutomatedCalls SET Provider = 'other' WHERE SystemID = @OtherCallId;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @OtherCallId, @Sid, @AccountSid, @RecordingSid, 'in-progress';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'provider-mismatch') THROW 51000, 'Non-Twilio call was accepted.', 1;
    INSERT INTO dbo.AutomatedCalls (Provider) VALUES ('Twilio');
    SET @UnboundCallId = SCOPE_IDENTITY();
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_RecordingStatus @UnboundCallId, @Sid, @AccountSid, @RecordingSid, 'in-progress';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'call-sid-mismatch')
       OR EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @UnboundCallId AND providerCallId IS NOT NULL)
        THROW 51000, 'Recording callback bound an unbound attempt.', 1;

    DECLARE @CallAfter nvarchar(max) = (SELECT calls.* FROM dbo.AutomatedCalls AS calls WHERE SystemID = @CallId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES),
            @QueueAfter nvarchar(max) = (SELECT queue.* FROM dbo.AutomatedCallQueue AS queue WHERE SystemID = @QueueId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES);
    IF @CallBefore COLLATE Latin1_General_100_BIN2 <> @CallAfter COLLATE Latin1_General_100_BIN2
       OR @QueueBefore COLLATE Latin1_General_100_BIN2 <> @QueueAfter COLLATE Latin1_General_100_BIN2
        THROW 51000, 'Recording callbacks changed call or queue data.', 1;
    ROLLBACK TRANSACTION;
    PRINT 'Recording SQL regression checks passed; test data rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

IF OBJECT_ID('dbo.RecordingStatusRollbackTest', 'TR') IS NOT NULL
    THROW 51000, 'Recording rollback trigger already exists; do not replace it.', 1;

DECLARE @RollbackCallId int, @TriggerCreated bit = 0, @FailureNumber int;
BEGIN TRY
    INSERT INTO dbo.AutomatedCalls (Provider, providerCallId) VALUES ('Twilio', 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
    SET @RollbackCallId = SCOPE_IDENTITY();
    DECLARE @TriggerSQL nvarchar(max) = N'CREATE TRIGGER dbo.RecordingStatusRollbackTest ON dbo.AutomatedCallEvents AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE CallId = '
        + CONVERT(varchar(11), @RollbackCallId) + N') THROW 51001, ''Forced recording event persistence failure.'', 1; END;';
    EXEC(@TriggerSQL);
    SET @TriggerCreated = 1;
    BEGIN TRY
        EXEC dbo.automatedcalls_RecordingStatus @RollbackCallId, 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REffffffffffffffffffffffffffffffff', 'absent';
    END TRY
    BEGIN CATCH
        SET @FailureNumber = ERROR_NUMBER();
    END CATCH;
    IF @FailureNumber IS NULL OR @FailureNumber <> 51001
       OR EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE CallId = @RollbackCallId)
       OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @RollbackCallId)
        THROW 51000, 'Event persistence failure did not roll back recording metadata.', 1;
    DROP TRIGGER dbo.RecordingStatusRollbackTest;
    SET @TriggerCreated = 0;
    DECLARE @ConfigurationCase int=1, @ConfigurationVersion int, @ConfigurationJSON nvarchar(max);
    WHILE @ConfigurationCase<=3
    BEGIN
        SET @ConfigurationVersion=CASE WHEN @ConfigurationCase=1 THEN 0 ELSE 1 END;
        SET @ConfigurationJSON=CASE @ConfigurationCase WHEN 2 THEN N'[]' WHEN 3 THEN N'invalid' ELSE N'{}' END;
        SET @FailureNumber=NULL;
        BEGIN TRY
            EXEC dbo.automatedcalls_RecordingStatus @RollbackCallId,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','REffffffffffffffffffffffffffffffff','completed',
                N'https://api.twilio.com/2010-04-01/Accounts/ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/Recordings/REffffffffffffffffffffffffffffffff',5,1,
                @TranscriptionConfigurationVersion=@ConfigurationVersion,@TranscriptionConfigurationJSON=@ConfigurationJSON;
        END TRY
        BEGIN CATCH
            SET @FailureNumber=ERROR_NUMBER();
        END CATCH;
        IF @FailureNumber IS NULL OR @FailureNumber<>51000
            OR EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE CallId=@RollbackCallId)
            OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId=@RollbackCallId)
            THROW 51000, 'Invalid transcription configuration must roll back recording and event.', 1;
        SET @ConfigurationCase+=1;
    END;
    SET @TriggerSQL=N'CREATE TRIGGER dbo.RecordingStatusRollbackTest ON dbo.AutomatedCallTranscriptionJobs AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted j INNER JOIN dbo.AutomatedCallRecordings r ON r.SystemID=j.RecordingId WHERE r.CallId='
        +CONVERT(varchar(11),@RollbackCallId)+N') THROW 51002, ''Forced transcription enqueue failure.'', 1; END;';
    EXEC(@TriggerSQL);
    SET @TriggerCreated=1;
    SET @FailureNumber=NULL;
    BEGIN TRY
        EXEC dbo.automatedcalls_RecordingStatus @RollbackCallId, 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REffffffffffffffffffffffffffffffff', 'completed',
            N'https://api.twilio.com/2010-04-01/Accounts/ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/Recordings/REffffffffffffffffffffffffffffffff', 5, 1;
    END TRY
    BEGIN CATCH
        SET @FailureNumber=ERROR_NUMBER();
    END CATCH;
    IF @FailureNumber IS NULL OR @FailureNumber<>51002
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE CallId=@RollbackCallId)
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId=@RollbackCallId)
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs j INNER JOIN dbo.AutomatedCallRecordings r ON r.SystemID=j.RecordingId WHERE r.CallId=@RollbackCallId)
        THROW 51000, 'Enqueue failure must roll back recording metadata, event and job together.', 1;
    DROP TRIGGER dbo.RecordingStatusRollbackTest;
    SET @TriggerCreated=0;
    DELETE FROM dbo.AutomatedCalls WHERE SystemID = @RollbackCallId;
    PRINT 'Recording event and transcription enqueue failure rollback checks passed; fixtures cleaned up.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    IF @TriggerCreated = 1 DROP TRIGGER dbo.RecordingStatusRollbackTest;
    DELETE j FROM dbo.AutomatedCallTranscriptionJobs j INNER JOIN dbo.AutomatedCallRecordings r ON r.SystemID=j.RecordingId WHERE r.CallId=@RollbackCallId;
    DELETE FROM dbo.AutomatedCallEvents WHERE CallId = @RollbackCallId;
    DELETE FROM dbo.AutomatedCallRecordings WHERE CallId = @RollbackCallId;
    DELETE FROM dbo.AutomatedCalls WHERE SystemID = @RollbackCallId;
    THROW;
END CATCH;