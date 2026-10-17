

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Deploy 037.sql in AutoCallDB first; group membership comes only from the queue snapshot.
IF COL_LENGTH(N'dbo.AutomatedCallQueue', N'POGroupNum') IS NULL
    THROW 51000, 'Deploy 037.sql before the POGroup call-log procedure.', 1;
GO

CREATE OR ALTER PROC [dbo].[automatedcalls_GetAllCallForPOGroup]
    @pogroupnum varchar(20),
    @startdate datetime,
    @enddate datetime
AS
BEGIN
    SET NOCOUNT ON;

    IF NULLIF(LTRIM(RTRIM(@pogroupnum)), '') IS NULL
        THROW 51000, 'POGroupNum is required.', 1;
    IF @startdate IS NULL OR @enddate IS NULL OR @startdate >= @enddate
        THROW 51000, 'A UTC startdate earlier than enddate is required.', 1;

    SELECT
        c.SystemID AS CallId,
        c.AutomatedCallQueueId,
        q.ActiveAlarmID,
        q.POGroupNum,
        c.OID,
        c.PhoneE164,
        c.FlowId,
        c.Provider,
        c.providerCallId AS ProviderCallId,
        c.CallStatus,
        c.SubmissionState,
        c.SubmissionErrorCode,
        q.Status AS QueueStatus,
        c.CreatedDateTime,
        c.StartedDateTime,
        c.EndedDateTime,
        c.UpdatedDateTime,
        c.CallDurationSeconds,
        COALESCE((
            SELECT
                r.SystemID AS RecordingId,
                r.Provider,
                r.RecordingSid,
                r.RecordingStatus,
                r.DurationSeconds,
                r.Channels,
                r.RecordingStartTime,
                r.CreatedDateTime,
                r.UpdatedDateTime
            FROM dbo.AutomatedCallRecordings AS r
            WHERE r.CallId = c.SystemID
            ORDER BY r.SystemID
            FOR JSON PATH, INCLUDE_NULL_VALUES
        ), N'[]') AS RecordingsJSON,
        COALESCE((
            SELECT
                j.SystemID AS TranscriptionJobId,
                j.RecordingId,
                j.Provider,
                j.ConfigurationVersion,
                j.Status AS TranscriptionStatus,
                j.AttemptCount,
                j.MaxAttempts,
                j.CompletedSubmissionId,
                j.CompletedDateTime,
                j.LastErrorCode,
                j.CreatedDateTime,
                j.UpdatedDateTime,
                latest.SubmissionId,
                latest.SubmissionStatus,
                latest.AttemptNumber AS SubmissionAttemptNumber,
                latest.SubmittedDateTime,
                latest.CallbackReceivedDateTime,
                latest.LastErrorCode AS SubmissionErrorCode
            FROM dbo.AutomatedCallTranscriptionJobs AS j
            INNER JOIN dbo.AutomatedCallRecordings AS r ON r.SystemID = j.RecordingId
            OUTER APPLY
            (
                SELECT TOP (1)
                    s.SystemID AS SubmissionId,
                    s.Status AS SubmissionStatus,
                    s.AttemptNumber,
                    s.SubmittedDateTime,
                    s.CallbackReceivedDateTime,
                    s.LastErrorCode
                FROM dbo.AutomatedCallTranscriptionSubmissions AS s
                WHERE s.TranscriptionJobId = j.SystemID AND s.RecordingId = j.RecordingId
                ORDER BY s.AttemptNumber DESC, s.SystemID DESC
            ) AS latest
            WHERE r.CallId = c.SystemID
            ORDER BY j.RecordingId, j.ConfigurationVersion, j.SystemID
            FOR JSON PATH, INCLUDE_NULL_VALUES
        ), N'[]') AS TranscriptionsJSON
    FROM dbo.AutomatedCalls AS c
    INNER JOIN dbo.AutomatedCallQueue AS q ON q.SystemID = c.AutomatedCallQueueId
    WHERE q.POGroupNum = @pogroupnum
      AND c.CreatedDateTime >= @startdate
      AND c.CreatedDateTime < @enddate
    ORDER BY c.CreatedDateTime DESC, c.SystemID DESC;
END;
GO