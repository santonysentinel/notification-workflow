SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
GO
BEGIN TRY
    BEGIN TRANSACTION;
    IF COL_LENGTH('dbo.AutomatedCallTranscriptionJobs', 'CurrentSubmissionId') IS NULL
    BEGIN
        ALTER TABLE dbo.AutomatedCallTranscriptionJobs ADD CurrentSubmissionId int NULL;
        EXEC sys.sp_executesql N'
        IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions GROUP BY TranscriptionJobId
                   HAVING COUNT(*) <> MAX(AttemptNumber))
            THROW 51000, ''Transcription attempt history must be contiguous before migration.'', 1;
        IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs j
                   CROSS APPLY (SELECT COUNT(*) AS Total FROM dbo.AutomatedCallTranscriptionSubmissions
                                WHERE TranscriptionJobId = j.SystemID) history WHERE history.Total > j.MaxAttempts)
            THROW 51000, ''Transcription history exceeds the configured attempt budget.'', 1;
        UPDATE j SET AttemptCount = history.Total, CurrentSubmissionId = latest.SystemID
        FROM dbo.AutomatedCallTranscriptionJobs j
        CROSS APPLY (SELECT COUNT(*) AS Total FROM dbo.AutomatedCallTranscriptionSubmissions
                     WHERE TranscriptionJobId = j.SystemID) history
        OUTER APPLY (SELECT TOP (1) SystemID FROM dbo.AutomatedCallTranscriptionSubmissions
                     WHERE TranscriptionJobId = j.SystemID ORDER BY AttemptNumber DESC) latest;';
    END;
    IF COL_LENGTH('dbo.AutomatedCallTranscriptionJobs', 'CompletionOrigin') IS NULL
    BEGIN
        ALTER TABLE dbo.AutomatedCallTranscriptionJobs ADD CompletionOrigin varchar(20) COLLATE Latin1_General_100_BIN2 NULL;
        EXEC sys.sp_executesql N'UPDATE dbo.AutomatedCallTranscriptionJobs SET CompletionOrigin = ''PROVIDER'' WHERE Status = ''COMPLETED'';';
    END;
    IF COL_LENGTH('dbo.AutomatedCallTranscriptionJobs', 'MaxJobAgeMinutes') IS NULL
        ALTER TABLE dbo.AutomatedCallTranscriptionJobs ADD
            MaxJobAgeMinutes int NOT NULL CONSTRAINT DF_TranscriptionJobs_MaxAge DEFAULT (1440) WITH VALUES,
            CallbackWaitMinutes int NOT NULL CONSTRAINT DF_TranscriptionJobs_CallbackWait DEFAULT (60) WITH VALUES,
            RetryBaseSeconds int NOT NULL CONSTRAINT DF_TranscriptionJobs_RetryBase DEFAULT (30) WITH VALUES,
            RetryMaxSeconds int NOT NULL CONSTRAINT DF_TranscriptionJobs_RetryMax DEFAULT (900) WITH VALUES;
    IF COL_LENGTH('dbo.AutomatedCallTranscriptionSubmissions', 'SubmissionToken') IS NULL
        ALTER TABLE dbo.AutomatedCallTranscriptionSubmissions ADD
            SubmissionToken uniqueidentifier NOT NULL CONSTRAINT DF_TranscriptionSubmissions_Token DEFAULT (NEWID()) WITH VALUES,
            CreatedLeaseToken uniqueidentifier NULL,
            SendStartedDateTime datetime2(3) NULL,
            CallbackTokenExpiresAt datetime2(3) NULL;
    EXEC sys.sp_executesql N'
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N''FK_TranscriptionJobs_CurrentSubmission'')
        ALTER TABLE dbo.AutomatedCallTranscriptionJobs ADD CONSTRAINT FK_TranscriptionJobs_CurrentSubmission
            FOREIGN KEY (SystemID, CurrentSubmissionId) REFERENCES dbo.AutomatedCallTranscriptionSubmissions (TranscriptionJobId, SystemID);
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N''CK_TranscriptionJobs_Policy'')
        ALTER TABLE dbo.AutomatedCallTranscriptionJobs ADD CONSTRAINT CK_TranscriptionJobs_Policy CHECK
            (MaxJobAgeMinutes BETWEEN 1 AND 10080 AND CallbackWaitMinutes BETWEEN 1 AND 1440
             AND RetryBaseSeconds BETWEEN 1 AND 900 AND RetryMaxSeconds BETWEEN RetryBaseSeconds AND 86400);
    ALTER TABLE dbo.AutomatedCallTranscriptionJobs DROP CONSTRAINT CK_TranscriptionJobs_Completion;
    ALTER TABLE dbo.AutomatedCallTranscriptionJobs ADD CONSTRAINT CK_TranscriptionJobs_Completion CHECK
        ((Status = ''COMPLETED'' AND CompletedDateTime IS NOT NULL
          AND ((CompletedSubmissionId IS NOT NULL AND (CompletionOrigin IS NULL OR CompletionOrigin = ''PROVIDER''))
               OR (CompletedSubmissionId IS NULL AND CompletionOrigin IS NOT NULL AND CompletionOrigin = ''EMPTY'')))
         OR (Status <> ''COMPLETED'' AND CompletedSubmissionId IS NULL AND CompletedDateTime IS NULL AND CompletionOrigin IS NULL));
    ALTER TABLE dbo.AutomatedCallTranscriptionSubmissions DROP CONSTRAINT CK_TranscriptionSubmissions_Status;
    ALTER TABLE dbo.AutomatedCallTranscriptionSubmissions ADD CONSTRAINT CK_TranscriptionSubmissions_Status CHECK
        (Status IN (''PREPARED'',''SUBMITTING'',''ACCEPTED'',''COMPLETED'',''FAILED'',''TIMED_OUT''));
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N''dbo.AutomatedCallTranscriptionJobs'') AND name = N''IX_TranscriptionJobs_Waiting'')
        CREATE INDEX IX_TranscriptionJobs_Waiting ON dbo.AutomatedCallTranscriptionJobs (AvailableAt, SystemID)
            INCLUDE (CurrentSubmissionId) WHERE Status = ''WAITING_CALLBACK'';';
    COMMIT;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK;
    THROW;
END CATCH;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_ClaimJobs
    @WorkerId varchar(100),
    @BatchSize int = 1,
    @LeaseSeconds int = 300
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NULLIF(LTRIM(RTRIM(@WorkerId)), '') IS NULL OR @BatchSize IS NULL OR @BatchSize NOT BETWEEN 1 AND 100
        OR @LeaseSeconds IS NULL OR @LeaseSeconds NOT BETWEEN 10 AND 3600
        THROW 51000, 'Invalid transcription claim settings.', 1;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    ;WITH candidates AS
    (
        SELECT TOP (@BatchSize) * FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE (Status = 'READY' AND AvailableAt <= @Now)
           OR (Status = 'PROCESSING' AND (LockedUntil IS NULL OR LockedUntil <= @Now))
        ORDER BY AvailableAt, SystemID
    )
    UPDATE candidates SET Status = 'PROCESSING', LockedBy = @WorkerId,
        LockedUntil = DATEADD(second, @LeaseSeconds, @Now), LeaseToken = NEWID(), UpdatedDateTime = @Now
    OUTPUT inserted.SystemID, inserted.RecordingId, inserted.ConfigurationVersion, inserted.ConfigurationJSON,
        inserted.AttemptCount, inserted.MaxAttempts, inserted.CurrentSubmissionId,
        inserted.LockedBy, inserted.LockedUntil, inserted.LeaseToken;
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_RenewLease
    @JobId int, @WorkerId varchar(100), @LeaseToken uniqueidentifier, @LeaseSeconds int = 300
AS
BEGIN
    SET NOCOUNT ON;
    IF @LeaseSeconds IS NULL OR @LeaseSeconds NOT BETWEEN 10 AND 3600
        THROW 51000, 'Invalid transcription lease duration.', 1;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @LockedJobId int;
        SELECT @LockedJobId = SystemID FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK, HOLDLOCK) WHERE SystemID = @JobId;
        DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Applied bit = 0;
        UPDATE dbo.AutomatedCallTranscriptionJobs SET LockedUntil = DATEADD(second, @LeaseSeconds, @Now), UpdatedDateTime = @Now
        WHERE SystemID = @LockedJobId AND Status = 'PROCESSING' AND LockedBy COLLATE Latin1_General_100_BIN2 = @WorkerId COLLATE Latin1_General_100_BIN2
            AND DATALENGTH(LockedBy) = DATALENGTH(@WorkerId) AND LeaseToken = @LeaseToken AND LockedUntil > @Now;
        IF @@ROWCOUNT = 1 SET @Applied = 1;
        COMMIT;
        SELECT @Applied AS Applied;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_GetWorkContext
    @JobId int, @WorkerId varchar(100), @LeaseToken uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT j.SystemID AS JobId, j.RecordingId, j.ConfigurationVersion, j.ConfigurationJSON,
        j.AttemptCount, j.MaxAttempts, j.MaxJobAgeMinutes, j.CallbackWaitMinutes,
        j.RetryBaseSeconds, j.RetryMaxSeconds, j.CurrentSubmissionId, j.CreatedDateTime,
        r.CallId, r.Provider, r.AccountSid, r.RecordingSid, r.RecordingStatus, r.RecordingUrl,
        r.DurationSeconds, r.Channels, r.RecordingSource, r.RecordingTrack,
        s.CorrelationId, s.SubmissionToken, s.CreatedLeaseToken, s.Status AS SubmissionStatus,
        s.ProviderRequestId, s.SendStartedDateTime, s.SubmittedDateTime,
        s.CallbackDeadlineDateTime, s.CallbackTokenExpiresAt
    FROM dbo.AutomatedCallTranscriptionJobs j
    INNER JOIN dbo.AutomatedCallRecordings r ON r.SystemID = j.RecordingId
    LEFT JOIN dbo.AutomatedCallTranscriptionSubmissions s ON s.SystemID = j.CurrentSubmissionId
    WHERE j.SystemID = @JobId AND j.Status = 'PROCESSING' AND j.LeaseToken = @LeaseToken
        AND j.LockedBy COLLATE Latin1_General_100_BIN2 = @WorkerId COLLATE Latin1_General_100_BIN2
        AND DATALENGTH(j.LockedBy) = DATALENGTH(@WorkerId) AND j.LockedUntil > SYSUTCDATETIME();
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_CreateSubmission
    @JobId int, @WorkerId varchar(100), @LeaseToken uniqueidentifier,
    @CorrelationId uniqueidentifier, @SubmissionToken uniqueidentifier,
    @CallbackTokenHash binary(32), @CallbackTokenExpiresAt datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @CorrelationId IS NULL OR @SubmissionToken IS NULL OR @CallbackTokenHash IS NULL OR @CallbackTokenExpiresAt IS NULL
        THROW 51000, 'Submission identity and credential expiry are required.', 1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Current int, @RecordingId int, @Attempts int,
            @Maximum int, @Wait int, @Status varchar(20), @Until datetime2(3), @Owner varchar(100), @Fence uniqueidentifier,
            @Created datetime2(3), @MaxAge int, @SubmissionId int, @CurrentStatus varchar(20);
        SELECT @Current = CurrentSubmissionId, @RecordingId = RecordingId, @Attempts = AttemptCount,
            @Maximum = MaxAttempts, @Wait = CallbackWaitMinutes, @Status = Status, @Until = LockedUntil,
            @Owner = LockedBy, @Fence = LeaseToken, @Created = CreatedDateTime, @MaxAge = MaxJobAgeMinutes
        FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK, HOLDLOCK) WHERE SystemID = @JobId;
        SET @Now = SYSUTCDATETIME();
        IF @Status IS NULL OR @Status <> 'PROCESSING' OR @Until IS NULL OR @Until <= @Now
            OR @WorkerId IS NULL OR @Owner IS NULL OR @Owner COLLATE Latin1_General_100_BIN2 <> @WorkerId COLLATE Latin1_General_100_BIN2
            OR DATALENGTH(@Owner) <> DATALENGTH(@WorkerId) OR @LeaseToken IS NULL OR @Fence IS NULL OR @Fence <> @LeaseToken
        BEGIN
            COMMIT;
            SELECT 'stale-lease' AS Outcome, CAST(NULL AS int) AS SubmissionId, CAST(NULL AS int) AS AttemptNumber;
            RETURN;
        END;
        SELECT @SubmissionId = SystemID FROM dbo.AutomatedCallTranscriptionSubmissions WITH (UPDLOCK, HOLDLOCK)
        WHERE CorrelationId = @CorrelationId AND TranscriptionJobId = @JobId AND SubmissionToken = @SubmissionToken
            AND CallbackTokenHash = @CallbackTokenHash AND CallbackTokenExpiresAt = @CallbackTokenExpiresAt
            AND CreatedLeaseToken = @LeaseToken;
        IF @SubmissionId IS NOT NULL
        BEGIN
            COMMIT;
            SELECT 'duplicate' AS Outcome, @SubmissionId AS SubmissionId,
                AttemptNumber FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID = @SubmissionId;
            RETURN;
        END;
        IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WHERE CorrelationId = @CorrelationId)
            THROW 51000, 'Submission correlation identity conflicts.', 1;
        SELECT @CurrentStatus = Status FROM dbo.AutomatedCallTranscriptionSubmissions WITH (UPDLOCK, HOLDLOCK) WHERE SystemID = @Current;
        IF @CurrentStatus IN ('SUBMITTING','ACCEPTED')
        BEGIN
            COMMIT;
            SELECT 'pending-submission' AS Outcome, @Current AS SubmissionId, @Attempts AS AttemptNumber;
            RETURN;
        END;
        IF @Attempts >= @Maximum OR DATEADD(minute, @MaxAge, @Created) <= @Now
        BEGIN
            UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = 'DEAD_LETTER', LockedBy = NULL, LockedUntil = NULL,
                LeaseToken = NULL, LastErrorCode = 'RETRY_BUDGET_EXHAUSTED', UpdatedDateTime = @Now WHERE SystemID = @JobId;
            COMMIT;
            SELECT 'exhausted' AS Outcome, CAST(NULL AS int) AS SubmissionId, @Attempts AS AttemptNumber;
            RETURN;
        END;
        IF @CallbackTokenExpiresAt <= DATEADD(minute, @Wait, @Now)
            THROW 51000, 'Callback credential must outlive the callback wait.', 1;
        IF @CurrentStatus = 'PREPARED'
            UPDATE dbo.AutomatedCallTranscriptionSubmissions SET Status = 'TIMED_OUT',
                LastErrorCode = 'UNSENT_REPLACED', UpdatedDateTime = @Now WHERE SystemID = @Current;
        INSERT dbo.AutomatedCallTranscriptionSubmissions
            (TranscriptionJobId, RecordingId, AttemptNumber, CorrelationId, SubmissionToken,
             CreatedLeaseToken, CallbackTokenHash, CallbackTokenExpiresAt, Status, CallbackDeadlineDateTime, CreatedDateTime)
        VALUES (@JobId, @RecordingId, @Attempts + 1, @CorrelationId, @SubmissionToken,
            @LeaseToken, @CallbackTokenHash, @CallbackTokenExpiresAt, 'PREPARED', DATEADD(minute, @Wait, @Now), @Now);
        SET @SubmissionId = SCOPE_IDENTITY();
        UPDATE dbo.AutomatedCallTranscriptionJobs SET CurrentSubmissionId = @SubmissionId, AttemptCount = @Attempts + 1,
            LastErrorCode = NULL, LastErrorMessage = NULL, UpdatedDateTime = @Now WHERE SystemID = @JobId;
        COMMIT;
        SELECT 'created' AS Outcome, @SubmissionId AS SubmissionId, @Attempts + 1 AS AttemptNumber;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_BeginSubmission
    @JobId int, @WorkerId varchar(100), @LeaseToken uniqueidentifier,
    @CorrelationId uniqueidentifier, @SubmissionToken uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Current int, @Wait int, @Created datetime2(3), @MaxAge int, @Until datetime2(3);
        SELECT @Current = CurrentSubmissionId, @Wait = CallbackWaitMinutes, @Created = CreatedDateTime, @MaxAge = MaxJobAgeMinutes, @Until = LockedUntil
        FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK, HOLDLOCK)
        WHERE SystemID = @JobId AND Status = 'PROCESSING' AND LeaseToken = @LeaseToken AND LockedUntil > @Now
            AND LockedBy COLLATE Latin1_General_100_BIN2 = @WorkerId COLLATE Latin1_General_100_BIN2
            AND DATALENGTH(LockedBy) = DATALENGTH(@WorkerId);
        DECLARE @CanSend bit = 0;
        SET @Now = SYSUTCDATETIME();
        IF @Current IS NOT NULL AND @Until > @Now AND DATEADD(minute, @MaxAge, @Created) > @Now
        BEGIN
            UPDATE dbo.AutomatedCallTranscriptionSubmissions SET Status = 'SUBMITTING', SendStartedDateTime = @Now,
                CallbackDeadlineDateTime = DATEADD(minute, @Wait, @Now), UpdatedDateTime = @Now
            WHERE SystemID = @Current AND TranscriptionJobId = @JobId AND CorrelationId = @CorrelationId
                AND SubmissionToken = @SubmissionToken AND CreatedLeaseToken = @LeaseToken AND Status = 'PREPARED'
                AND CallbackTokenExpiresAt > DATEADD(minute, @Wait, @Now) AND CallbackPayloadHash IS NULL;
            IF @@ROWCOUNT = 1
            BEGIN
                SET @CanSend = 1;
                UPDATE dbo.AutomatedCallTranscriptionJobs SET AvailableAt = DATEADD(minute, @Wait, @Now),
                    UpdatedDateTime = @Now WHERE SystemID = @JobId;
            END;
        END;
        COMMIT;
        SELECT @CanSend AS CanSend;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_SaveAcceptance
    @JobId int, @CorrelationId uniqueidentifier, @SubmissionToken uniqueidentifier, @RequestId varchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @RequestId IS NULL OR DATALENGTH(@RequestId) <> 36 OR TRY_CONVERT(uniqueidentifier, @RequestId) IS NULL
        THROW 51000, 'Invalid Deepgram request identity.', 1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Current int, @Wait int, @JobStatus varchar(20),
            @SubmissionId int, @Stored varchar(100), @Status varchar(20), @Submitted datetime2(3), @Started datetime2(3);
        SELECT @Current = CurrentSubmissionId, @Wait = CallbackWaitMinutes, @JobStatus = Status
        FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK, HOLDLOCK) WHERE SystemID = @JobId;
        SELECT @SubmissionId = SystemID, @Stored = ProviderRequestId, @Status = Status,
            @Submitted = SubmittedDateTime, @Started = SendStartedDateTime
        FROM dbo.AutomatedCallTranscriptionSubmissions WITH (UPDLOCK, HOLDLOCK)
        WHERE TranscriptionJobId = @JobId AND CorrelationId = @CorrelationId AND SubmissionToken = @SubmissionToken;
        SET @Now = SYSUTCDATETIME();
        DECLARE @Outcome varchar(40) = 'stale-submission';
        IF @SubmissionId IS NOT NULL AND @Started IS NOT NULL
        BEGIN
            IF (@Stored IS NOT NULL AND (@Stored COLLATE Latin1_General_100_BIN2 <> @RequestId COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@Stored) <> DATALENGTH(@RequestId)))
                OR EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WITH (UPDLOCK, HOLDLOCK)
                           WHERE ProviderRequestId = @RequestId AND SystemID <> @SubmissionId)
                SET @Outcome = 'request-conflict';
            ELSE IF @Submitted IS NOT NULL SET @Outcome = 'duplicate';
            ELSE
            BEGIN
                UPDATE dbo.AutomatedCallTranscriptionSubmissions SET ProviderRequestId = @RequestId, SubmittedDateTime = @Now,
                    Status = CASE WHEN @Status = 'SUBMITTING' THEN 'ACCEPTED' ELSE Status END,
                    CallbackDeadlineDateTime = CASE WHEN @Status = 'SUBMITTING' AND @Current = @SubmissionId
                        AND @JobStatus IN ('PROCESSING','WAITING_CALLBACK') THEN DATEADD(minute, @Wait, @Now) ELSE CallbackDeadlineDateTime END,
                    UpdatedDateTime = @Now WHERE SystemID = @SubmissionId;
                IF @Current = @SubmissionId AND @Status = 'SUBMITTING' AND @JobStatus IN ('PROCESSING','WAITING_CALLBACK')
                    UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = 'WAITING_CALLBACK', AvailableAt = DATEADD(minute, @Wait, @Now),
                        LockedBy = NULL, LockedUntil = NULL, LeaseToken = NULL, UpdatedDateTime = @Now WHERE SystemID = @JobId;
                SET @Outcome = 'saved';
            END;
        END;
        COMMIT;
        SELECT @Outcome AS Outcome;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_ScheduleRetry
    @JobId int, @WorkerId varchar(100), @LeaseToken uniqueidentifier,
    @SubmissionId int = NULL, @Disposition varchar(10) = 'RETRY',
    @ErrorCode varchar(100) = NULL, @ErrorMessage nvarchar(2000) = NULL,
    @DefiniteRejection bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Disposition IS NULL OR @Disposition COLLATE Latin1_General_100_BIN2 NOT IN ('RETRY','FAIL','WAIT')
        THROW 51000, 'Invalid transcription disposition.', 1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Current int, @Attempts int, @Maximum int, @Created datetime2(3),
            @MaxAge int, @Base int, @Cap int, @Deadline datetime2(3), @SubmissionStatus varchar(20), @Budget bit, @Until datetime2(3);
        SELECT @Current = CurrentSubmissionId, @Attempts = AttemptCount, @Maximum = MaxAttempts,
            @Created = CreatedDateTime, @MaxAge = MaxJobAgeMinutes, @Base = RetryBaseSeconds, @Cap = RetryMaxSeconds, @Until = LockedUntil
        FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK, HOLDLOCK)
        WHERE SystemID = @JobId AND Status = 'PROCESSING' AND LeaseToken = @LeaseToken AND LockedUntil > @Now
            AND LockedBy COLLATE Latin1_General_100_BIN2 = @WorkerId COLLATE Latin1_General_100_BIN2
            AND DATALENGTH(LockedBy) = DATALENGTH(@WorkerId);
        SET @Now = SYSUTCDATETIME();
        IF @Attempts IS NULL OR @Until IS NULL OR @Until <= @Now OR (@Current IS NULL AND @SubmissionId IS NOT NULL)
            OR (@Current IS NOT NULL AND (@SubmissionId IS NULL OR @Current <> @SubmissionId))
        BEGIN
            COMMIT;
            SELECT CAST(0 AS bit) AS Applied;
            RETURN;
        END;
        SELECT @Deadline = CallbackDeadlineDateTime, @SubmissionStatus = Status
        FROM dbo.AutomatedCallTranscriptionSubmissions WITH (UPDLOCK, HOLDLOCK) WHERE SystemID = @Current;
        IF @SubmissionStatus = 'SUBMITTING' AND @Disposition <> 'WAIT' AND ISNULL(@DefiniteRejection, 0) <> 1
            THROW 51000, 'Unknown acceptance must wait for callback recovery; only definite rejection permits immediate disposition.', 1;
        IF @SubmissionStatus = 'ACCEPTED' AND @Disposition <> 'WAIT'
            THROW 51000, 'Accepted submissions must await callback recovery.', 1;
        IF @Disposition = 'WAIT' AND (@SubmissionStatus IS NULL OR @SubmissionStatus NOT IN ('SUBMITTING','ACCEPTED'))
            THROW 51000, 'Only potentially accepted submissions can wait for a callback.', 1;
        SET @Budget = CASE WHEN @Attempts >= @Maximum OR DATEADD(minute, @MaxAge, @Created) <= @Now THEN 0 ELSE 1 END;
        DECLARE @Ceiling int = CONVERT(int, CASE WHEN @Base * POWER(CONVERT(float, 2), CASE WHEN @Attempts > 20 THEN 20 WHEN @Attempts > 0 THEN @Attempts - 1 ELSE 0 END) > @Cap
            THEN @Cap ELSE @Base * POWER(CONVERT(float, 2), CASE WHEN @Attempts > 20 THEN 20 WHEN @Attempts > 0 THEN @Attempts - 1 ELSE 0 END) END);
        DECLARE @Delay int = @Ceiling / 2 + (CHECKSUM(NEWID()) & 2147483647) % (@Ceiling - @Ceiling / 2 + 1);
        IF @Disposition <> 'WAIT' AND @SubmissionStatus IN ('PREPARED','SUBMITTING')
            UPDATE dbo.AutomatedCallTranscriptionSubmissions SET Status = 'FAILED', LastErrorCode = @ErrorCode,
                LastErrorMessage = @ErrorMessage, UpdatedDateTime = @Now WHERE SystemID = @Current AND CallbackPayloadHash IS NULL;
        UPDATE dbo.AutomatedCallTranscriptionJobs SET
            Status = CASE WHEN @Disposition = 'WAIT' THEN 'WAITING_CALLBACK' WHEN @Disposition = 'FAIL' THEN 'FAILED'
                          WHEN @Budget = 0 THEN 'DEAD_LETTER' ELSE 'READY' END,
            AvailableAt = CASE WHEN @Disposition = 'WAIT' THEN @Deadline ELSE DATEADD(second, @Delay, @Now) END,
            LockedBy = NULL, LockedUntil = NULL, LeaseToken = NULL,
            LastErrorCode = @ErrorCode, LastErrorMessage = @ErrorMessage, UpdatedDateTime = @Now WHERE SystemID = @JobId;
        COMMIT;
        SELECT CAST(1 AS bit) AS Applied;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_RecoverTimeouts
    @BatchSize int = 100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @BatchSize IS NULL OR @BatchSize NOT BETWEEN 1 AND 1000 THROW 51000, 'Invalid transcription recovery batch.', 1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Now datetime2(3) = SYSUTCDATETIME();
        DECLARE @Jobs TABLE (JobId int PRIMARY KEY);
        INSERT @Jobs SELECT TOP (@BatchSize) SystemID FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE (Status = 'WAITING_CALLBACK' AND (AvailableAt <= @Now OR DATEADD(minute, MaxJobAgeMinutes, CreatedDateTime) <= @Now))
           OR (Status = 'PROCESSING' AND (LockedUntil IS NULL OR LockedUntil <= @Now))
           OR (Status = 'READY' AND (AttemptCount >= MaxAttempts OR DATEADD(minute, MaxJobAgeMinutes, CreatedDateTime) <= @Now))
        ORDER BY AvailableAt, SystemID;
        DECLARE @Results TABLE (JobId int, Status varchar(20));
        DECLARE @JobId int, @Current int, @Attempts int, @Maximum int, @Created datetime2(3), @MaxAge int,
            @Base int, @Cap int, @Deadline datetime2(3), @SubmissionStatus varchar(20), @NextStatus varchar(20);
        WHILE EXISTS (SELECT 1 FROM @Jobs)
        BEGIN
            SELECT @JobId = MIN(JobId) FROM @Jobs;
            SELECT @Current = CurrentSubmissionId, @Attempts = AttemptCount, @Maximum = MaxAttempts,
                @Created = CreatedDateTime, @MaxAge = MaxJobAgeMinutes, @Base = RetryBaseSeconds, @Cap = RetryMaxSeconds
            FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID = @JobId;
            SET @Deadline = NULL; SET @SubmissionStatus = NULL;
            SELECT @Deadline = CallbackDeadlineDateTime, @SubmissionStatus = Status
            FROM dbo.AutomatedCallTranscriptionSubmissions WITH (UPDLOCK, HOLDLOCK) WHERE SystemID = @Current;
            SET @NextStatus = CASE WHEN DATEADD(minute, @MaxAge, @Created) <= @Now THEN 'DEAD_LETTER'
                WHEN @SubmissionStatus IN ('SUBMITTING','ACCEPTED') AND @Deadline > @Now THEN 'WAITING_CALLBACK'
                WHEN @Attempts >= @Maximum THEN 'DEAD_LETTER' ELSE 'READY' END;
            IF @SubmissionStatus = 'PREPARED' OR (@SubmissionStatus IN ('SUBMITTING','ACCEPTED') AND (@Deadline <= @Now OR @NextStatus = 'DEAD_LETTER'))
                UPDATE dbo.AutomatedCallTranscriptionSubmissions SET Status = 'TIMED_OUT', LastErrorCode = 'RECOVERY_TIMEOUT',
                    UpdatedDateTime = @Now WHERE SystemID = @Current AND CallbackPayloadHash IS NULL;
            DECLARE @Ceiling int = CONVERT(int, CASE WHEN @Base * POWER(CONVERT(float, 2), CASE WHEN @Attempts > 20 THEN 20 WHEN @Attempts > 0 THEN @Attempts - 1 ELSE 0 END) > @Cap
                THEN @Cap ELSE @Base * POWER(CONVERT(float, 2), CASE WHEN @Attempts > 20 THEN 20 WHEN @Attempts > 0 THEN @Attempts - 1 ELSE 0 END) END);
            DECLARE @Delay int = @Ceiling / 2 + (CHECKSUM(NEWID()) & 2147483647) % (@Ceiling - @Ceiling / 2 + 1);
            UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = @NextStatus,
                AvailableAt = CASE WHEN @NextStatus = 'WAITING_CALLBACK' THEN @Deadline ELSE DATEADD(second, @Delay, @Now) END,
                LockedBy = NULL, LockedUntil = NULL, LeaseToken = NULL, LastErrorCode = 'RECOVERY_TIMEOUT', UpdatedDateTime = @Now
            WHERE SystemID = @JobId;
            INSERT @Results VALUES (@JobId, @NextStatus);
            DELETE @Jobs WHERE JobId = @JobId;
        END;
        COMMIT;
        SELECT JobId, Status FROM @Results;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_CompleteEmpty
    @JobId int, @WorkerId varchar(100), @LeaseToken uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Current int, @RecordingId int, @Until datetime2(3);
        SELECT @Current = CurrentSubmissionId, @RecordingId = RecordingId, @Until = LockedUntil
        FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK, HOLDLOCK)
        WHERE SystemID = @JobId AND Status = 'PROCESSING' AND LeaseToken = @LeaseToken AND LockedUntil > @Now
            AND LockedBy COLLATE Latin1_General_100_BIN2 = @WorkerId COLLATE Latin1_General_100_BIN2
            AND DATALENGTH(LockedBy) = DATALENGTH(@WorkerId);
        DECLARE @Applied bit = 0;
        SET @Now = SYSUTCDATETIME();
        IF @RecordingId IS NOT NULL AND @Until > @Now AND EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE SystemID = @RecordingId
            AND RecordingStatus = 'completed' AND DurationSeconds = 0)
            AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionSubmissions WITH (UPDLOCK, HOLDLOCK)
                            WHERE TranscriptionJobId = @JobId AND (SendStartedDateTime IS NOT NULL OR Status <> 'PREPARED'))
        BEGIN
            UPDATE dbo.AutomatedCallTranscriptionSubmissions SET Status = 'TIMED_OUT', LastErrorCode = 'EMPTY_RECORDING',
                UpdatedDateTime = @Now WHERE SystemID = @Current AND Status = 'PREPARED';
            UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = 'COMPLETED', CompletionOrigin = 'EMPTY',
                CompletedDateTime = @Now, LockedBy = NULL, LockedUntil = NULL, LeaseToken = NULL,
                LastErrorCode = NULL, LastErrorMessage = NULL, UpdatedDateTime = @Now WHERE SystemID = @JobId;
            SET @Applied = 1;
        END;
        COMMIT;
        SELECT @Applied AS Applied;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
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
        DECLARE @JobId int, @SubmissionId int, @JobStatus varchar(20), @Winner int, @Current int,
            @Attempt int, @AttemptCount int, @Maximum int, @Created datetime2(3), @MaxAge int, @Base int, @Cap int,
            @StoredRequestId varchar(100), @StoredHash binary(32), @StoredToken binary(32), @StoredRecording int,
            @CallId int, @Now datetime2(3) = SYSUTCDATETIME();
        SELECT @JobId = TranscriptionJobId FROM dbo.AutomatedCallTranscriptionSubmissions WHERE CorrelationId = @CorrelationId;
        SELECT @JobStatus = Status, @Winner = CompletedSubmissionId, @Current = CurrentSubmissionId, @AttemptCount = AttemptCount,
            @Maximum = MaxAttempts, @Created = CreatedDateTime, @MaxAge = MaxJobAgeMinutes, @Base = RetryBaseSeconds, @Cap = RetryMaxSeconds
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
        DECLARE @Applied bit = CASE WHEN @Success = 1 AND @Winner IS NULL
            AND @JobStatus IN ('READY','PROCESSING','WAITING_CALLBACK','DEAD_LETTER') THEN 1 ELSE 0 END;
        UPDATE dbo.AutomatedCallTranscriptionSubmissions SET ProviderRequestId = @RequestId,
            CallbackPayloadHash = @PayloadHash, CallbackReceivedDateTime = @Now, ResponseJSON = @ResponseJSON,
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
                CompletionOrigin = 'PROVIDER', CompletedDateTime = @Now, LockedBy = NULL, LockedUntil = NULL, LeaseToken = NULL,
                LastErrorCode = NULL, LastErrorMessage = NULL, UpdatedDateTime = @Now WHERE SystemID = @JobId;
            EXEC dbo.transcription_QueueTranscriptNote @JobId;
        END
        ELSE IF @Success = 0 AND @Winner IS NULL
            AND (@Current = @SubmissionId OR (@Current IS NULL AND @Attempt = @AttemptCount))
            AND @JobStatus IN ('READY','PROCESSING','WAITING_CALLBACK')
        BEGIN
            DECLARE @Retryable bit = CASE WHEN @ErrorCode COLLATE Latin1_General_100_BIN2 IN
                ('REMOTE_CONTENT_ERROR','INTERNAL_SERVER_ERROR','SERVICE_UNAVAILABLE','TOO_MANY_REQUESTS','RATE_LIMIT_EXCEEDED','TIMEOUT') THEN 1 ELSE 0 END;
            DECLARE @Ceiling int = CONVERT(int, CASE WHEN @Base * POWER(CONVERT(float, 2), CASE WHEN @AttemptCount > 20 THEN 20 WHEN @AttemptCount > 0 THEN @AttemptCount - 1 ELSE 0 END) > @Cap
                THEN @Cap ELSE @Base * POWER(CONVERT(float, 2), CASE WHEN @AttemptCount > 20 THEN 20 WHEN @AttemptCount > 0 THEN @AttemptCount - 1 ELSE 0 END) END);
            DECLARE @Delay int = @Ceiling / 2 + (CHECKSUM(NEWID()) & 2147483647) % (@Ceiling - @Ceiling / 2 + 1);
            UPDATE dbo.AutomatedCallTranscriptionJobs SET Status = CASE WHEN @Retryable = 0 THEN 'FAILED'
                WHEN @AttemptCount >= @Maximum OR DATEADD(minute, @MaxAge, @Created) <= @Now THEN 'DEAD_LETTER' ELSE 'READY' END,
                AvailableAt = DATEADD(second, @Delay, @Now), LockedBy = NULL, LockedUntil = NULL, LeaseToken = NULL,
                LastErrorCode = @ErrorCode, LastErrorMessage = @ErrorMessage, UpdatedDateTime = @Now WHERE SystemID = @JobId;
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