SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ANSI_PADDING ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
IF COL_LENGTH('dbo.AutomatedCallTranscriptionSubmissions', 'CallbackPayloadHash') IS NULL
    ALTER TABLE dbo.AutomatedCallTranscriptionSubmissions ADD CallbackPayloadHash binary(32) NULL;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_GetCallbackContext
    @CorrelationId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT s.SystemID AS SubmissionId, s.TranscriptionJobId, s.RecordingId,
           s.CallbackTokenHash, s.ProviderRequestId, j.ConfigurationJSON, r.Channels
    FROM dbo.AutomatedCallTranscriptionSubmissions s
    INNER JOIN dbo.AutomatedCallTranscriptionJobs j ON j.SystemID = s.TranscriptionJobId
    INNER JOIN dbo.AutomatedCallRecordings r ON r.SystemID = s.RecordingId
    WHERE s.CorrelationId = @CorrelationId AND j.Provider = 'Deepgram';
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_CompleteCallback
    @CorrelationId uniqueidentifier,
    @RecordingId int,
    @TokenHash binary(32),
    @RequestId varchar(100),
    @PayloadHash binary(32),
    @Success bit,
    @ResponseJSON nvarchar(max),
    @TranscriptsJSON nvarchar(max),
    @ErrorCode varchar(100) = NULL,
    @ErrorMessage nvarchar(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET ANSI_WARNINGS ON;
    SET ARITHABORT ON;
    SET CONCAT_NULL_YIELDS_NULL ON;
    SET NUMERIC_ROUNDABORT OFF;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @JobId int, @SubmissionId int, @JobStatus varchar(20), @Winner int,
                @Attempt int, @AttemptCount int, @StoredRequestId varchar(100), @StoredHash binary(32),
                @StoredToken binary(32), @StoredRecording int, @CallId int, @Now datetime2(3) = SYSUTCDATETIME();
        SELECT @JobId = TranscriptionJobId FROM dbo.AutomatedCallTranscriptionSubmissions WHERE CorrelationId = @CorrelationId;
        SELECT @JobStatus = Status, @Winner = CompletedSubmissionId, @AttemptCount = AttemptCount
        FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK,HOLDLOCK) WHERE SystemID = @JobId AND Provider = 'Deepgram';
        SELECT @SubmissionId = SystemID, @StoredRequestId = ProviderRequestId, @StoredHash = CallbackPayloadHash,
               @StoredToken = CallbackTokenHash, @StoredRecording = RecordingId, @Attempt = AttemptNumber
        FROM dbo.AutomatedCallTranscriptionSubmissions WITH (UPDLOCK,HOLDLOCK)
        WHERE CorrelationId = @CorrelationId AND TranscriptionJobId = @JobId;
        IF @SubmissionId IS NULL OR @JobStatus IS NULL OR @StoredToken <> @TokenHash OR @TokenHash IS NULL
            OR @RecordingId IS NULL OR @StoredRecording <> @RecordingId
        BEGIN
            COMMIT;
            SELECT 'unauthorized' AS Outcome, CAST(0 AS bit) AS Applied, CAST(NULL AS int) AS SubmissionId;
            RETURN;
        END;
        IF @RequestId IS NULL OR DATALENGTH(@RequestId) <> 36 OR TRY_CONVERT(uniqueidentifier,@RequestId) IS NULL
            OR @PayloadHash IS NULL OR @Success IS NULL OR ISJSON(@ResponseJSON) <> 1 OR @ResponseJSON IS NULL
            OR ISJSON(@TranscriptsJSON) <> 1 OR @TranscriptsJSON IS NULL OR LEFT(LTRIM(@TranscriptsJSON),1) <> '['
            THROW 51000, 'Invalid callback data.', 1;
        IF @StoredRequestId IS NOT NULL AND (@StoredRequestId COLLATE Latin1_General_100_BIN2 <> @RequestId COLLATE Latin1_General_100_BIN2
            OR DATALENGTH(@StoredRequestId) <> DATALENGTH(@RequestId))
        BEGIN
            COMMIT;
            SELECT 'request-conflict' AS Outcome, CAST(0 AS bit) AS Applied, @SubmissionId AS SubmissionId;
            RETURN;
        END;
        IF @StoredHash IS NOT NULL
        BEGIN
            COMMIT;
                 SELECT CASE WHEN @StoredHash = @PayloadHash THEN 'duplicate' ELSE 'payload-conflict' END AS Outcome,
                     CAST(0 AS bit) AS Applied, @SubmissionId AS SubmissionId;
            RETURN;
        END;
        IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WITH (UPDLOCK,HOLDLOCK)
                   WHERE ProviderRequestId = @RequestId AND SystemID <> @SubmissionId)
        BEGIN
            COMMIT;
            SELECT 'request-conflict' AS Outcome, CAST(0 AS bit) AS Applied, @SubmissionId AS SubmissionId;
            RETURN;
        END;
        SELECT @CallId = CallId FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId;
        DECLARE @Applied bit = CASE WHEN @Success = 1 AND @Winner IS NULL AND @JobStatus IN ('READY','PROCESSING','WAITING_CALLBACK') THEN 1 ELSE 0 END;
        UPDATE dbo.AutomatedCallTranscriptionSubmissions SET
            ProviderRequestId = @RequestId, CallbackPayloadHash = @PayloadHash,
            CallbackReceivedDateTime = @Now, ResponseJSON = @ResponseJSON,
            Status = CASE WHEN @Success = 1 THEN 'COMPLETED' ELSE 'FAILED' END,
            LastErrorCode = CASE WHEN @Success = 0 THEN @ErrorCode ELSE NULL END,
            LastErrorMessage = CASE WHEN @Success = 0 THEN @ErrorMessage ELSE NULL END, UpdatedDateTime = @Now
        WHERE SystemID = @SubmissionId;
        IF @Applied = 1
        BEGIN
            INSERT dbo.AutomatedCallTranscripts
                (Provider,ProviderModel,CallId,RecordingId,SubmissionId,Sentence,AudioChannel,
                 StartTimeMilliseconds,EndTimeMilliseconds,TranscriptText,Confidence,WordsJSON,Language)
            SELECT N'Deepgram',providerModel,@CallId,@RecordingId,@SubmissionId,sentence,channel,
                   startTimeMilliseconds,endTimeMilliseconds,transcriptText,confidence,wordsJSON,language
            FROM OPENJSON(@TranscriptsJSON) WITH
                (providerModel nvarchar(200),sentence int,channel int,startTimeMilliseconds int,endTimeMilliseconds int,
                 transcriptText nvarchar(max),confidence decimal(6,5),wordsJSON nvarchar(max),language nvarchar(35));
            UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = 'COMPLETED', CompletedSubmissionId = @SubmissionId,
                CompletedDateTime = @Now, LockedBy = NULL, LockedUntil = NULL, LeaseToken = NULL,
                LastErrorCode = NULL, LastErrorMessage = NULL, UpdatedDateTime = @Now WHERE SystemID = @JobId;
        END
        ELSE IF @Success = 0 AND @Winner IS NULL AND @Attempt = @AttemptCount AND @JobStatus IN ('PROCESSING','WAITING_CALLBACK')
        BEGIN
            UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = 'FAILED', LockedBy = NULL, LockedUntil = NULL,
                LeaseToken = NULL, LastErrorCode = @ErrorCode, LastErrorMessage = @ErrorMessage, UpdatedDateTime = @Now
            WHERE SystemID = @JobId;
        END;
        COMMIT;
        SELECT 'recorded' AS Outcome, @Applied AS Applied, @SubmissionId AS SubmissionId;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO