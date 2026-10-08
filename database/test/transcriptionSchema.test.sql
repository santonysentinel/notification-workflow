SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT OFF;

IF DB_NAME() NOT IN (N'NotificationWorkflowVoiceTests', N'NotificationWorkflowTranscriptionSchemaTests')
    THROW 51000, 'Run transcription schema tests only in an owned scratch database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.AutomatedCalls (Provider, providerCallId, CallStatus)
    VALUES ('Twilio', 'CAbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'completed');
    DECLARE @CallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls (Provider, providerCallId, CallStatus)
    VALUES ('Twilio', 'CAdddddddddddddddddddddddddddddddd', 'completed');
    DECLARE @OtherCallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus)
    VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REcccccccccccccccccccccccccccccccc', 'completed');
    DECLARE @RecordingId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus)
    VALUES (@OtherCallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 'completed');
    DECLARE @OtherRecordingId int = SCOPE_IDENTITY();

    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON)
    VALUES (@RecordingId, 1, N'{"model":"nova-3","utterances":true,"multichannel":true}');
    DECLARE @JobId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON)
    VALUES (@RecordingId, 2, N'{"model":"nova-3","language":"en"}');
    DECLARE @OtherJobId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId, RecordingId, AttemptNumber, CallbackTokenHash, CallbackDeadlineDateTime)
    VALUES (@JobId, @RecordingId, 1, HASHBYTES('SHA2_256', 'test-only-token'), DATEADD(hour, 1, SYSUTCDATETIME()));
    DECLARE @SubmissionId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId, RecordingId, AttemptNumber, CallbackTokenHash, CallbackDeadlineDateTime)
    VALUES (@JobId, @RecordingId, 2, HASHBYTES('SHA2_256', 'retry-test-token'), DATEADD(hour, 1, SYSUTCDATETIME()));
    DECLARE @RetryId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId, RecordingId, AttemptNumber, CallbackTokenHash, CallbackDeadlineDateTime)
    VALUES (@OtherJobId, @RecordingId, 1, HASHBYTES('SHA2_256', 'other-job-token'), DATEADD(hour, 1, SYSUTCDATETIME()));
    DECLARE @OtherSubmissionId int = SCOPE_IDENTITY();

    UPDATE dbo.AutomatedCallTranscriptionSubmissions SET Status = 'COMPLETED',
        ProviderRequestId = '00000000-0000-4000-8000-000000000001',
        CallbackReceivedDateTime = SYSUTCDATETIME(), ResponseJSON = N'{"metadata":{"request_id":"00000000-0000-4000-8000-000000000001"},"results":{}}'
    WHERE SystemID = @SubmissionId;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions
        WHERE SystemID = @SubmissionId AND SubmittedDateTime IS NULL AND CallbackReceivedDateTime IS NOT NULL)
        THROW 51000, 'Callback-before-acknowledgement must be representable.', 1;

    INSERT dbo.AutomatedCallTranscripts
        (CallId, RecordingId, SubmissionId, Sentence, AudioChannel, StartTimeMilliseconds, EndTimeMilliseconds, Confidence, WordsJSON, TranscriptText)
    VALUES (@CallId, @RecordingId, @SubmissionId, 0, 0, 125, 1525, 0.95, N'[{"word":"hello","start":0.125}]', N'Hello');
    INSERT dbo.AutomatedCallTranscripts
        (CallId, RecordingId, SubmissionId, Sentence, AudioChannel, StartTimeMilliseconds, EndTimeMilliseconds)
    VALUES (@CallId, @RecordingId, @SubmissionId, 0, 1, 125, 1525);
    INSERT dbo.AutomatedCallTranscripts
        (CallId, RecordingId, SubmissionId, Sentence, AudioChannel, StartTimeMilliseconds, EndTimeMilliseconds)
    VALUES (@CallId, @RecordingId, @RetryId, 0, 0, 0, 100);
    UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = 'COMPLETED',
        CompletedSubmissionId = @SubmissionId, CompletedDateTime = SYSUTCDATETIME() WHERE SystemID = @JobId;

    DECLARE @Cases TABLE (SystemID int IDENTITY PRIMARY KEY, Statement nvarchar(max));
    INSERT @Cases (Statement) VALUES
      (N'INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON) VALUES (@recording,1,N''{}'');'),
      (N'INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON) VALUES (@recording,0,N''{}'');'),
      (N'INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON) VALUES (@recording,3,N''bad JSON'');'),
      (N'INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, Provider, ConfigurationVersion, ConfigurationJSON) VALUES (@recording,''Other'',3,N''{}'');'),
      (N'UPDATE dbo.AutomatedCallTranscriptionJobs SET AttemptCount = -1 WHERE SystemID = @job;'),
      (N'UPDATE dbo.AutomatedCallTranscriptionJobs SET AttemptCount = MaxAttempts + 1 WHERE SystemID = @job;'),
      (N'UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = ''bogus'' WHERE SystemID = @job;'),
      (N'UPDATE dbo.AutomatedCallTranscriptionJobs SET CompletedSubmissionId = @otherSubmission WHERE SystemID = @job;'),
      (N'UPDATE dbo.AutomatedCallTranscriptionJobs SET CompletedSubmissionId = NULL WHERE SystemID = @job;'),
      (N'INSERT dbo.AutomatedCallTranscriptionSubmissions (TranscriptionJobId, RecordingId, AttemptNumber, CallbackTokenHash, CallbackDeadlineDateTime) VALUES (@job,@recording,1,0x01,DATEADD(hour,1,SYSUTCDATETIME()));'),
      (N'INSERT dbo.AutomatedCallTranscriptionSubmissions (TranscriptionJobId, RecordingId, AttemptNumber, CallbackTokenHash, CallbackDeadlineDateTime) VALUES (@job,@otherRecording,3,0x01,DATEADD(hour,1,SYSUTCDATETIME()));'),
      (N'UPDATE dbo.AutomatedCallTranscriptionSubmissions SET CorrelationId = (SELECT CorrelationId FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@submission) WHERE SystemID=@retry;'),
      (N'UPDATE dbo.AutomatedCallTranscriptionSubmissions SET ProviderRequestId = ''00000000-0000-4000-8000-000000000001'' WHERE SystemID = @retry;'),
      (N'UPDATE dbo.AutomatedCallTranscriptionSubmissions SET CallbackTokenHash = NULL WHERE SystemID = @retry;'),
      (N'UPDATE dbo.AutomatedCallTranscriptionSubmissions SET CallbackDeadlineDateTime = CreatedDateTime WHERE SystemID = @retry;'),
      (N'UPDATE dbo.AutomatedCallTranscriptionSubmissions SET ResponseJSON = N''invalid'' WHERE SystemID = @retry;'),
      (N'UPDATE dbo.AutomatedCallTranscriptionSubmissions SET Status = ''bogus'' WHERE SystemID = @retry;'),
      (N'INSERT dbo.AutomatedCallTranscripts (CallId,RecordingId,SubmissionId,Sentence,AudioChannel,StartTimeMilliseconds,EndTimeMilliseconds) VALUES (@call,@recording,@submission,0,0,0,10);'),
      (N'UPDATE dbo.AutomatedCallTranscripts SET RecordingId = @otherRecording WHERE SubmissionId = @submission;'),
      (N'UPDATE dbo.AutomatedCallTranscripts SET CallId = @otherCall WHERE SubmissionId = @submission;'),
      (N'UPDATE dbo.AutomatedCallTranscripts SET RecordingId = NULL WHERE SubmissionId = @submission;'),
      (N'UPDATE dbo.AutomatedCallTranscripts SET StartTimeMilliseconds = -1 WHERE SubmissionId = @submission;'),
      (N'UPDATE dbo.AutomatedCallTranscripts SET EndTimeMilliseconds = 1 WHERE SubmissionId = @submission;'),
      (N'UPDATE dbo.AutomatedCallTranscripts SET AudioChannel = NULL WHERE SubmissionId = @submission;'),
      (N'UPDATE dbo.AutomatedCallTranscripts SET Confidence = 1.5 WHERE SubmissionId = @submission;'),
      (N'UPDATE dbo.AutomatedCallTranscripts SET WordsJSON = N''invalid'' WHERE SubmissionId = @submission;');
    DECLARE @Case int = 1, @Count int = (SELECT COUNT(*) FROM @Cases), @Statement nvarchar(max);
    WHILE @Case <= @Count
    BEGIN
        SELECT @Statement = Statement FROM @Cases WHERE SystemID = @Case;
        BEGIN TRY
            EXEC sys.sp_executesql @Statement,
                N'@recording int,@otherRecording int,@job int,@submission int,@retry int,@otherSubmission int,@call int,@otherCall int',
                @RecordingId,@OtherRecordingId,@JobId,@SubmissionId,@RetryId,@OtherSubmissionId,@CallId,@OtherCallId;
            THROW 51000, 'Expected schema constraint rejection did not occur.', 1;
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (547, 515, 2601, 2627) THROW;
        END CATCH;
        SET @Case += 1;
    END;
    INSERT dbo.AutomatedCallTranscripts (Provider, CallId, StartTime, EndTime, Confidence, WordsJSON)
    VALUES (N'legacy', @CallId, 12, 15, 2.0, N'legacy format');
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscripts WHERE Provider=N'legacy'
        AND SubmissionId IS NULL AND RecordingId IS NULL AND StartTime=12 AND EndTime=15)
        THROW 51000, 'Legacy transcript writes must remain compatible.', 1;
    ROLLBACK TRANSACTION;
    PRINT 'Transcription schema checks passed: early callback, retry identities, ownership, timing, legacy compatibility; fixtures rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;