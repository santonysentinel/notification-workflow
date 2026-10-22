SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'NotificationWorkflowCallReportTests'
    THROW 51000, 'Run call-log tests only in the owned scratch database.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallQueue')
      AND name = N'POGroupNum' AND max_length = 20 AND is_nullable = 1
      AND system_type_id = TYPE_ID(N'varchar')
)
    THROW 51000, 'The queue group snapshot must be nullable varchar(20).', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @Start datetime = '2026-10-17T00:00:00', @End datetime = '2026-10-18T00:00:00';
    INSERT dbo.CallFlowTemplates (Name, TemplateJSON, TwiML, CreatedBy)
    VALUES ('report-test', N'{}', N'<CallFlow/>', N'test');
    DECLARE @FlowId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, OID, FlowId, POGroupNum, CreatedDateTime)
    VALUES (42, 'report-oid', @FlowId, 'report-test', @Start);
    DECLARE @QueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, POGroupNum, CreatedDateTime)
    VALUES (43, 'report-test-child', DATEADD(millisecond, -1, CONVERT(datetime2(3), @End)));
    DECLARE @ChildQueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, POGroupNum, CreatedDateTime)
    VALUES (44, 'unrelated-group', @Start);
    DECLARE @OtherQueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, CreatedDateTime) VALUES (45, @Start);
    DECLARE @UnassignedQueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, POGroupNum, CreatedDateTime)
    VALUES (46, 'report-test', DATEADD(millisecond, -1, CONVERT(datetime2(3), @Start)));
    DECLARE @BeforeQueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, POGroupNum, CreatedDateTime)
    VALUES (47, 'report-test', @End);
    DECLARE @EndQueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, POGroupNum, CreatedDateTime)
    VALUES (48, 'report-test', DATEADD(hour, 1, @Start));
    DECLARE @NoMediaQueueId int = SCOPE_IDENTITY();

    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, OID, FlowId, POGroupNum, CreatedDateTime, AvailableAt)
    VALUES (49, 'ready-oid', @FlowId, 'report-test', @Start, DATEADD(day, 7, @End));
    DECLARE @ReadyQueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue
        (ActiveAlarmID, POGroupNum, CreatedDateTime, Status, AttemptCount, MaxAttempts, LastErrorCode)
    VALUES (50, 'report-test', @Start, 'FAILED', 2, 5, 'PREPARATION_FAILED');
    DECLARE @FailedQueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue
        (ActiveAlarmID, POGroupNum, CreatedDateTime, Status, AttemptCount, MaxAttempts, LastErrorCode)
    VALUES (51, 'report-test', @Start, 'DEAD_LETTER', 5, 5, 'RETRIES_EXHAUSTED');
    DECLARE @DeadQueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, POGroupNum, CreatedDateTime, Status)
    VALUES (52, 'report-test', @Start, 'PROCESSING');
    DECLARE @ProcessingQueueId int = SCOPE_IDENTITY();

    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, CallStatus, CreatedDateTime)
    VALUES (@QueueId, 'Twilio', 'failed', DATEADD(day, 1, @End));
    DECLARE @OlderCallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, CallStatus, CreatedDateTime)
    VALUES (@QueueId, 'Twilio', 'completed', DATEADD(day, 1, @End));
    DECLARE @CallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, CallStatus, CreatedDateTime)
    VALUES (@NoMediaQueueId, 'Twilio', 'no-answer', DATEADD(day, -1, @Start));
    DECLARE @NoMediaCallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, CallStatus, CreatedDateTime)
    VALUES (@ChildQueueId, 'Twilio', 'completed', @Start);
    DECLARE @ChildCallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, CallStatus, CreatedDateTime)
    VALUES (@OtherQueueId, 'Twilio', 'completed', @Start),
           (@UnassignedQueueId, 'Twilio', 'completed', @Start),
           (@BeforeQueueId, 'Twilio', 'completed', @Start),
           (@EndQueueId, 'Twilio', 'completed', @Start),
           (NULL, 'Twilio', 'completed', @Start);

    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, DurationSeconds)
    VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'completed', 30);
    DECLARE @RecordingId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus)
    VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REcccccccccccccccccccccccccccccccc', 'failed');
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus)
    VALUES (@ChildCallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REdddddddddddddddddddddddddddddddd', 'completed');
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus)
    VALUES (@OlderCallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 'absent');

    INSERT dbo.AutomatedCallTranscriptionJobs (RecordingId, ConfigurationVersion, ConfigurationJSON)
    VALUES (@RecordingId, 1, N'{}');
    DECLARE @CompletedJobId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId, RecordingId, AttemptNumber, CallbackTokenHash, Status, CallbackDeadlineDateTime)
    VALUES (@CompletedJobId, @RecordingId, 1, HASHBYTES('SHA2_256', 'test'), 'COMPLETED', DATEADD(day, 1, SYSUTCDATETIME()));
    DECLARE @CompletedSubmissionId int = SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCallTranscriptionJobs
    SET Status = 'COMPLETED', CompletedSubmissionId = @CompletedSubmissionId, CompletedDateTime = SYSUTCDATETIME()
    WHERE SystemID = @CompletedJobId;
    INSERT dbo.AutomatedCallTranscripts
        (CallId, RecordingId, SubmissionId, Sentence, AudioChannel, StartTimeMilliseconds, EndTimeMilliseconds, TranscriptText)
    VALUES
        (@CallId, @RecordingId, @CompletedSubmissionId, 1, 1, 1250, 2500, N'world'),
        (@CallId, @RecordingId, @CompletedSubmissionId, 0, 0, 0, 1000, N'Hello');

    INSERT dbo.AutomatedCallTranscriptionJobs
        (RecordingId, ConfigurationVersion, ConfigurationJSON, Status, AttemptCount, LastErrorCode)
    VALUES (@RecordingId, 2, N'{}', 'FAILED', 2, 'TEST_FAILURE');
    DECLARE @FailedJobId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallTranscriptionSubmissions
        (TranscriptionJobId, RecordingId, AttemptNumber, CallbackTokenHash, Status, CallbackDeadlineDateTime, LastErrorCode)
    VALUES (@FailedJobId, @RecordingId, 1, HASHBYTES('SHA2_256', 'test'), 'TIMED_OUT', DATEADD(day, 1, SYSUTCDATETIME()), 'OLD_ERROR'),
           (@FailedJobId, @RecordingId, 2, HASHBYTES('SHA2_256', 'test'), 'FAILED', DATEADD(day, 1, SYSUTCDATETIME()), 'LATEST_ERROR');
    DECLARE @LatestSubmissionId int = SCOPE_IDENTITY();

    DECLARE @Result TABLE
    (
        CallId int, AutomatedCallQueueId int, ActiveAlarmID int, POGroupNum varchar(20),
        OID varchar(20), PhoneE164 varchar(16), FlowId int, Provider varchar(100),
        ProviderCallId varchar(200), CallStatus varchar(20), SubmissionState varchar(20),
        SubmissionErrorCode varchar(100), QueueStatus varchar(20),
        CreatedDateTime datetime2(3), StartedDateTime datetime2(3), EndedDateTime datetime2(3),
        UpdatedDateTime datetime2(3), CallDurationSeconds int,
        RecordingsJSON nvarchar(max), TranscriptionsJSON nvarchar(max),
        QueueCreatedDateTime datetime2(3), QueueUpdatedAt datetime2(3), QueueAvailableAt datetime2(3),
        QueueAttemptCount int, QueueMaxAttempts int, QueueLastErrorCode varchar(100),
        PhoneType varchar(20), AnsweredBy varchar(30), CallDeviceIndex int, RootQueueId int
    );
    UPDATE dbo.AutomatedCalls SET PhoneType = 'CellPhone', DeviceIndex = 0, AnsweredBy = 'human' WHERE SystemID = @CallId;
    UPDATE dbo.AutomatedCallQueue SET CallDeviceIndex = 1, RootQueueId = @QueueId,
        OptionsJSON = N'{"historyId":1,"postRecordingTranscript":false,"callDevices":[{"type":"CellPhone","phoneNumber":null},{"type":"HomePhone","phoneNumber":null}]}'
    WHERE SystemID = @ReadyQueueId;
    INSERT @Result EXEC dbo.automatedcalls_GetAllCallForPOGroup 'report-test', @Start, @End;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE CallId = @CallId AND PhoneType = 'CellPhone' AND AnsweredBy = 'human'
            AND CallDeviceIndex = 0 AND RootQueueId IS NULL)
        OR NOT EXISTS (SELECT 1 FROM @Result WHERE AutomatedCallQueueId = @ReadyQueueId AND CallId IS NULL
            AND PhoneType = 'HomePhone' AND AnsweredBy IS NULL AND CallDeviceIndex = 1 AND RootQueueId = @QueueId)
        THROW 51000, 'Report must show the dialled device, who answered, and the fallback chain.', 1;

    IF (SELECT COUNT(*) FROM @Result) <> 7
        OR NOT EXISTS
        (
            SELECT 1 FROM @Result
            WHERE AutomatedCallQueueId = @QueueId AND CallId = @CallId AND CallStatus = 'completed'
              AND QueueCreatedDateTime = @Start AND CreatedDateTime > @End
              AND OID = 'report-oid' AND FlowId = @FlowId
        )
        OR NOT EXISTS (SELECT 1 FROM @Result WHERE AutomatedCallQueueId = @ChildQueueId AND POGroupNum = 'report-test-child')
        OR EXISTS (SELECT AutomatedCallQueueId FROM @Result GROUP BY AutomatedCallQueueId HAVING COUNT(*) > 1)
        OR EXISTS
        (
            SELECT 1 FROM @Result
            WHERE AutomatedCallQueueId IN (@BeforeQueueId, @EndQueueId, @OtherQueueId, @UnassignedQueueId)
               OR QueueCreatedDateTime < @Start OR QueueCreatedDateTime >= @End
        )
        THROW 51000, 'Report must use queue date boundaries, preserve child-group filtering, and return only the latest call per queue.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM @Result
        WHERE AutomatedCallQueueId = @ReadyQueueId AND CallId IS NULL AND CallStatus IS NULL
          AND SubmissionState IS NULL AND QueueStatus = 'READY'
          AND OID = 'ready-oid' AND FlowId = @FlowId
          AND QueueAttemptCount = 0 AND QueueMaxAttempts = 10 AND QueueAvailableAt > @End
          AND RecordingsJSON = N'[]' AND TranscriptionsJSON = N'[]'
    )
        OR NOT EXISTS
        (
            SELECT 1 FROM @Result
            WHERE AutomatedCallQueueId = @FailedQueueId AND CallId IS NULL AND QueueStatus = 'FAILED'
              AND QueueAttemptCount = 2 AND QueueMaxAttempts = 5 AND QueueLastErrorCode = 'PREPARATION_FAILED'
        )
        OR NOT EXISTS
        (
            SELECT 1 FROM @Result
            WHERE AutomatedCallQueueId = @DeadQueueId AND CallId IS NULL AND QueueStatus = 'DEAD_LETTER'
              AND QueueLastErrorCode = 'RETRIES_EXHAUSTED'
        )
        OR NOT EXISTS
        (
            SELECT 1 FROM @Result
            WHERE AutomatedCallQueueId = @ProcessingQueueId AND CallId IS NULL AND QueueStatus = 'PROCESSING'
        )
        THROW 51000, 'Requests without calls must show current READY/PROCESSING/FAILED/DEAD_LETTER queue status and retry context.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM @Result
        WHERE CallId = @NoMediaCallId AND CallStatus = 'no-answer'
          AND RecordingsJSON = N'[]' AND TranscriptionsJSON = N'[]'
    )
        THROW 51000, 'Calls without recordings/transcription must remain visible with empty arrays.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM @Result
        WHERE AutomatedCallQueueId = @ChildQueueId AND CallId = @ChildCallId
          AND (SELECT COUNT(*) FROM OPENJSON(RecordingsJSON)) = 1 AND TranscriptionsJSON = N'[]'
    )
        THROW 51000, 'A recording without a transcription job must not hide the request.', 1;

    DECLARE @Recordings nvarchar(max), @Transcriptions nvarchar(max);
    SELECT @Recordings = RecordingsJSON, @Transcriptions = TranscriptionsJSON FROM @Result WHERE CallId = @CallId;
    IF ISJSON(@Recordings) <> 1 OR (SELECT COUNT(*) FROM OPENJSON(@Recordings)) <> 2
        OR NOT EXISTS
        (
            SELECT 1 FROM OPENJSON(@Recordings)
            WITH (RecordingId int, RecordingStatus varchar(20), DurationSeconds int)
            WHERE RecordingId = @RecordingId AND RecordingStatus = 'completed' AND DurationSeconds = 30
        )
        OR NOT EXISTS
        (
            SELECT 1 FROM OPENJSON(@Recordings) WITH (RecordingStatus varchar(20))
            WHERE RecordingStatus = 'failed'
        )
        THROW 51000, 'All recording statuses must be returned in one call row.', 1;

    IF ISJSON(@Transcriptions) <> 1 OR (SELECT COUNT(*) FROM OPENJSON(@Transcriptions)) <> 2
        OR NOT EXISTS
        (
            SELECT 1 FROM OPENJSON(@Transcriptions)
            WITH (TranscriptionJobId int, RecordingId int, ConfigurationVersion int,
                  TranscriptionStatus varchar(20), CompletedSubmissionId int, SubmissionStatus varchar(20),
                  Messages nvarchar(max) AS JSON)
            WHERE TranscriptionJobId = @CompletedJobId AND RecordingId = @RecordingId AND ConfigurationVersion = 1
              AND TranscriptionStatus = 'COMPLETED' AND CompletedSubmissionId = @CompletedSubmissionId
              AND SubmissionStatus = 'COMPLETED'
              AND (SELECT COUNT(*) FROM OPENJSON(Messages)) = 2
        )
        OR NOT EXISTS
        (
            SELECT 1 FROM OPENJSON(@Transcriptions)
            WITH (TranscriptionJobId int, ConfigurationVersion int, TranscriptionStatus varchar(20),
                  SubmissionId int, SubmissionStatus varchar(20), SubmissionAttemptNumber int, SubmissionErrorCode varchar(100),
                  Messages nvarchar(max) AS JSON)
            WHERE TranscriptionJobId = @FailedJobId AND ConfigurationVersion = 2 AND TranscriptionStatus = 'FAILED'
              AND SubmissionId = @LatestSubmissionId AND SubmissionStatus = 'FAILED'
              AND SubmissionAttemptNumber = 2 AND SubmissionErrorCode = 'LATEST_ERROR'
              AND Messages = N'[]'
        )
        THROW 51000, 'Every transcription configuration must retain its job status and latest submission status.', 1;

    DECLARE @Messages nvarchar(max);
    SELECT @Messages = Messages
    FROM OPENJSON(@Transcriptions)
    WITH (TranscriptionJobId int, Messages nvarchar(max) AS JSON)
    WHERE TranscriptionJobId = @CompletedJobId;

    IF NOT EXISTS
    (
        SELECT 1 FROM OPENJSON(@Messages) AS message
        CROSS APPLY OPENJSON(message.value)
        WITH (Sentence int, AudioChannel int, StartTimeMilliseconds int, EndTimeMilliseconds int, TranscriptText nvarchar(max)) AS detail
        WHERE message.[key] = '0' AND detail.Sentence = 0 AND detail.AudioChannel = 0
          AND detail.StartTimeMilliseconds = 0 AND detail.EndTimeMilliseconds = 1000
          AND detail.TranscriptText = N'Hello'
    )
        OR NOT EXISTS
        (
            SELECT 1 FROM OPENJSON(@Messages) AS message
            CROSS APPLY OPENJSON(message.value)
            WITH (Sentence int, AudioChannel int, StartTimeMilliseconds int, EndTimeMilliseconds int, TranscriptText nvarchar(max)) AS detail
            WHERE message.[key] = '1' AND detail.Sentence = 1 AND detail.AudioChannel = 1
              AND detail.StartTimeMilliseconds = 1250 AND detail.EndTimeMilliseconds = 2500
              AND detail.TranscriptText = N'world'
        )
        THROW 51000, 'Messages must be ordered and retain each sentence, channel, text, and recording-relative millisecond timestamps.', 1;

    DELETE @Result;
    INSERT @Result EXEC dbo.automatedcalls_GetAllCallForPOGroup 'missing-group', @Start, @End;
    IF EXISTS (SELECT 1 FROM @Result)
        THROW 51000, 'A group without queued requests must return no rows.', 1;

    ROLLBACK TRANSACTION;
    PRINT 'POGroup call-log SQL regression checks passed; test data rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

DECLARE @Invalid TABLE (POGroupNum varchar(20), StartDate datetime, EndDate datetime);
INSERT @Invalid VALUES
    (NULL, '2026-10-17', '2026-10-18'),
    (' ', '2026-10-17', '2026-10-18'),
    ('report-test', NULL, '2026-10-18'),
    ('report-test', '2026-10-17', NULL),
    ('report-test', '2026-10-17', '2026-10-17'),
    ('report-test', '2026-10-18', '2026-10-17');
DECLARE @Group varchar(20), @InvalidStart datetime, @InvalidEnd datetime;
DECLARE InvalidArguments CURSOR LOCAL FAST_FORWARD FOR SELECT POGroupNum, StartDate, EndDate FROM @Invalid;
OPEN InvalidArguments;
FETCH NEXT FROM InvalidArguments INTO @Group, @InvalidStart, @InvalidEnd;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        EXEC dbo.automatedcalls_GetAllCallForPOGroup @Group, @InvalidStart, @InvalidEnd;
        THROW 51001, 'Invalid report arguments were accepted.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    FETCH NEXT FROM InvalidArguments INTO @Group, @InvalidStart, @InvalidEnd;
END;
CLOSE InvalidArguments;
DEALLOCATE InvalidArguments;
PRINT 'Invalid POGroup/date arguments rejected.';
