

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
        q.SystemID AS AutomatedCallQueueId,
        q.ActiveAlarmID,
        q.POGroupNum,
        COALESCE(c.OID, q.OID) AS OID,
        c.PhoneE164,
        COALESCE(c.FlowId, q.FlowId) AS FlowId,
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
                latest.LastErrorCode AS SubmissionErrorCode,
                -- Message timestamps are offsets from the start of the recording.
                JSON_QUERY(COALESCE((
                    SELECT
                        t.Sentence,
                        t.AudioChannel,
                        t.StartTimeMilliseconds,
                        t.EndTimeMilliseconds,
                        t.TranscriptText
                    FROM dbo.AutomatedCallTranscripts AS t
                    WHERE t.SubmissionId = j.CompletedSubmissionId
                    ORDER BY t.Sentence, t.AudioChannel
                    FOR JSON PATH, INCLUDE_NULL_VALUES
                ), N'[]')) AS Messages
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
        ), N'[]') AS TranscriptionsJSON,
        q.CreatedDateTime AS QueueCreatedDateTime,
        q.UpdatedAt AS QueueUpdatedAt,
        q.AvailableAt AS QueueAvailableAt,
        q.AttemptCount AS QueueAttemptCount,
        q.MaxAttempts AS QueueMaxAttempts,
        q.LastErrorCode AS QueueLastErrorCode
    FROM dbo.AutomatedCallQueue AS q
    OUTER APPLY
    (
        SELECT TOP (1)
            attempt.SystemID, attempt.OID, attempt.PhoneE164, attempt.FlowId,
            attempt.Provider, attempt.providerCallId, attempt.CallStatus,
            attempt.SubmissionState, attempt.SubmissionErrorCode,
            attempt.CreatedDateTime, attempt.StartedDateTime, attempt.EndedDateTime,
            attempt.UpdatedDateTime, attempt.CallDurationSeconds
        FROM dbo.AutomatedCalls AS attempt
        WHERE attempt.AutomatedCallQueueId = q.SystemID
        ORDER BY attempt.CreatedDateTime DESC, attempt.SystemID DESC
    ) AS c
    WHERE (q.POGroupNum = @pogroupnum OR q.POGroupNum LIKE @pogroupnum + '%')
      AND q.CreatedDateTime >= @startdate
      AND q.CreatedDateTime < @enddate
    ORDER BY q.CreatedDateTime DESC, q.SystemID DESC;
END;
GO