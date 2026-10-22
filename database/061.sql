SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-- AutoCallDB: call flow record step (participant leaves a spoken message).
-- Deploy after 060.sql, then the feedback API, then the worker.
-- automatedcalls_Next accepts the record step's input types: 'recording' (Twilio's <Record> action, with a
-- RecordingSid) and 'no-recording' (the step's fallthrough <Redirect>). The InputReceived event keeps the
-- recordingSid, which links each record-step recording to the step that produced it.
-- automatedcalls_ClaimNote posts record-step messages with their step and device, and skips the whole-call
-- transcript note for calls that have a record-step message. Whole-call transcription is unchanged.
IF COL_LENGTH(N'dbo.AutomatedCalls', N'AnsweredBy') IS NULL
    THROW 51000, 'Deploy 060.sql before the record step.', 1;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_Next
    @CallId int,@ExecutionId uniqueidentifier,@ProviderCallId varchar(200),@PhoneE164 varchar(16),@InputJSON nvarchar(max),
    @InputType varchar(20)=NULL,@ExpectedTwiML nvarchar(max)=NULL,@ExpectedTemplateJSON nvarchar(max)=NULL,
    @SourceStepId nvarchar(200)=NULL,@StepId nvarchar(200)=NULL,@NextExecutionId uniqueidentifier=NULL,@ResponseTwiML nvarchar(max)=NULL
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
        DECLARE @Found bit=0,@Provider varchar(100),@ExistingCallId varchar(200),@ExistingPhone varchar(16),@CallStatus varchar(20),
            @EndedDateTime datetime2(3),@TwiML nvarchar(max),@TemplateJSON nvarchar(max),@ActualSourceStepId nvarchar(200),
            @SourceEventId int,@SourceCount int,@EventJSON nvarchar(max),@EventType varchar(100),@EventFound bit=0,@Replayed bit=0,
            @Outcome varchar(30)='advanced',@ExecutionKey varchar(36)=LOWER(CONVERT(varchar(36),@ExecutionId)),
            @InlineExecutionId uniqueidentifier,@SubmissionToken uniqueidentifier,@SubmissionState varchar(20);
        SELECT @Found=1,@Provider=calls.Provider,@ExistingCallId=calls.providerCallId,@ExistingPhone=calls.PhoneE164,
            @TwiML=calls.TwiML,@TemplateJSON=templates.TemplateJSON,@CallStatus=calls.CallStatus,@EndedDateTime=calls.EndedDateTime,
            @SubmissionToken=calls.SubmissionToken,@SubmissionState=calls.SubmissionState,
            @InlineExecutionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(CASE WHEN ISJSON(calls.SubmissionRequestJSON)=1 THEN calls.SubmissionRequestJSON ELSE N'{}' END,'$.OpeningStep.ExecutionId'))
        FROM dbo.AutomatedCalls AS calls WITH(UPDLOCK,HOLDLOCK)
        LEFT JOIN dbo.CallFlowTemplates AS templates WITH(HOLDLOCK) ON templates.SystemID=calls.FlowId
        WHERE calls.SystemID=@CallId;
        IF @Found=0 SET @Outcome='not-found';
        ELSE IF @Provider IS NULL OR LOWER(@Provider)<>'twilio' SET @Outcome='provider-mismatch';
        ELSE IF @ExistingPhone IS NULL OR @ExistingPhone<>@PhoneE164 SET @Outcome='destination-mismatch';
        ELSE IF (@ExistingCallId IS NULL AND (@InlineExecutionId IS NULL OR @InlineExecutionId<>@ExecutionId OR @SubmissionToken IS NULL
            OR @SubmissionState IS NULL OR @SubmissionState NOT IN('SUBMITTING','UNKNOWN','ACCEPTED')
            OR @ProviderCallId IS NULL OR LEN(@ProviderCallId)<>34 OR LEFT(@ProviderCallId,2) COLLATE Latin1_General_100_BIN2<>'CA'
            OR SUBSTRING(@ProviderCallId,3,32) COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-fA-F]%'))
            OR (@ExistingCallId IS NOT NULL AND @ExistingCallId COLLATE Latin1_General_100_BIN2<>@ProviderCallId COLLATE Latin1_General_100_BIN2)
            SET @Outcome='call-sid-mismatch';
        IF @Outcome='advanced'
        BEGIN
            SELECT @SourceCount=COUNT(*),@SourceEventId=MAX(SystemID),@ActualSourceStepId=MAX(JSON_VALUE(CASE WHEN ISJSON(EventJSON)=1 THEN EventJSON ELSE N'{}' END,'$.stepId'))
            FROM dbo.AutomatedCallEvents WHERE CallId=@CallId AND EventType='StepIssued'
                AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(CASE WHEN ISJSON(EventJSON)=1 THEN EventJSON ELSE N'{}' END,'$.executionId'))=@ExecutionId;
            IF @SourceCount=0 SET @Outcome='execution-not-found';
            ELSE IF @SourceCount<>1 OR NULLIF(LTRIM(RTRIM(@ActualSourceStepId)),N'') IS NULL SET @Outcome='invalid-flow';
        END;
        IF @Outcome='advanced'
        BEGIN
            SELECT @EventFound=1,@EventJSON=EventJSON,@EventType=EventType FROM dbo.AutomatedCallEvents
            WHERE CallId=@CallId AND IdempotencyKey='voice:next:'+@ExecutionKey;
            IF @EventFound=1
            BEGIN
                IF @EventJSON IS NULL OR ISJSON(@EventJSON)<>1 OR @EventType IS NULL OR @EventType<>'StepTransitioned' SET @Outcome='invalid-flow';
                ELSE
                BEGIN
                    SET @StepId=NULL; SET @NextExecutionId=NULL; SET @ResponseTwiML=NULL;
                    SELECT @StepId=stepId,@NextExecutionId=TRY_CONVERT(uniqueidentifier,executionId),@ResponseTwiML=responseTwiML
                    FROM OPENJSON(@EventJSON) WITH(stepId nvarchar(200),executionId nvarchar(36),responseTwiML nvarchar(max));
                    IF @NextExecutionId IS NULL OR NULLIF(LTRIM(RTRIM(@StepId)),N'') IS NULL OR NULLIF(LTRIM(RTRIM(@ResponseTwiML)),N'') IS NULL SET @Outcome='invalid-flow';
                    ELSE SET @Replayed=1;
                END;
            END
            -- A caller who hangs up while recording ends the call; Twilio still posts the Record action, which may
            -- arrive after the final status callback. Keep that event so the message is linked to its step.
            ELSE IF (@EndedDateTime IS NOT NULL OR @CallStatus IN('completed','busy','failed','no-answer','canceled')) AND ISNULL(@InputType,'')<>'recording' SET @Outcome='call-ended';
            ELSE IF @SourceEventId<>(SELECT MAX(SystemID) FROM dbo.AutomatedCallEvents WHERE CallId=@CallId AND EventType='StepIssued') SET @Outcome='stale-execution';
            ELSE IF @NextExecutionId IS NULL OR @NextExecutionId=@ExecutionId OR NULLIF(LTRIM(RTRIM(@StepId)),N'') IS NULL
                OR NULLIF(LTRIM(RTRIM(@ResponseTwiML)),N'') IS NULL OR @ExpectedTwiML IS NULL OR @ExpectedTemplateJSON IS NULL OR @TwiML IS NULL OR @TemplateJSON IS NULL
                OR @SourceStepId IS NULL OR @InputJSON IS NULL OR ISJSON(@InputJSON)<>1 OR @InputType IS NULL OR @InputType NOT IN('redirect','dtmf','speech','no-input','recording','no-recording')
                OR (@InputType='recording' AND (JSON_VALUE(@InputJSON,'$.recordingSid') IS NULL OR LEN(JSON_VALUE(@InputJSON,'$.recordingSid'))<>34
                    OR LEFT(JSON_VALUE(@InputJSON,'$.recordingSid'),2) COLLATE Latin1_General_100_BIN2<>'RE')) SET @Outcome='invalid-flow';
            ELSE IF DATALENGTH(@TwiML)<>DATALENGTH(@ExpectedTwiML) OR @TwiML COLLATE Latin1_General_100_BIN2<>@ExpectedTwiML COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@TemplateJSON)<>DATALENGTH(@ExpectedTemplateJSON) OR @TemplateJSON COLLATE Latin1_General_100_BIN2<>@ExpectedTemplateJSON COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@ActualSourceStepId)<>DATALENGTH(@SourceStepId) OR @ActualSourceStepId COLLATE Latin1_General_100_BIN2<>@SourceStepId COLLATE Latin1_General_100_BIN2 SET @Outcome='context-changed';
            ELSE IF EXISTS(SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId=@CallId AND EventType='StepIssued'
                AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(CASE WHEN ISJSON(EventJSON)=1 THEN EventJSON ELSE N'{}' END,'$.executionId'))=@NextExecutionId) SET @Outcome='context-changed';
        END;
        IF @Outcome='advanced' AND @Replayed=0
        BEGIN
            SET @EventJSON=(SELECT @ActualSourceStepId AS stepId,CONVERT(varchar(36),@ExecutionId) AS executionId,@ProviderCallId AS providerCallId,
                @InputType AS inputType,JSON_QUERY(@InputJSON) AS input FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT dbo.AutomatedCallEvents(CallId,CallEventDateTime,EventType,EventJSON,IdempotencyKey)
            VALUES(@CallId,SYSUTCDATETIME(),'InputReceived',@EventJSON,'voice:input:'+@ExecutionKey);
            SET @EventJSON=(SELECT @ActualSourceStepId AS fromStepId,CONVERT(varchar(36),@ExecutionId) AS fromExecutionId,@StepId AS stepId,
                CONVERT(varchar(36),@NextExecutionId) AS executionId,@ProviderCallId AS providerCallId,@InputType AS inputType,@ResponseTwiML AS responseTwiML FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT dbo.AutomatedCallEvents(CallId,CallEventDateTime,EventType,EventJSON,IdempotencyKey)
            VALUES(@CallId,SYSUTCDATETIME(),'StepTransitioned',@EventJSON,'voice:next:'+@ExecutionKey);
            SET @EventJSON=(SELECT @StepId AS stepId,CONVERT(varchar(36),@NextExecutionId) AS executionId,CONVERT(varchar(36),@ExecutionId) AS sourceExecutionId,
                @ProviderCallId AS providerCallId,@ResponseTwiML AS responseTwiML FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            INSERT dbo.AutomatedCallEvents(CallId,CallEventDateTime,EventType,EventJSON,IdempotencyKey)
            VALUES(@CallId,SYSUTCDATETIME(),'StepIssued',@EventJSON,'voice:step:'+LOWER(CONVERT(varchar(36),@NextExecutionId)));
            UPDATE dbo.AutomatedCalls SET providerCallId=COALESCE(providerCallId,@ProviderCallId),
                CallStatus=CASE WHEN @InlineExecutionId=@ExecutionId AND (CallStatus IS NULL OR CallStatus IN('queued','initiated','ringing')) THEN 'in-progress' ELSE CallStatus END,
                StartedDateTime=CASE WHEN @InlineExecutionId=@ExecutionId THEN COALESCE(StartedDateTime,SYSUTCDATETIME()) ELSE StartedDateTime END,
                UpdatedDateTime=SYSUTCDATETIME() WHERE SystemID=@CallId;
        END;
        COMMIT;
        SELECT @Outcome AS Outcome,CASE WHEN @Outcome='advanced' THEN @ResponseTwiML END AS ResponseTwiML,
            CASE WHEN @Outcome='advanced' THEN @StepId END AS StepId,CASE WHEN @Outcome='advanced' THEN @NextExecutionId END AS ExecutionId,@Replayed AS Replayed;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_ClaimNote
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    DECLARE @Claimed TABLE
    (
        SystemID bigint, DeliveryId uniqueidentifier, LeaseToken uniqueidentifier, CallId int, Kind varchar(20),
        OID varchar(20), HistoryID int, OccurredAt datetime2(3), RecordingId int, MessagesJSON nvarchar(max),
        QueueId int, ActiveAlarmID int
    );
    -- Transcripts follow the completion note, which unsuccessful and machine-answered calls never get.
    UPDATE o SET Status = 'SKIPPED', LastErrorCode = 'CALL_NOT_COMPLETED'
    FROM dbo.AutomatedCallNoteOutbox o JOIN dbo.AutomatedCalls c ON c.SystemID = o.CallId
    WHERE o.Status = 'READY' AND o.Kind = 'TRANSCRIPT'
      AND (c.CallStatus IN ('failed','busy','no-answer','canceled') OR c.AnsweredBy LIKE 'machine[_]%' OR c.AnsweredBy = 'fax');
    -- A call with a record-step message posts only the message transcripts. The whole-call recording and
    -- its transcript are still stored; only its history note is skipped.
    UPDATE o SET Status = 'SKIPPED', LastErrorCode = 'RECORD_STEP_MESSAGE'
    FROM dbo.AutomatedCallNoteOutbox o
    LEFT JOIN dbo.AutomatedCallRecordings r ON r.SystemID = o.RecordingId
    WHERE o.Status = 'READY' AND o.Kind = 'TRANSCRIPT' AND ISNULL(r.RecordingSource, '') <> 'RecordVerb'
      AND (EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings m WHERE m.CallId = o.CallId AND m.RecordingSource = 'RecordVerb')
           OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents e WHERE e.CallId = o.CallId AND e.EventType = 'InputReceived'
                      AND JSON_VALUE(CASE WHEN ISJSON(e.EventJSON) = 1 THEN e.EventJSON ELSE N'{}' END, '$.inputType') = 'recording'));
    UPDATE dbo.AutomatedCallNoteOutbox SET Status = 'DEAD_LETTER', LastErrorCode = 'DELIVERY_ATTEMPTS_EXHAUSTED'
    WHERE AttemptCount >= 10 AND (Status = 'READY' OR (Status = 'PROCESSING' AND LockedUntil <= @Now));
    ;WITH NextNote AS
    (
        SELECT TOP (1) o.* FROM dbo.AutomatedCallNoteOutbox o WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE o.AttemptCount < 10 AND
            ((o.Status = 'READY' AND o.AvailableAt <= @Now) OR (o.Status = 'PROCESSING' AND o.LockedUntil <= @Now))
          AND (o.Kind IN ('COMPLETED', 'RELEASE', 'DEVICE') OR
               (EXISTS (SELECT 1 FROM dbo.AutomatedCalls c WHERE c.SystemID = o.CallId AND c.CallStatus = 'completed')
                AND EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox completion
                    WHERE completion.CallId = o.CallId AND completion.Kind = 'COMPLETED' AND completion.Status = 'DELIVERED')))
        ORDER BY o.SystemID
    )
    UPDATE NextNote SET Status = 'PROCESSING', LeaseToken = NEWID(), LockedUntil = DATEADD(second, 120, @Now),
        AttemptCount = AttemptCount + 1
    OUTPUT inserted.SystemID, inserted.DeliveryId, inserted.LeaseToken, inserted.CallId, inserted.Kind,
        inserted.OID, inserted.HistoryID, inserted.OccurredAt, inserted.RecordingId, inserted.MessagesJSON,
        inserted.QueueId, inserted.ActiveAlarmID
    INTO @Claimed;
    SELECT n.SystemID, n.DeliveryId, n.LeaseToken, n.CallId, n.Kind, n.OID, n.HistoryID, n.OccurredAt, n.RecordingId,
        n.MessagesJSON, n.QueueId, n.ActiveAlarmID,
        r.RecordingSource, c.PhoneType,
        (SELECT TOP (1) JSON_VALUE(e.EventJSON, '$.stepId')
         FROM dbo.AutomatedCallEvents e
         WHERE e.CallId = n.CallId AND e.EventType = 'InputReceived'
           AND JSON_VALUE(CASE WHEN ISJSON(e.EventJSON) = 1 THEN e.EventJSON ELSE N'{}' END, '$.inputType') = 'recording'
           AND JSON_VALUE(CASE WHEN ISJSON(e.EventJSON) = 1 THEN e.EventJSON ELSE N'{}' END, '$.input.recordingSid')
               COLLATE Latin1_General_100_BIN2 = r.RecordingSid COLLATE Latin1_General_100_BIN2
         ORDER BY e.SystemID) AS MessageStepId
    FROM @Claimed n
    LEFT JOIN dbo.AutomatedCallRecordings r ON n.Kind = 'TRANSCRIPT' AND r.SystemID = n.RecordingId
    LEFT JOIN dbo.AutomatedCalls c ON c.SystemID = n.CallId;
END;
GO
