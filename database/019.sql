SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_ClaimJobs
    @WorkerId varchar(100),
    @BatchSize int = 1,
    @LeaseSeconds int = 300,
    @MaxOutstandingSubmissions int = 10
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NULLIF(LTRIM(RTRIM(@WorkerId)), '') IS NULL OR DATALENGTH(@WorkerId) > 100
        OR @BatchSize IS NULL OR @BatchSize NOT BETWEEN 1 AND 100
        OR @LeaseSeconds IS NULL OR @LeaseSeconds NOT BETWEEN 10 AND 3600
        OR @MaxOutstandingSubmissions IS NULL OR @MaxOutstandingSubmissions NOT BETWEEN 1 AND 10000
        THROW 51000, 'Invalid transcription claim settings.', 1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @LockResult int;
        EXEC @LockResult = sys.sp_getapplock @Resource = 'transcription:outstanding-capacity',
            @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
        IF @LockResult < 0 THROW 51000, 'Transcription capacity lock unavailable.', 1;
        DECLARE @Now datetime2(3) = SYSUTCDATETIME(), @Outstanding int, @Capacity int;
        SELECT @Outstanding = COUNT(*) FROM dbo.AutomatedCallTranscriptionJobs
        WHERE Status IN ('PROCESSING', 'WAITING_CALLBACK');
        DECLARE @Claims TABLE (SystemID int, RecordingId int, ConfigurationVersion int,
            ConfigurationJSON nvarchar(max), AttemptCount int, MaxAttempts int,
            CurrentSubmissionId int, LockedBy varchar(100), LockedUntil datetime2(3), LeaseToken uniqueidentifier);
        ;WITH expired AS
        (
            SELECT TOP (@BatchSize) j.* FROM dbo.AutomatedCallTranscriptionJobs j WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE j.Status = 'PROCESSING' AND (j.LockedUntil IS NULL OR j.LockedUntil <= @Now)
            ORDER BY j.AvailableAt, j.SystemID
        )
        UPDATE expired SET LockedBy = @WorkerId, LockedUntil = DATEADD(second, @LeaseSeconds, @Now),
            LeaseToken = NEWID(), UpdatedDateTime = @Now
        OUTPUT inserted.SystemID, inserted.RecordingId, inserted.ConfigurationVersion, inserted.ConfigurationJSON,
            inserted.AttemptCount, inserted.MaxAttempts, inserted.CurrentSubmissionId,
            inserted.LockedBy, inserted.LockedUntil, inserted.LeaseToken INTO @Claims;
        DECLARE @Remaining int = @BatchSize - (SELECT COUNT(*) FROM @Claims);
        SET @Capacity = CASE WHEN @MaxOutstandingSubmissions - @Outstanding < @Remaining
            THEN @MaxOutstandingSubmissions - @Outstanding ELSE @Remaining END;
        IF @Capacity < 0 SET @Capacity = 0;
        ;WITH candidates AS
        (
            SELECT TOP (@Capacity) j.* FROM dbo.AutomatedCallTranscriptionJobs j WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE j.Status = 'READY' AND j.AvailableAt <= @Now
            ORDER BY j.AvailableAt, j.SystemID
        )
        UPDATE candidates SET Status = 'PROCESSING', LockedBy = @WorkerId,
            LockedUntil = DATEADD(second, @LeaseSeconds, @Now), LeaseToken = NEWID(), UpdatedDateTime = @Now
        OUTPUT inserted.SystemID, inserted.RecordingId, inserted.ConfigurationVersion, inserted.ConfigurationJSON,
            inserted.AttemptCount, inserted.MaxAttempts, inserted.CurrentSubmissionId,
            inserted.LockedBy, inserted.LockedUntil, inserted.LeaseToken INTO @Claims;
        COMMIT;
        SELECT * FROM @Claims;
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
    SELECT j.SystemID AS JobId, j.RecordingId, j.ConfigurationVersion, j.ConfigurationJSON, j.LeaseToken,
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