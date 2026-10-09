SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ANSI_PADDING ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
IF DB_NAME() NOT IN (N'NotificationWorkflowVoiceTests', N'NotificationWorkflowTranscriptionSchemaTests')
    THROW 51000, 'Run callback tests only in an owned scratch database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.AutomatedCalls (Provider, providerCallId, CallStatus)
    VALUES ('Twilio', 'CA11112222333344445555666677778888', 'completed');
    DECLARE @CallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, Channels)
    VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'RE11112222333344445555666677778888', 'completed', 1);
    DECLARE @RecordingId int = SCOPE_IDENTITY(), @OriginalCall nvarchar(max), @OriginalRecording nvarchar(max);
    SET @OriginalCall = (SELECT * FROM dbo.AutomatedCalls WHERE SystemID=@CallId FOR JSON PATH);
    SET @OriginalRecording = (SELECT * FROM dbo.AutomatedCallRecordings WHERE SystemID=@RecordingId FOR JSON PATH);
    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON, AttemptCount)
    VALUES (@RecordingId, 1, N'{"model":"nova-3","utterances":true}', 2);
    DECLARE @Job int = SCOPE_IDENTITY(), @First uniqueidentifier = NEWID(), @Retry uniqueidentifier = NEWID(),
            @Token binary(32) = HASHBYTES('SHA2_256','callback-token'), @Hash binary(32) = HASHBYTES('SHA2_256','body'),
            @OtherHash binary(32) = HASHBYTES('SHA2_256','different'), @WrongToken binary(32) = HASHBYTES('SHA2_256','wrong');
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId, RecordingId, AttemptNumber, CorrelationId, CallbackTokenHash, CallbackDeadlineDateTime)
    VALUES (@Job, @RecordingId, 1, @First, @Token, DATEADD(hour,1,SYSUTCDATETIME())),
           (@Job, @RecordingId, 2, @Retry, @Token, DATEADD(hour,1,SYSUTCDATETIME()));
    DECLARE @FirstId int, @RetryId int;
    DECLARE @Context TABLE
        (SubmissionId int, TranscriptionJobId int, RecordingId int, CallbackTokenHash binary(32), ProviderRequestId varchar(100), ConfigurationJSON nvarchar(max), Channels int);
    SELECT @FirstId = SystemID FROM dbo.AutomatedCallTranscriptionSubmissions WHERE CorrelationId = @First;
    SELECT @RetryId = SystemID FROM dbo.AutomatedCallTranscriptionSubmissions WHERE CorrelationId = @Retry;
    INSERT @Context EXEC dbo.transcription_GetCallbackContext @First;
    IF NOT EXISTS (SELECT 1 FROM @Context WHERE RecordingId=@RecordingId AND Channels=1 AND CallbackTokenHash=@Token)
        THROW 51000, 'Callback context must return persisted recording and token identity.', 1;
    DECLARE @Outcome TABLE (Outcome varchar(40), Applied bit, SubmissionId int);
    DECLARE @Rows nvarchar(max) = N'[{"providerModel":"nova-3","sentence":0,"channel":0,"startTimeMilliseconds":125,"endTimeMilliseconds":1525,"transcriptText":"Hello","confidence":0.95,"wordsJSON":"[]","language":"en"}]';
    INSERT @Outcome EXEC dbo.transcription_CompleteCallback @First,@RecordingId,@WrongToken,'33333333-3333-4333-8333-333333333333',@Hash,1,N'{}',@Rows;
    IF NOT EXISTS (SELECT 1 FROM @Outcome WHERE Outcome='unauthorized') THROW 51000, 'Wrong token must be rejected.', 1;
    DELETE @Outcome;
    DECLARE @WrongRecording int = @RecordingId + 1000000;
    INSERT @Outcome EXEC dbo.transcription_CompleteCallback @First,@WrongRecording,@Token,'33333333-3333-4333-8333-333333333333',@Hash,1,N'{}',@Rows;
    IF NOT EXISTS (SELECT 1 FROM @Outcome WHERE Outcome='unauthorized') THROW 51000, 'Wrong recording must be rejected.', 1;
    DELETE @Outcome;
    INSERT @Outcome EXEC dbo.transcription_CompleteCallback @First,@RecordingId,@Token,'33333333-3333-4333-8333-333333333333',@Hash,1,N'{}',@Rows;
    IF NOT EXISTS (SELECT 1 FROM @Outcome WHERE Outcome='recorded' AND Applied=1) THROW 51000, 'First success must win.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@FirstId AND SubmittedDateTime IS NULL AND Status='COMPLETED')
        THROW 51000, 'Callback before acknowledgement must complete submission.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscripts WHERE SubmissionId=@FirstId AND StartTimeMilliseconds=125 AND EndTimeMilliseconds=1525)
        THROW 51000, 'Normalized transcript must be saved.', 1;
    DECLARE @JobVersion binary(8), @SubmissionVersion binary(8);
    SELECT @JobVersion=Version FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@Job;
    SELECT @SubmissionVersion=Version FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@FirstId;
    DELETE @Outcome;
    INSERT @Outcome EXEC dbo.transcription_CompleteCallback @First,@RecordingId,@Token,'33333333-3333-4333-8333-333333333333',@Hash,1,N'{}',@Rows;
    IF NOT EXISTS (SELECT 1 FROM @Outcome WHERE Outcome='duplicate') THROW 51000, 'Identical retry must be idempotent.', 1;
    DELETE @Outcome;
    INSERT @Outcome EXEC dbo.transcription_CompleteCallback @First,@RecordingId,@Token,'33333333-3333-4333-8333-333333333333',@OtherHash,1,N'{}',@Rows;
    IF NOT EXISTS (SELECT 1 FROM @Outcome WHERE Outcome='payload-conflict') THROW 51000, 'Changed retry must conflict.', 1;
    DELETE @Outcome;
    INSERT @Outcome EXEC dbo.transcription_CompleteCallback @First,@RecordingId,@Token,'44444444-4444-4444-8444-444444444444',@Hash,1,N'{}',@Rows;
    IF NOT EXISTS (SELECT 1 FROM @Outcome WHERE Outcome='request-conflict') THROW 51000, 'Changed provider request must conflict.', 1;
    DELETE @Outcome;
    INSERT @Outcome EXEC dbo.transcription_CompleteCallback @Retry,@RecordingId,@Token,'33333333-3333-4333-8333-333333333333',@Hash,1,N'{}',@Rows;
    IF NOT EXISTS (SELECT 1 FROM @Outcome WHERE Outcome='request-conflict') THROW 51000, 'Provider request cannot bind another submission.', 1;
    DELETE @Outcome;
    INSERT @Outcome EXEC dbo.transcription_CompleteCallback @Retry,@RecordingId,@Token,'55555555-5555-4555-8555-555555555555',@Hash,1,N'{}',@Rows;
    IF NOT EXISTS (SELECT 1 FROM @Outcome WHERE Outcome='recorded' AND Applied=0) OR EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscripts WHERE SubmissionId=@RetryId)
        THROW 51000, 'Losing submission must be saved without displayed transcript rows.', 1;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@Job AND Version<>@JobVersion)
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@FirstId AND Version<>@SubmissionVersion)
        THROW 51000, 'Winning job and submission must remain immutable.', 1;

    DECLARE @Case int=2, @CaseCorrelation uniqueidentifier, @CaseJob int, @CaseSubmission int, @CaseRequest varchar(100), @CaseStatus varchar(20);
    WHILE @Case <= 7
    BEGIN
        SET @CaseStatus = CASE @Case WHEN 2 THEN 'PROCESSING' WHEN 3 THEN 'WAITING_CALLBACK' WHEN 4 THEN 'FAILED' WHEN 5 THEN 'DEAD_LETTER' ELSE 'READY' END;
        INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON, Status, AttemptCount)
        VALUES (@RecordingId,@Case,N'{}',@CaseStatus,CASE WHEN @Case=3 THEN 2 ELSE 1 END);
        SET @CaseJob=SCOPE_IDENTITY(); SET @CaseCorrelation=NEWID(); SET @CaseRequest=CONVERT(varchar(36),NEWID());
        INSERT dbo.AutomatedCallTranscriptionSubmissions
            (TranscriptionJobId, RecordingId, AttemptNumber, CorrelationId, CallbackTokenHash, CallbackDeadlineDateTime, Status)
        VALUES (@CaseJob,@RecordingId,1,@CaseCorrelation,@Token,DATEADD(hour,1,SYSUTCDATETIME()),CASE WHEN @Case=6 THEN 'TIMED_OUT' ELSE 'SUBMITTING' END);
        SET @CaseSubmission=SCOPE_IDENTITY(); DELETE @Outcome;
        DECLARE @Success bit=CASE WHEN @Case IN (2,3) THEN 0 ELSE 1 END;
        INSERT @Outcome EXEC dbo.transcription_CompleteCallback @CaseCorrelation,@RecordingId,@Token,@CaseRequest,@Hash,@Success,N'{}',N'[]','REMOTE_CONTENT_ERROR',N'Deepgram reported a transcription error';
        IF @Case=2 AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@CaseJob AND Status='FAILED')
            THROW 51000, 'Current attempt failure must fail active job.', 1;
        IF @Case=3 AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@CaseJob AND Status='WAITING_CALLBACK')
            THROW 51000, 'Stale failure cannot fail newer attempt.', 1;
        IF @Case IN (4,5) AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@CaseJob AND Status=@CaseStatus AND CompletedSubmissionId IS NULL)
            THROW 51000, 'Terminal job must not be reopened.', 1;
        IF @Case IN (6,7) AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@CaseJob AND Status='COMPLETED' AND CompletedSubmissionId=@CaseSubmission)
            THROW 51000, 'Silence and late success must complete an active job.', 1;
        SET @Case+=1;
    END;
    IF @OriginalCall<>(SELECT * FROM dbo.AutomatedCalls WHERE SystemID=@CallId FOR JSON PATH)
        OR @OriginalRecording<>(SELECT * FROM dbo.AutomatedCallRecordings WHERE SystemID=@RecordingId FOR JSON PATH)
        THROW 51000, 'Callback cannot mutate the call or recording.', 1;
    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON) VALUES (@RecordingId,8,N'{}');
    SET @CaseJob=SCOPE_IDENTITY(); SET @CaseCorrelation=NEWID(); SET @CaseRequest=CONVERT(varchar(36),NEWID());
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId,RecordingId,AttemptNumber,CorrelationId,CallbackTokenHash,CallbackDeadlineDateTime)
    VALUES (@CaseJob,@RecordingId,1,@CaseCorrelation,@Token,DATEADD(hour,1,SYSUTCDATETIME()));
    BEGIN TRY
        EXEC dbo.transcription_CompleteCallback @CaseCorrelation,@RecordingId,@Token,@CaseRequest,@Hash,1,N'{}',
            N'[{"sentence":0,"channel":0,"startTimeMilliseconds":0,"endTimeMilliseconds":1,"wordsJSON":"invalid"}]';
        THROW 51000, 'Expected transcript constraint failure.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER()<>547 THROW;
    END CATCH;
    IF @@TRANCOUNT<>0 OR EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@CallId)
        THROW 51000, 'Failed transcript write must roll back every transaction mutation.', 1;
    PRINT 'Callback SQL checks passed: authentication, early callback, duplicates, conflicts, winner fencing, stale/terminal policy, silence, late success, rollback; fixtures removed.';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;