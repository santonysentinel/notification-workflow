SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ANSI_PADDING ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
IF DB_NAME() NOT IN (N'NotificationWorkflowVoiceTests', N'NotificationWorkflowTranscriptionSchemaTests')
    THROW 51000, 'Run transcription lifecycle tests only in an owned scratch database.', 1;
IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE Status IN ('READY','PROCESSING','WAITING_CALLBACK'))
    THROW 51000, 'Lifecycle tests require an idle owned transcription queue.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.AutomatedCalls (Provider, providerCallId, CallStatus)
        VALUES ('Twilio', 'CA88889999000011112222333344445555', 'completed');
    DECLARE @CallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, DurationSeconds, Channels)
        VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'RE88889999000011112222333344445555', 'completed', 10, 2);
    DECLARE @RecordingId int = SCOPE_IDENTITY(), @OriginalRecording nvarchar(max), @OriginalCall nvarchar(max);
    SET @OriginalRecording = (SELECT * FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId FOR JSON PATH);
    SET @OriginalCall = (SELECT * FROM dbo.AutomatedCalls WHERE SystemID = @CallId FOR JSON PATH);
    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON)
        VALUES (@RecordingId, 1, N'{"model":"nova-3","utterances":true,"multichannel":true}');
    DECLARE @JobId int = SCOPE_IDENTITY(), @Lease uniqueidentifier, @OldLease uniqueidentifier,
        @Correlation uniqueidentifier = NEWID(), @Identity uniqueidentifier = NEWID(), @SubmissionId int,
        @Token binary(32) = HASHBYTES('SHA2_256', 'lifecycle-token'), @Hash binary(32) = HASHBYTES('SHA2_256', 'lifecycle-body'),
        @Expiry datetime2(3) = DATEADD(hour, 2, SYSUTCDATETIME()), @Request varchar(100) = CONVERT(varchar(36), NEWID());
    DECLARE @Claims TABLE (SystemID int, RecordingId int, ConfigurationVersion int, ConfigurationJSON nvarchar(max),
        AttemptCount int, MaxAttempts int, CurrentSubmissionId int, LockedBy varchar(100), LockedUntil datetime2(3), LeaseToken uniqueidentifier);
    DECLARE @Created TABLE (Outcome varchar(40), SubmissionId int, AttemptNumber int);
    DECLARE @Applied TABLE (Applied bit);
    DECLARE @Send TABLE (CanSend bit);
    DECLARE @Accepted TABLE (Outcome varchar(40));
    DECLARE @Callback TABLE (Outcome varchar(40), Applied bit, SubmissionId int);
    DECLARE @Recovery TABLE (JobId int, Status varchar(20));
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-first', 1, 300;
    SELECT @Lease = LeaseToken FROM @Claims WHERE SystemID = @JobId;
    IF @Lease IS NULL OR NOT EXISTS (SELECT 1 FROM @Claims WHERE AttemptCount = 0)
        THROW 51000, 'Claim must fence ownership without counting an attempt.', 1;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-second', 1, 300;
    IF EXISTS (SELECT 1 FROM @Claims) THROW 51000, 'An active lease cannot be claimed again.', 1;
    INSERT @Applied EXEC dbo.transcription_RenewLease @JobId, 'lifecycle-first', @Lease, 300;
    IF NOT EXISTS (SELECT 1 FROM @Applied WHERE Applied = 1) THROW 51000, 'Current lease must renew.', 1;
    DELETE @Applied;
    INSERT @Applied EXEC dbo.transcription_RenewLease @JobId, 'lifecycle-first ', @Lease, 300;
    IF EXISTS (SELECT 1 FROM @Applied WHERE Applied = 1) THROW 51000, 'Owner comparison must be exact.', 1;
    INSERT @Created EXEC dbo.transcription_CreateSubmission @JobId, 'lifecycle-first', @Lease, @Correlation, @Identity, @Token, @Expiry;
    SELECT @SubmissionId = SubmissionId FROM @Created WHERE Outcome = 'created' AND AttemptNumber = 1;
    IF @SubmissionId IS NULL THROW 51000, 'First submission must allocate immutable identity.', 1;
    DELETE @Created;
    INSERT @Created EXEC dbo.transcription_CreateSubmission @JobId, 'lifecycle-first', @Lease, @Correlation, @Identity, @Token, @Expiry;
    IF NOT EXISTS (SELECT 1 FROM @Created WHERE Outcome = 'duplicate' AND SubmissionId = @SubmissionId)
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId AND AttemptCount = 1)
        THROW 51000, 'Submission creation replay must not consume another attempt.', 1;
    INSERT @Send EXEC dbo.transcription_BeginSubmission @JobId, 'lifecycle-first', @Lease, @Correlation, @Identity;
    IF NOT EXISTS (SELECT 1 FROM @Send WHERE CanSend = 1) THROW 51000, 'Only the first durable marker may authorize sending.', 1;
    DELETE @Send;
    INSERT @Send EXEC dbo.transcription_BeginSubmission @JobId, 'lifecycle-first', @Lease, @Correlation, @Identity;
    IF EXISTS (SELECT 1 FROM @Send WHERE CanSend = 1) THROW 51000, 'Marker replay must never authorize another POST.', 1;
    DECLARE @Rows nvarchar(max) = N'[{"providerModel":"nova-3","sentence":0,"channel":0,"startTimeMilliseconds":0,"endTimeMilliseconds":1000,"transcriptText":"Hello","wordsJSON":"[]"}]';
    INSERT @Callback EXEC dbo.transcription_CompleteCallback @Correlation, @RecordingId, @Token, @Request, @Hash, 1, N'{}', @Rows;
    IF NOT EXISTS (SELECT 1 FROM @Callback WHERE Applied = 1) THROW 51000, 'Early callback must win before acknowledgement.', 1;
    DECLARE @WinnerVersion binary(8), @CallbackDeadline datetime2(3);
    SELECT @WinnerVersion = Version FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId;
    SELECT @CallbackDeadline = CallbackDeadlineDateTime FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID = @SubmissionId;
    INSERT @Accepted EXEC dbo.transcription_SaveAcceptance @JobId, @Correlation, @Identity, @Request;
    IF NOT EXISTS (SELECT 1 FROM @Accepted WHERE Outcome = 'saved')
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID = @SubmissionId
            AND Status = 'COMPLETED' AND SubmittedDateTime IS NOT NULL AND CallbackDeadlineDateTime = @CallbackDeadline)
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId AND Version <> @WinnerVersion)
        THROW 51000, 'Late acknowledgement must preserve early callback and winning job.', 1;
    DELETE @Accepted;
    INSERT @Accepted EXEC dbo.transcription_SaveAcceptance @JobId, @Correlation, @Identity, @Request;
    IF NOT EXISTS (SELECT 1 FROM @Accepted WHERE Outcome = 'duplicate') THROW 51000, 'Acknowledgement replay must be idempotent.', 1;
    DECLARE @WrongRequest varchar(100) = CONVERT(varchar(36), NEWID());
    DELETE @Accepted;
    INSERT @Accepted EXEC dbo.transcription_SaveAcceptance @JobId, @Correlation, @Identity, @WrongRequest;
    IF NOT EXISTS (SELECT 1 FROM @Accepted WHERE Outcome = 'request-conflict') THROW 51000, 'Request identity cannot change.', 1;

    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON, MaxAttempts)
        VALUES (@RecordingId, 2, N'{}', 2);
    SET @JobId = SCOPE_IDENTITY(); SET @Correlation = NEWID(); SET @Identity = NEWID(); SET @Request = CONVERT(varchar(36), NEWID());
    DELETE @Claims;
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-first', 1, 300;
    SELECT @Lease = LeaseToken FROM @Claims WHERE SystemID = @JobId;
    SET @OldLease = @Lease;
    UPDATE dbo.AutomatedCallTranscriptionJobs SET LockedUntil = DATEADD(second, -1, SYSUTCDATETIME()) WHERE SystemID = @JobId;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-second', 1, 300;
    SELECT @Lease = LeaseToken FROM @Claims WHERE SystemID = @JobId;
    IF @Lease = @OldLease OR NOT EXISTS (SELECT 1 FROM @Claims WHERE AttemptCount = 0)
        THROW 51000, 'Expired reclaim must replace the fence without spending a submission.', 1;
    DELETE @Applied;
    INSERT @Applied EXEC dbo.transcription_RenewLease @JobId, 'lifecycle-first', @OldLease, 300;
    IF EXISTS (SELECT 1 FROM @Applied WHERE Applied = 1) THROW 51000, 'Stale renewal must be rejected.', 1;
    DELETE @Created;
    INSERT @Created EXEC dbo.transcription_CreateSubmission @JobId, 'lifecycle-first', @OldLease, @Correlation, @Identity, @Token, @Expiry;
    IF NOT EXISTS (SELECT 1 FROM @Created WHERE Outcome = 'stale-lease') THROW 51000, 'Stale creation must be rejected.', 1;
    DELETE @Created;
    INSERT @Created EXEC dbo.transcription_CreateSubmission @JobId, 'lifecycle-second', @Lease, @Correlation, @Identity, @Token, @Expiry;
    SELECT @SubmissionId = SubmissionId FROM @Created;
    DELETE @Send;
    INSERT @Send EXEC dbo.transcription_BeginSubmission @JobId, 'lifecycle-first', @OldLease, @Correlation, @Identity;
    IF EXISTS (SELECT 1 FROM @Send WHERE CanSend = 1) THROW 51000, 'Stale send marker must be rejected.', 1;
    DELETE @Send;
    INSERT @Send EXEC dbo.transcription_BeginSubmission @JobId, 'lifecycle-second', @Lease, @Correlation, @Identity;
    DELETE @Callback;
    INSERT @Callback EXEC dbo.transcription_CompleteCallback @Correlation, @RecordingId, @Token, @Request, @Hash, 0, N'{}', N'[]', 'REMOTE_CONTENT_ERROR', N'Provider reported a media error';
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId AND Status = 'READY'
        AND AvailableAt BETWEEN DATEADD(second, 15, UpdatedDateTime) AND DATEADD(second, 30, UpdatedDateTime)
        AND AttemptCount = 1 AND LeaseToken IS NULL)
        THROW 51000, 'Retryable callback must schedule bounded backoff and release ownership.', 1;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-first', 1, 300;
    IF EXISTS (SELECT 1 FROM @Claims) THROW 51000, 'A retry is not eligible before AvailableAt.', 1;
    UPDATE dbo.AutomatedCallTranscriptionJobs SET AvailableAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE SystemID = @JobId;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-first', 1, 300;
    SELECT @Lease = LeaseToken FROM @Claims WHERE SystemID = @JobId;
    SET @Correlation = NEWID(); SET @Identity = NEWID(); SET @Request = CONVERT(varchar(36), NEWID());
    DELETE @Created;
    INSERT @Created EXEC dbo.transcription_CreateSubmission @JobId, 'lifecycle-first', @Lease, @Correlation, @Identity, @Token, @Expiry;
    SELECT @SubmissionId = SubmissionId FROM @Created WHERE AttemptNumber = 2;
    DELETE @Send;
    INSERT @Send EXEC dbo.transcription_BeginSubmission @JobId, 'lifecycle-first', @Lease, @Correlation, @Identity;
    SET @OldLease = @Lease;
    UPDATE dbo.AutomatedCallTranscriptionJobs SET LockedUntil = DATEADD(second, -1, SYSUTCDATETIME()) WHERE SystemID = @JobId;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-second', 1, 300;
    SELECT @Lease = LeaseToken FROM @Claims WHERE SystemID = @JobId;
    DECLARE @Replacement uniqueidentifier = NEWID(), @ReplacementIdentity uniqueidentifier = NEWID();
    DELETE @Created;
    INSERT @Created EXEC dbo.transcription_CreateSubmission @JobId, 'lifecycle-second', @Lease, @Replacement, @ReplacementIdentity, @Token, @Expiry;
    IF NOT EXISTS (SELECT 1 FROM @Created WHERE Outcome = 'pending-submission')
        THROW 51000, 'Reclaim alone must not replace a potentially accepted submission.', 1;
    DELETE @Applied;
    INSERT @Applied EXEC dbo.transcription_ScheduleRetry @JobId, 'lifecycle-first', @OldLease, @SubmissionId, 'WAIT', 'AMBIGUOUS';
    IF EXISTS (SELECT 1 FROM @Applied WHERE Applied = 1) THROW 51000, 'Stale disposition must be rejected.', 1;
    DELETE @Applied;
    INSERT @Applied EXEC dbo.transcription_ScheduleRetry @JobId, 'lifecycle-second', @Lease, @SubmissionId, 'WAIT', 'AMBIGUOUS';
    IF NOT EXISTS (SELECT 1 FROM @Applied WHERE Applied = 1)
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId AND Status = 'WAITING_CALLBACK')
        THROW 51000, 'Current owner may release uncertain acceptance into callback waiting.', 1;
    UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = 'PROCESSING', LockedBy = 'lifecycle-second', LeaseToken = @Lease,
        LockedUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE SystemID = @JobId;
    UPDATE dbo.AutomatedCallTranscriptionJobs SET LockedUntil = DATEADD(second, -1, SYSUTCDATETIME()) WHERE SystemID = @JobId;
    INSERT @Recovery EXEC dbo.transcription_RecoverTimeouts;
    IF NOT EXISTS (SELECT 1 FROM @Recovery WHERE JobId = @JobId AND Status = 'WAITING_CALLBACK')
        THROW 51000, 'Unknown outcome must await its persisted deadline even at the attempt limit.', 1;
    DELETE @Recovery;
    INSERT @Recovery EXEC dbo.transcription_RecoverTimeouts;
    IF EXISTS (SELECT 1 FROM @Recovery) THROW 51000, 'Recovery must not sweep a waiting submission early.', 1;
    UPDATE dbo.AutomatedCallTranscriptionSubmissions SET CreatedDateTime = DATEADD(hour, -2, SYSUTCDATETIME()),
        CallbackDeadlineDateTime = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE SystemID = @SubmissionId;
    UPDATE dbo.AutomatedCallTranscriptionJobs SET AvailableAt = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE SystemID = @JobId;
    INSERT @Recovery EXEC dbo.transcription_RecoverTimeouts;
    IF NOT EXISTS (SELECT 1 FROM @Recovery WHERE JobId = @JobId AND Status = 'DEAD_LETTER')
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID = @SubmissionId AND Status = 'TIMED_OUT')
        THROW 51000, 'Timed-out final submission must dead-letter without consuming another attempt.', 1;
    UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = 'READY', AvailableAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE SystemID = @JobId;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-first', 1, 300;
    SELECT @Lease = LeaseToken FROM @Claims WHERE SystemID = @JobId;
    DELETE @Created;
    INSERT @Created EXEC dbo.transcription_CreateSubmission @JobId, 'lifecycle-first', @Lease, @Replacement, @ReplacementIdentity, @Token, @Expiry;
    IF NOT EXISTS (SELECT 1 FROM @Created WHERE Outcome = 'exhausted')
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId AND AttemptCount = 2 AND Status = 'DEAD_LETTER')
        THROW 51000, 'Attempt limit must prevent a third submission.', 1;
    DELETE @Accepted;
    INSERT @Accepted EXEC dbo.transcription_SaveAcceptance @JobId, @Correlation, @Identity, @Request;
    IF NOT EXISTS (SELECT 1 FROM @Accepted WHERE Outcome = 'saved')
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId AND Status = 'DEAD_LETTER')
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID = @SubmissionId AND Status = 'TIMED_OUT' AND ProviderRequestId = @Request)
        THROW 51000, 'Late acceptance must persist identity without reopening timed-out work.', 1;
    DELETE @Callback;
    INSERT @Callback EXEC dbo.transcription_CompleteCallback @Correlation, @RecordingId, @Token, @Request, @Hash, 1, N'{}', N'[]';
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId AND Status = 'COMPLETED'
        AND CompletedSubmissionId = @SubmissionId AND CompletionOrigin = 'PROVIDER')
        THROW 51000, 'Valid late success must complete a dead-lettered job.', 1;

    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON)
        VALUES (@RecordingId, 3, N'{}');
    SET @JobId = SCOPE_IDENTITY(); SET @Correlation = NEWID(); SET @Identity = NEWID();
    DELETE @Claims;
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-first', 1, 300;
    SELECT @Lease = LeaseToken FROM @Claims WHERE SystemID = @JobId;
    DELETE @Created;
    INSERT @Created EXEC dbo.transcription_CreateSubmission @JobId, 'lifecycle-first', @Lease, @Correlation, @Identity, @Token, @Expiry;
    DECLARE @UnsentId int = (SELECT SubmissionId FROM @Created);
    SET @Replacement = NEWID(); SET @ReplacementIdentity = NEWID();
    DELETE @Created;
    INSERT @Created EXEC dbo.transcription_CreateSubmission @JobId, 'lifecycle-first', @Lease, @Replacement, @ReplacementIdentity, @Token, @Expiry;
    IF NOT EXISTS (SELECT 1 FROM @Created WHERE AttemptNumber = 2 AND Outcome = 'created')
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID = @UnsentId
            AND Status = 'TIMED_OUT' AND SendStartedDateTime IS NULL AND LastErrorCode = 'UNSENT_REPLACED')
        THROW 51000, 'Provably unsent replacement must preserve history and use a new submission.', 1;
    DELETE @Send;
    INSERT @Send EXEC dbo.transcription_BeginSubmission @JobId, 'lifecycle-first', @Lease, @Correlation, @Identity;
    IF EXISTS (SELECT 1 FROM @Send WHERE CanSend = 1) THROW 51000, 'A replaced unsent identity cannot acquire a send marker.', 1;
    DELETE @Applied;
    SELECT @SubmissionId = SubmissionId FROM @Created;
    INSERT @Applied EXEC dbo.transcription_ScheduleRetry @JobId, 'lifecycle-first', @Lease, @SubmissionId, 'FAIL', 'INVALID_AUDIO';
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId AND Status = 'FAILED')
        THROW 51000, 'Permanent worker failure must not schedule automatic retry.', 1;

    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON, CreatedDateTime)
        VALUES (@RecordingId, 4, N'{}', DATEADD(hour, -25, SYSUTCDATETIME()));
    SET @JobId = SCOPE_IDENTITY();
    DELETE @Recovery;
    INSERT @Recovery EXEC dbo.transcription_RecoverTimeouts;
    IF NOT EXISTS (SELECT 1 FROM @Recovery WHERE JobId = @JobId AND Status = 'DEAD_LETTER')
        THROW 51000, 'Age budget must recover stranded preparation jobs with no submissions.', 1;
    IF @OriginalRecording <> (SELECT * FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId FOR JSON PATH)
        OR @OriginalCall <> (SELECT * FROM dbo.AutomatedCalls WHERE SystemID = @CallId FOR JSON PATH)
        THROW 51000, 'Transcription lifecycle must not mutate call or recording state.', 1;

    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, DurationSeconds, Channels)
        VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'RE99998888000011112222333344445555', 'completed', 0, 1);
    SET @RecordingId = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON) VALUES (@RecordingId, 1, N'{}');
    SET @JobId = SCOPE_IDENTITY();
    DELETE @Claims;
    INSERT @Claims EXEC dbo.transcription_ClaimJobs 'lifecycle-first', 1, 300;
    SELECT @Lease = LeaseToken FROM @Claims WHERE SystemID = @JobId;
    DELETE @Applied;
    INSERT @Applied EXEC dbo.transcription_CompleteEmpty @JobId, 'lifecycle-first', @Lease;
    IF NOT EXISTS (SELECT 1 FROM @Applied WHERE Applied = 1)
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId AND Status = 'COMPLETED'
            AND CompletionOrigin = 'EMPTY' AND CompletedSubmissionId IS NULL AND AttemptCount = 0)
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WHERE TranscriptionJobId = @JobId)
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscripts WHERE RecordingId = @RecordingId)
        THROW 51000, 'Empty recording must complete locally without a fabricated provider submission.', 1;
    ROLLBACK;
    BEGIN TRANSACTION;
    INSERT dbo.AutomatedCalls (Provider, providerCallId, CallStatus)
        VALUES ('Twilio', 'CA88889999000011112222333344445555', 'completed');
    SET @CallId = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, DurationSeconds, Channels)
        VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'RE88889999000011112222333344445555', 'completed', 10, 1);
    SET @RecordingId = SCOPE_IDENTITY(); SET @Lease = NEWID();
    INSERT dbo.AutomatedCallTranscriptionJobs
        (RecordingId, ConfigurationVersion, ConfigurationJSON, Status, AttemptCount, LockedBy, LeaseToken, LockedUntil)
        VALUES (@RecordingId, 1, N'{}', 'PROCESSING', 1, 'unknown-outcome', @Lease, DATEADD(minute, 5, SYSUTCDATETIME()));
    SET @JobId = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId, RecordingId, AttemptNumber, CallbackTokenHash, Status, SendStartedDateTime, CallbackDeadlineDateTime)
        VALUES (@JobId, @RecordingId, 1, @Token, 'SUBMITTING', SYSUTCDATETIME(), DATEADD(hour, 1, SYSUTCDATETIME()));
    SET @SubmissionId = SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCallTranscriptionJobs SET CurrentSubmissionId = @SubmissionId WHERE SystemID = @JobId;
    BEGIN TRY
        EXEC dbo.transcription_ScheduleRetry @JobId, 'unknown-outcome', @Lease, @SubmissionId, 'RETRY', 'TRANSPORT_TIMEOUT';
        THROW 51000, 'Expected unsafe retry rejection.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 OR ERROR_MESSAGE() NOT LIKE 'Unknown acceptance must wait%' THROW;
    END CATCH;
    IF @@TRANCOUNT <> 0 OR EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId)
        THROW 51000, 'Unsafe disposition rejection must roll back its transaction.', 1;
    PRINT 'Transcription lifecycle SQL checks passed: claims, fences, marker replay, early callback, late acknowledgement, retries, deadlines, budgets, unsent replacement, empty completion; fixtures rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK;
    THROW;
END CATCH;