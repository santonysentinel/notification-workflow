SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'NotificationWorkflowVoiceTests'
    THROW 51000, 'Run recording lookup tests only in the owned scratch database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.AutomatedCalls (Provider, providerCallId, CallStatus)
    VALUES ('Twilio', 'CAbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'completed');
    DECLARE @CallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings
        (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, RecordingUrl)
    VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            'REcccccccccccccccccccccccccccccccc', 'completed',
            N'https://api.twilio.com/2010-04-01/Accounts/ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/Recordings/REcccccccccccccccccccccccccccccccc');
    DECLARE @RecordingId int = SCOPE_IDENTITY(), @Before nvarchar(max);
    SELECT @Before = (SELECT * FROM dbo.AutomatedCallRecordings WHERE CallId = @CallId FOR JSON PATH, INCLUDE_NULL_VALUES);
    DECLARE @Result TABLE
    (
        RecordingId int, CallId int, Provider varchar(30), AccountSid varchar(34),
        RecordingSid varchar(34), RecordingStatus varchar(20),
        RecordingUrl nvarchar(2048), ProviderCallId varchar(200)
    );
    INSERT @Result EXEC dbo.automatedcalls_GetRecording @RecordingId;
    IF (SELECT COUNT(*) FROM @Result) <> 1 OR NOT EXISTS
        (SELECT 1 FROM @Result WHERE RecordingId = @RecordingId AND CallId = @CallId
            AND ProviderCallId = 'CAbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
            AND RecordingStatus = 'completed' AND RecordingUrl IS NOT NULL)
        THROW 51000, 'Recording lookup did not return the requested metadata and bound call SID.', 1;

    DELETE @Result;
    INSERT @Result EXEC dbo.automatedcalls_GetRecording 2147483647;
    IF EXISTS (SELECT 1 FROM @Result)
        THROW 51000, 'Missing recording lookup returned a row.', 1;

    UPDATE dbo.AutomatedCallRecordings SET RecordingStatus = 'absent', RecordingUrl = NULL WHERE SystemID = @RecordingId;
    INSERT @Result EXEC dbo.automatedcalls_GetRecording @RecordingId;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE RecordingStatus = 'absent' AND RecordingUrl IS NULL)
        THROW 51000, 'Lookup must return non-playable rows for the controller to reject.', 1;
    UPDATE dbo.AutomatedCallRecordings SET RecordingStatus = 'completed',
        RecordingUrl = N'https://api.twilio.com/2010-04-01/Accounts/ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/Recordings/REcccccccccccccccccccccccccccccccc'
    WHERE SystemID = @RecordingId;
    IF @Before <> (SELECT * FROM dbo.AutomatedCallRecordings WHERE CallId = @CallId FOR JSON PATH, INCLUDE_NULL_VALUES)
        OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId)
        OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND CallStatus = 'completed' AND UpdatedDateTime IS NULL)
        THROW 51000, 'Recording lookup changed call, recording, or event data.', 1;
    ROLLBACK TRANSACTION;
    PRINT 'Recording lookup SQL regression checks passed; test data rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

BEGIN TRY
    EXEC dbo.automatedcalls_GetRecording 0;
    THROW 51001, 'Invalid recording ID was accepted.', 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() <> 51000 THROW;
END CATCH;