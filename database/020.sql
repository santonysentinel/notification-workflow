SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
IF OBJECT_ID('dbo.AutomatedCallTranscriptionOperations', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AutomatedCallTranscriptionOperations
    (
        SystemID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_TranscriptionOperations PRIMARY KEY,
        SourceJobId int NOT NULL CONSTRAINT FK_TranscriptionOperations_Source REFERENCES dbo.AutomatedCallTranscriptionJobs(SystemID),
        NewJobId int NOT NULL CONSTRAINT FK_TranscriptionOperations_New REFERENCES dbo.AutomatedCallTranscriptionJobs(SystemID),
        RequestedBy nvarchar(100) NOT NULL,
        Reason nvarchar(500) NOT NULL,
        CreatedDateTime datetime2(3) NOT NULL CONSTRAINT DF_TranscriptionOperations_Created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_TranscriptionOperations_NewJob UNIQUE(NewJobId)
    );
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_GetQueueMetrics
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    SELECT
        (SELECT COUNT_BIG(*) FROM dbo.AutomatedCallTranscriptionJobs WHERE Status = 'READY') AS ReadyJobs,
        (SELECT COALESCE(MAX(CASE WHEN CreatedDateTime < @Now THEN DATEDIFF_BIG(second, CreatedDateTime, @Now) ELSE 0 END), 0)
            FROM dbo.AutomatedCallTranscriptionJobs WHERE Status = 'READY') AS QueueAgeSeconds,
        (SELECT COUNT_BIG(*) FROM dbo.AutomatedCallTranscriptionJobs WHERE Status IN ('PROCESSING','WAITING_CALLBACK')) AS OutstandingSubmissions,
        (SELECT COUNT_BIG(*) FROM dbo.AutomatedCallTranscriptionSubmissions WHERE Status IN ('SUBMITTING','ACCEPTED','TIMED_OUT')
            AND SendStartedDateTime IS NOT NULL AND CallbackDeadlineDateTime <= @Now AND CallbackPayloadHash IS NULL) AS CallbackTimeoutSubmissions,
        (SELECT COUNT_BIG(*) FROM dbo.AutomatedCallTranscriptionJobs WHERE Status = 'DEAD_LETTER') AS DeadLetterJobs;
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_RequestRetranscription
    @SourceJobId int,
    @ConfigurationVersion int,
    @ConfigurationJSON nvarchar(max),
    @RequestedBy nvarchar(100),
    @Reason nvarchar(500)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @SourceJobId IS NULL OR @SourceJobId <= 0 OR @ConfigurationVersion IS NULL OR @ConfigurationVersion <= 0
        OR NULLIF(LTRIM(RTRIM(@RequestedBy)), '') IS NULL OR NULLIF(LTRIM(RTRIM(@Reason)), '') IS NULL
        OR @ConfigurationJSON IS NULL OR ISJSON(@ConfigurationJSON) <> 1 OR LEFT(LTRIM(@ConfigurationJSON), 1) <> '{'
        THROW 51000, 'Retranscription requires a source, complete versioned options and operator reason.', 1;
    DECLARE @Options TABLE (Name nvarchar(4000) COLLATE Latin1_General_100_BIN2, Value nvarchar(max), JsonType int);
    INSERT @Options SELECT [key], value, type FROM OPENJSON(@ConfigurationJSON);
    IF EXISTS (SELECT Name FROM @Options GROUP BY Name HAVING COUNT(*) <> 1)
        OR EXISTS (SELECT 1 FROM @Options WHERE Name NOT IN ('model','version','language','detect_language','utterances','multichannel','diarize','punctuate','smart_format','mip_opt_out'))
        OR EXISTS (SELECT 1 FROM @Options WHERE Name IN ('model','version','language')
            AND (JsonType <> 1 OR LEN(Value) NOT BETWEEN 1 AND 100 OR Value COLLATE Latin1_General_100_BIN2 LIKE N'%[^-a-zA-Z0-9_.]%'))
        OR EXISTS (SELECT 1 FROM @Options WHERE Name NOT IN ('model','version','language') AND JsonType <> 3)
        OR NOT EXISTS (SELECT 1 FROM @Options WHERE Name='model') OR NOT EXISTS (SELECT 1 FROM @Options WHERE Name='version')
        OR (NOT EXISTS (SELECT 1 FROM @Options WHERE Name='language') AND NOT EXISTS (SELECT 1 FROM @Options WHERE Name='detect_language' AND Value='true'))
        OR NOT EXISTS (SELECT 1 FROM @Options WHERE Name='utterances' AND Value='true')
        OR NOT EXISTS (SELECT 1 FROM @Options WHERE Name='multichannel' AND Value='true')
        OR NOT EXISTS (SELECT 1 FROM @Options WHERE Name='mip_opt_out' AND Value='true')
        OR NOT EXISTS (SELECT 1 FROM @Options WHERE Name='diarize') OR NOT EXISTS (SELECT 1 FROM @Options WHERE Name='punctuate')
        OR NOT EXISTS (SELECT 1 FROM @Options WHERE Name='smart_format')
        THROW 51000, 'Retranscription provider snapshot is incomplete or invalid.', 1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @RecordingId int, @Version int, @Status varchar(20), @Provider varchar(50), @NewJobId int,
            @Attempts int, @Age int, @Wait int, @Base int, @Cap int, @LockResult int;
        SELECT @RecordingId=RecordingId FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@SourceJobId;
        IF @RecordingId IS NULL THROW 51000, 'Retranscription source not found.', 1;
        DECLARE @Resource nvarchar(255) = 'transcription:retranscription:' + CONVERT(varchar(20), @RecordingId);
        EXEC @LockResult=sys.sp_getapplock @Resource=@Resource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000;
        IF @LockResult < 0 THROW 51000, 'Retranscription lock unavailable.', 1;
        SELECT @Version=ConfigurationVersion, @Status=Status, @Provider=Provider,
            @Attempts=MaxAttempts, @Age=MaxJobAgeMinutes, @Wait=CallbackWaitMinutes, @Base=RetryBaseSeconds, @Cap=RetryMaxSeconds
        FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK,HOLDLOCK) WHERE SystemID=@SourceJobId AND RecordingId=@RecordingId;
        IF @Status IS NULL OR @Status NOT IN ('COMPLETED','FAILED','DEAD_LETTER') OR @Provider <> 'Deepgram' OR @ConfigurationVersion <= @Version
            THROW 51000, 'Retranscription requires a terminal Deepgram source and a newer configuration version.', 1;
        IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings WHERE SystemID=@RecordingId AND RecordingStatus='completed')
            THROW 51000, 'Retranscription recording must be completed.', 1;
        SELECT @NewJobId=SystemID FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK,HOLDLOCK)
            WHERE RecordingId=@RecordingId AND Provider=@Provider AND ConfigurationVersion=@ConfigurationVersion;
        IF @NewJobId IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionOperations WHERE NewJobId=@NewJobId AND SourceJobId=@SourceJobId)
                OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@NewJobId
                    AND ConfigurationJSON COLLATE Latin1_General_100_BIN2=@ConfigurationJSON COLLATE Latin1_General_100_BIN2
                    AND DATALENGTH(ConfigurationJSON)=DATALENGTH(@ConfigurationJSON))
                THROW 51000, 'Retranscription version conflicts with existing work.', 1;
            COMMIT;
            SELECT 'duplicate' AS Outcome, @NewJobId AS JobId;
            RETURN;
        END;
        IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WITH (UPDLOCK,HOLDLOCK)
            WHERE RecordingId=@RecordingId AND Provider=@Provider AND Status IN ('READY','PROCESSING','WAITING_CALLBACK'))
            THROW 51000, 'Retranscription is blocked while another version is active.', 1;
        IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId AND Provider=@Provider AND ConfigurationVersion>=@ConfigurationVersion)
            THROW 51000, 'Retranscription requires a new highest configuration version.', 1;
        IF EXISTS (SELECT 1 FROM dbo.AutomatedCallTranscriptionJobs j
            JOIN dbo.AutomatedCallTranscriptionSubmissions s ON s.TranscriptionJobId=j.SystemID
            WHERE j.RecordingId=@RecordingId AND j.Provider=@Provider AND j.Status='DEAD_LETTER'
                AND s.SendStartedDateTime IS NOT NULL AND s.CallbackPayloadHash IS NULL AND s.CallbackTokenExpiresAt>SYSUTCDATETIME())
            THROW 51000, 'Retranscription is blocked while a dead-letter send can still authenticate a late callback.', 1;
        INSERT dbo.AutomatedCallTranscriptionJobs
            (RecordingId,Provider,ConfigurationVersion,ConfigurationJSON,MaxAttempts,MaxJobAgeMinutes,CallbackWaitMinutes,RetryBaseSeconds,RetryMaxSeconds)
        VALUES (@RecordingId,@Provider,@ConfigurationVersion,@ConfigurationJSON,@Attempts,@Age,@Wait,@Base,@Cap);
        SET @NewJobId=SCOPE_IDENTITY();
        INSERT dbo.AutomatedCallTranscriptionOperations(SourceJobId,NewJobId,RequestedBy,Reason)
            VALUES(@SourceJobId,@NewJobId,@RequestedBy,@Reason);
        COMMIT;
        SELECT 'created' AS Outcome, @NewJobId AS JobId;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO