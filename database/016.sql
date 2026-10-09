SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_BeginSubmission
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100),@CallId int,
    @SubmissionToken uniqueidentifier,@RequestJSON nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @SubmissionToken IS NULL OR @SubmissionToken='00000000-0000-0000-0000-000000000000'
        OR @RequestJSON IS NULL OR ISJSON(@RequestJSON)<>1 OR LEFT(LTRIM(@RequestJSON),1)<>'{'
        THROW 51000,'A submission token and request snapshot are required.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Status varchar(20),@QueueToken uniqueidentifier,@Owner varchar(100),@Until datetime2(3),
            @Found bit=0,@State varchar(20),@Sid varchar(200),@Phone varchar(16),@Xml nvarchar(max),@Parameters nvarchar(max),
            @CallStatus varchar(20),@Token uniqueidentifier,@ShouldSubmit bit=0,@StoredRequest nvarchar(max),
            @OpeningStepId nvarchar(200),@OpeningExecutionId uniqueidentifier,@OpeningResponse nvarchar(max),
            @BundleXml xml,@ResponseXml xml,@ExpectedResponse xml;
        SELECT @Found=1,@State=SubmissionState,@Token=SubmissionToken,@Sid=providerCallId,@Phone=PhoneE164,
            @Xml=TwiML,@Parameters=Parameters,@CallStatus=CallStatus,@StoredRequest=SubmissionRequestJSON
        FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@CallId AND AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken AND Provider='Twilio';
        IF @Found=0 THROW 51002,'Attempt does not belong to the claim.',1;
        SELECT @Status=Status,@QueueToken=LeaseToken,@Owner=LockedBy,@Until=LockedUntil
        FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@QueueId;
        IF @Sid IS NOT NULL AND (@State IS NULL OR @State IN('READY','SUBMITTING'))
        BEGIN
            UPDATE dbo.AutomatedCalls SET SubmissionState='ACCEPTED',SubmissionCompletedAt=COALESCE(SubmissionCompletedAt,SYSUTCDATETIME()) WHERE SystemID=@CallId;
            SET @State='ACCEPTED';
        END;
        IF @State='READY' AND @Sid IS NULL
        BEGIN
            IF @Status IS NULL OR @Status<>'PROCESSING' OR @QueueToken IS NULL OR @QueueToken<>@LeaseToken
                OR @Owner IS NULL OR @WorkerId IS NULL OR @Owner COLLATE Latin1_General_100_BIN2<>@WorkerId COLLATE Latin1_General_100_BIN2
                OR @Until IS NULL OR @Until<=SYSUTCDATETIME()
                THROW 51002,'Submission requires current queue lease ownership.',1;
            IF @Xml IS NULL OR @Parameters IS NULL OR @CallStatus IS NULL OR @CallStatus<>'queued'
                THROW 51000,'Only a persisted prepared attempt may be submitted.',1;
            IF TRY_CONVERT(int,JSON_VALUE(@RequestJSON,'$.CallId')) IS NULL OR TRY_CONVERT(int,JSON_VALUE(@RequestJSON,'$.CallId'))<>@CallId
                OR JSON_VALUE(@RequestJSON,'$.To') IS NULL OR JSON_VALUE(@RequestJSON,'$.To')<>@Phone
                THROW 51000,'Submission request identity does not match the attempt.',1;
            IF JSON_QUERY(@RequestJSON,'$.OpeningStep') IS NOT NULL
            BEGIN
                SELECT @OpeningStepId=StepId,@OpeningExecutionId=TRY_CONVERT(uniqueidentifier,ExecutionId),@OpeningResponse=ResponseTwiML
                FROM OPENJSON(@RequestJSON,'$.OpeningStep') WITH(StepId nvarchar(200),ExecutionId nvarchar(100),ResponseTwiML nvarchar(max));
                SET @BundleXml=TRY_CONVERT(xml,@Xml);
                SET @ResponseXml=TRY_CONVERT(xml,@OpeningResponse);
                IF NULLIF(LTRIM(RTRIM(@OpeningStepId)),N'') IS NULL OR @OpeningExecutionId IS NULL
                    OR @OpeningExecutionId='00000000-0000-0000-0000-000000000000' OR @BundleXml IS NULL OR @ResponseXml IS NULL
                    OR @ResponseXml.value('count(/*)','int')<>1 OR @ResponseXml.exist('/Response')<>1
                    OR @OpeningResponse LIKE N'%{{%' OR @BundleXml.value('(/CallFlow/@initialStep)[1]','nvarchar(200)') COLLATE Latin1_General_100_BIN2<>@OpeningStepId COLLATE Latin1_General_100_BIN2
                    OR @BundleXml.value('count(/CallFlow/Step[@id=sql:variable("@OpeningStepId")]/Response)','int')<>1
                    THROW 51000,'Inline opening step is invalid.',1;
                SET @ExpectedResponse=TRY_CONVERT(xml,REPLACE(CONVERT(nvarchar(max),@BundleXml.query('/CallFlow/Step[@id=sql:variable("@OpeningStepId")]/Response')),
                    N'{{executionId}}',LOWER(CONVERT(nvarchar(36),@OpeningExecutionId))));
                IF @ExpectedResponse IS NULL OR CONVERT(nvarchar(max),@ExpectedResponse) COLLATE Latin1_General_100_BIN2<>CONVERT(nvarchar(max),@ResponseXml) COLLATE Latin1_General_100_BIN2
                    THROW 51001,'Inline response does not match the prepared opening step.',1;
                IF EXISTS(SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId=@CallId AND IdempotencyKey='voice:start')
                    THROW 51001,'Opening execution has already been issued.',1;
            END;
            UPDATE dbo.AutomatedCalls SET SubmissionState='SUBMITTING',SubmissionToken=@SubmissionToken,
                SubmissionStartedAt=SYSUTCDATETIME(),SubmissionRequestJSON=@RequestJSON,SubmissionErrorCode=NULL WHERE SystemID=@CallId;
            IF @OpeningExecutionId IS NOT NULL
            BEGIN
                DECLARE @EventJSON nvarchar(max)=(SELECT @OpeningStepId AS stepId,LOWER(CONVERT(nvarchar(36),@OpeningExecutionId)) AS executionId,
                    @OpeningResponse AS responseTwiML FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
                INSERT dbo.AutomatedCallEvents(CallId,CallEventDateTime,EventType,EventJSON,IdempotencyKey)
                VALUES(@CallId,SYSUTCDATETIME(),'StepIssued',@EventJSON,'voice:start');
            END;
            SET @StoredRequest=@RequestJSON; SET @State='SUBMITTING'; SET @Token=@SubmissionToken; SET @ShouldSubmit=1;
            IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue lease expired before submission.',1;
        END;
        COMMIT;
        SELECT @ShouldSubmit AS ShouldSubmit,COALESCE(@State,'LEGACY') AS SubmissionState,@Token AS SubmissionToken,@Sid AS ProviderCallId,
            TRY_CONVERT(uniqueidentifier,JSON_VALUE(CASE WHEN ISJSON(@StoredRequest)=1 THEN @StoredRequest ELSE N'{}' END,'$.OpeningStep.ExecutionId')) AS OpeningExecutionId;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
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
            ELSE IF @EndedDateTime IS NOT NULL OR @CallStatus IN('completed','busy','failed','no-answer','canceled') SET @Outcome='call-ended';
            ELSE IF @SourceEventId<>(SELECT MAX(SystemID) FROM dbo.AutomatedCallEvents WHERE CallId=@CallId AND EventType='StepIssued') SET @Outcome='stale-execution';
            ELSE IF @NextExecutionId IS NULL OR @NextExecutionId=@ExecutionId OR NULLIF(LTRIM(RTRIM(@StepId)),N'') IS NULL
                OR NULLIF(LTRIM(RTRIM(@ResponseTwiML)),N'') IS NULL OR @ExpectedTwiML IS NULL OR @ExpectedTemplateJSON IS NULL OR @TwiML IS NULL OR @TemplateJSON IS NULL
                OR @SourceStepId IS NULL OR @InputJSON IS NULL OR ISJSON(@InputJSON)<>1 OR @InputType IS NULL OR @InputType NOT IN('redirect','dtmf','speech','no-input') SET @Outcome='invalid-flow';
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