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
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, POGroupNum) VALUES (42, 'report-test');
    DECLARE @QueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID, POGroupNum) VALUES (43, 'report-test-child');
    DECLARE @OtherQueueId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue (ActiveAlarmID) VALUES (44);
    DECLARE @UnassignedQueueId int = SCOPE_IDENTITY();

    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, CallStatus, CreatedDateTime)
    VALUES (@QueueId, 'Twilio', 'completed', @Start);
    DECLARE @CallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, CallStatus, CreatedDateTime)
    VALUES (@QueueId, 'Twilio', 'no-answer', '2026-10-17T12:00:00');
    DECLARE @NoMediaCallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, CallStatus, CreatedDateTime)
    VALUES (@QueueId, 'Twilio', 'failed', DATEADD(millisecond, -1, CONVERT(datetime2(3), @End)));
    DECLARE @LastIncludedCallId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls (AutomatedCallQueueId, Provider, CallStatus, CreatedDateTime)
    VALUES (@QueueId, 'Twilio', 'completed', DATEADD(second, -1, @Start)),
           (@QueueId, 'Twilio', 'completed', @End),
           (@OtherQueueId, 'Twilio', 'completed', @Start),
           (@UnassignedQueueId, 'Twilio', 'completed', @Start),
           (NULL, 'Twilio', 'completed', @Start);

    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus, DurationSeconds)
    VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'completed', 30);
    DECLARE @RecordingId int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallRecordings (CallId, Provider, AccountSid, RecordingSid, RecordingStatus)
    VALUES (@CallId, 'Twilio', 'ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'REcccccccccccccccccccccccccccccccc', 'failed');

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
        RecordingsJSON nvarchar(max), TranscriptionsJSON nvarchar(max)
    );
    INSERT @Result EXEC dbo.automatedcalls_GetAllCallForPOGroup 'report-test', @Start, @End;

    IF (SELECT COUNT(*) FROM @Result) <> 3
        OR NOT EXISTS (SELECT 1 FROM @Result WHERE CallId = @CallId AND CreatedDateTime = @Start)
        OR NOT EXISTS (SELECT 1 FROM @Result WHERE CallId = @LastIncludedCallId)
        OR EXISTS (SELECT CallId FROM @Result GROUP BY CallId HAVING COUNT(*) > 1)
        OR EXISTS (SELECT 1 FROM @Result WHERE AutomatedCallQueueId <> @QueueId OR POGroupNum <> 'report-test')
        THROW 51000, 'Call rows must use exact queue groups and inclusive-start/exclusive-end UTC dates without duplication.', 1;

    IF NOT EXISTS
    (
        SELECT 1 FROM @Result
        WHERE CallId = @NoMediaCallId AND CallStatus = 'no-answer'
          AND RecordingsJSON = N'[]' AND TranscriptionsJSON = N'[]'
    )
        THROW 51000, 'Calls without recordings/transcription must remain visible with empty arrays.', 1;

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
                  TranscriptionStatus varchar(20), CompletedSubmissionId int, SubmissionStatus varchar(20))
            WHERE TranscriptionJobId = @CompletedJobId AND RecordingId = @RecordingId AND ConfigurationVersion = 1
              AND TranscriptionStatus = 'COMPLETED' AND CompletedSubmissionId = @CompletedSubmissionId
              AND SubmissionStatus = 'COMPLETED'
        )
        OR NOT EXISTS
        (
            SELECT 1 FROM OPENJSON(@Transcriptions)
            WITH (TranscriptionJobId int, ConfigurationVersion int, TranscriptionStatus varchar(20),
                  SubmissionId int, SubmissionStatus varchar(20), SubmissionAttemptNumber int, SubmissionErrorCode varchar(100))
            WHERE TranscriptionJobId = @FailedJobId AND ConfigurationVersion = 2 AND TranscriptionStatus = 'FAILED'
              AND SubmissionId = @LatestSubmissionId AND SubmissionStatus = 'FAILED'
              AND SubmissionAttemptNumber = 2 AND SubmissionErrorCode = 'LATEST_ERROR'
        )
        THROW 51000, 'Every transcription configuration must retain its job status and latest submission status.', 1;

    DELETE @Result;
    INSERT @Result EXEC dbo.automatedcalls_GetAllCallForPOGroup 'missing-group', @Start, @End;
    IF EXISTS (SELECT 1 FROM @Result)
        THROW 51000, 'A group without calls must return no rows.', 1;

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
