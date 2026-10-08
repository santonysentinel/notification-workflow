SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF OBJECT_ID(N'dbo.AutomatedCallRecordings', N'U') IS NULL
CREATE TABLE dbo.AutomatedCallRecordings
(
    SystemID int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    CallId int NOT NULL,
    Provider varchar(30) COLLATE Latin1_General_100_BIN2 NOT NULL,
    AccountSid varchar(34) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RecordingSid varchar(34) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RecordingStatus varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RecordingUrl nvarchar(2048) NULL,
    DurationSeconds int NULL,
    Channels tinyint NULL,
    RecordingStartTime datetime2(3) NULL,
    RecordingSource varchar(100) NULL,
    RecordingTrack varchar(20) NULL,
    CreatedDateTime datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
    UpdatedDateTime datetime2(3) NULL,
    CONSTRAINT FK_AutomatedCallRecordings_CallId FOREIGN KEY (CallId) REFERENCES dbo.AutomatedCalls (SystemID),
    CONSTRAINT UQ_AutomatedCallRecordings_ProviderIdentity UNIQUE (Provider, AccountSid, RecordingSid),
    CONSTRAINT CK_AutomatedCallRecordings_Status CHECK (RecordingStatus IN ('in-progress', 'completed', 'absent', 'failed')),
    CONSTRAINT CK_AutomatedCallRecordings_Duration CHECK (DurationSeconds IS NULL OR DurationSeconds >= 0),
    CONSTRAINT CK_AutomatedCallRecordings_Channels CHECK (Channels IS NULL OR Channels IN (1, 2)),
    CONSTRAINT CK_AutomatedCallRecordings_Track CHECK (RecordingTrack IS NULL OR RecordingTrack IN ('inbound', 'outbound', 'both'))
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallRecordings') AND name = N'IX_AutomatedCallRecordings_CallId')
CREATE INDEX IX_AutomatedCallRecordings_CallId ON dbo.AutomatedCallRecordings (CallId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallEvents') AND name = N'UX_AutomatedCallEvents_CallId_IdempotencyKey')
CREATE UNIQUE INDEX UX_AutomatedCallEvents_CallId_IdempotencyKey
ON dbo.AutomatedCallEvents (CallId, IdempotencyKey)
WHERE CallId IS NOT NULL AND IdempotencyKey IS NOT NULL;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_RecordingStatus
    @CallId int,
    @ProviderCallId varchar(200),
    @AccountSid varchar(34),
    @RecordingSid varchar(34),
    @RecordingStatus varchar(20),
    @RecordingUrl nvarchar(2048) = NULL,
    @DurationSeconds int = NULL,
    @Channels tinyint = NULL,
    @RecordingStartTime datetime2(3) = NULL,
    @RecordingSource varchar(100) = NULL,
    @RecordingTrack varchar(20) = NULL,
    @ParametersJSON nvarchar(max) = N'{}'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET ANSI_PADDING ON;
    SET ANSI_WARNINGS ON;
    SET ARITHABORT ON;
    SET CONCAT_NULL_YIELDS_NULL ON;
    SET NUMERIC_ROUNDABORT OFF;

    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Found bit = 0, @Provider varchar(100), @ExistingSid varchar(200),
                @RecordingId int, @RecordingCallId int, @CurrentStatus varchar(20),
                @Outcome varchar(30) = 'recorded', @Applied bit = 0, @IgnoreReason varchar(40),
                @Now datetime2(3) = SYSUTCDATETIME(),
                @Key varchar(255) = 'recording:status:' + @RecordingSid + ':' + @RecordingStatus;

        SELECT @Found = 1, @Provider = Provider, @ExistingSid = providerCallId
        FROM dbo.AutomatedCalls WITH (UPDLOCK, HOLDLOCK) WHERE SystemID = @CallId;

        IF @Found = 0 SET @Outcome = 'not-found';
        ELSE IF @Provider IS NULL OR LOWER(@Provider) <> 'twilio' SET @Outcome = 'provider-mismatch';
        ELSE IF @ExistingSid IS NULL OR @ProviderCallId IS NULL
             OR @ExistingSid COLLATE Latin1_General_100_BIN2 <> @ProviderCallId COLLATE Latin1_General_100_BIN2
            SET @Outcome = 'call-sid-mismatch';
        ELSE IF @AccountSid IS NULL OR LEN(@AccountSid) <> 34 OR @AccountSid COLLATE Latin1_General_100_BIN2 NOT LIKE 'AC%'
             OR SUBSTRING(@AccountSid, 3, 32) COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-fA-F]%'
             OR @RecordingSid IS NULL OR LEN(@RecordingSid) <> 34 OR @RecordingSid COLLATE Latin1_General_100_BIN2 NOT LIKE 'RE%'
             OR SUBSTRING(@RecordingSid, 3, 32) COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-fA-F]%'
             OR @RecordingStatus IS NULL OR @RecordingStatus COLLATE Latin1_General_100_BIN2 NOT IN ('in-progress', 'completed', 'absent', 'failed')
             OR @DurationSeconds < 0 OR @Channels NOT IN (1, 2)
             OR (@RecordingTrack IS NOT NULL AND @RecordingTrack NOT IN ('inbound', 'outbound', 'both'))
             OR (@RecordingStatus = 'completed' AND (NULLIF(@RecordingUrl, '') IS NULL OR @DurationSeconds IS NULL OR @Channels IS NULL))
             OR @ParametersJSON IS NULL OR ISJSON(@ParametersJSON) <> 1
            SET @Outcome = 'invalid-recording';

        IF @Outcome = 'recorded'
        BEGIN
            SELECT @RecordingId = SystemID, @RecordingCallId = CallId, @CurrentStatus = RecordingStatus
            FROM dbo.AutomatedCallRecordings WITH (UPDLOCK, HOLDLOCK)
            WHERE Provider = 'Twilio' AND AccountSid = @AccountSid AND RecordingSid = @RecordingSid;
            IF @RecordingId IS NOT NULL AND @RecordingCallId <> @CallId
                SET @Outcome = 'recording-conflict';
            ELSE IF EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND IdempotencyKey = @Key)
                SET @Outcome = 'duplicate';
        END;

        IF @Outcome = 'recorded'
        BEGIN
            IF @CurrentStatus IN ('completed', 'absent', 'failed')
                SET @IgnoreReason = CASE WHEN @RecordingStatus = 'in-progress' THEN 'already-terminal' ELSE 'terminal-conflict' END;
            ELSE SET @Applied = 1;

            IF @RecordingId IS NULL
            BEGIN
                INSERT INTO dbo.AutomatedCallRecordings
                    (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, RecordingUrl, DurationSeconds,
                     Channels, RecordingStartTime, RecordingSource, RecordingTrack, CreatedDateTime, UpdatedDateTime)
                VALUES (@CallId, 'Twilio', @AccountSid, @RecordingSid, @RecordingStatus, @RecordingUrl, @DurationSeconds,
                        @Channels, @RecordingStartTime, @RecordingSource, @RecordingTrack, @Now, @Now);
                SET @RecordingId = SCOPE_IDENTITY();
            END
            ELSE IF @Applied = 1
                UPDATE dbo.AutomatedCallRecordings
                SET RecordingStatus = @RecordingStatus, RecordingUrl = COALESCE(@RecordingUrl, RecordingUrl),
                    DurationSeconds = COALESCE(@DurationSeconds, DurationSeconds), Channels = COALESCE(@Channels, Channels),
                    RecordingStartTime = COALESCE(@RecordingStartTime, RecordingStartTime),
                    RecordingSource = COALESCE(@RecordingSource, RecordingSource), RecordingTrack = COALESCE(@RecordingTrack, RecordingTrack),
                    UpdatedDateTime = @Now
                WHERE SystemID = @RecordingId;

            DECLARE @EventJSON nvarchar(max) = (
                SELECT @RecordingId AS recordingId, @ProviderCallId AS providerCallId, @AccountSid AS accountSid,
                       @RecordingSid AS recordingSid, @RecordingStatus AS recordingStatus, @RecordingUrl AS recordingUrl,
                       @DurationSeconds AS durationSeconds, @Channels AS channels,
                       CASE WHEN @RecordingStartTime IS NOT NULL THEN CONVERT(varchar(33), @RecordingStartTime, 126) + 'Z' END AS recordingStartTime,
                       @RecordingSource AS recordingSource, @RecordingTrack AS recordingTrack,
                       CONVERT(varchar(33), @Now, 126) + 'Z' AS receivedDateTime,
                       @Applied AS applied, @IgnoreReason AS ignoreReason, JSON_QUERY(@ParametersJSON) AS parameters
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
            );
            INSERT INTO dbo.AutomatedCallEvents (CallId, CallEventDateTime, EventType, EventJSON, IdempotencyKey)
            VALUES (@CallId, @Now, 'RecordingStatusReceived', @EventJSON, @Key);
        END;

        COMMIT TRANSACTION;
        SELECT @Outcome AS Outcome, @RecordingId AS RecordingId, @Applied AS Applied, @IgnoreReason AS IgnoreReason;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO