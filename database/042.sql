SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.automatedcalls_GetRecordingForPOGroup', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.automatedcalls_GetRecordingForPOGroup AS RETURN 0;');
GO

ALTER PROCEDURE dbo.automatedcalls_GetRecordingForPOGroup
    @RecordingId int
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;

    IF @RecordingId IS NULL OR @RecordingId <= 0
        THROW 51003, 'RecordingId is required.', 1;

    SELECT r.SystemID AS RecordingId,
        r.CallId,
        c.AutomatedCallQueueId,
        q.POGroupNum,
        r.Provider,
        r.AccountSid,
        r.RecordingSid,
        r.RecordingStatus,
        r.RecordingUrl,
        r.DurationSeconds,
        r.Channels,
        r.RecordingStartTime,
        r.RecordingSource,
        r.RecordingTrack,
        r.CreatedDateTime,
        r.UpdatedDateTime
    FROM dbo.AutomatedCallRecordings r
    INNER JOIN dbo.AutomatedCalls c ON c.SystemID = r.CallId
    INNER JOIN dbo.AutomatedCallQueue q ON q.SystemID = c.AutomatedCallQueueId
        WHERE r.SystemID = @RecordingId;
END;
GO