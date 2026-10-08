SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetRecording
    @RecordingId int
AS
BEGIN
    SET NOCOUNT ON;
    IF @RecordingId IS NULL OR @RecordingId <= 0
        THROW 51000, 'RecordingId must be a positive SQL integer.', 1;

    SELECT r.SystemID AS RecordingId, r.CallId, r.Provider, r.AccountSid,
           r.RecordingSid, r.RecordingStatus, r.RecordingUrl,
           c.providerCallId AS ProviderCallId
    FROM dbo.AutomatedCallRecordings AS r
    INNER JOIN dbo.AutomatedCalls AS c ON c.SystemID = r.CallId
    WHERE r.SystemID = @RecordingId;
END;
GO