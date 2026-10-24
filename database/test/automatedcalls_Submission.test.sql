SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
IF DB_NAME() NOT IN(N'NotificationWorkflowVoiceTests',N'NotificationWorkflowTranscriptionSchemaTests')
    THROW 51000,'Owned scratch database required.',1;
BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.Client(OID) VALUES('submission-test');
    INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('submission-test',N'test');
    DECLARE @Flow int=SCOPE_IDENTITY(),@Lease uniqueidentifier=NEWID(),@Send uniqueidentifier=NEWID(),@OtherSend uniqueidentifier=NEWID();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@Flow,1,N'{"schemaVersion":1}',N'<CallFlow/>',N'test');
    DECLARE @FlowVersion int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil)
    VALUES(42,'submission-test',@Flow,@FlowVersion,N'{}',NEWID(),'PROCESSING',1,@Lease,'submission-worker',DATEADD(minute,5,SYSUTCDATETIME()));
    DECLARE @Queue int=SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,TwiML,Parameters)
    VALUES(@Queue,@Lease,'Twilio','submission-test','+15551234567',@Flow,@FlowVersion,'queued',N'<CallFlow/>',N'{}');
    DECLARE @Call int=SCOPE_IDENTITY(),@Request nvarchar(max),@Now datetime2(3)=SYSUTCDATETIME();
    SET @Request=(SELECT @Call AS CallId,'+15551234567' AS [To] FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    DECLARE @Result TABLE(ShouldSubmit bit,SubmissionState varchar(20),SubmissionToken uniqueidentifier,ProviderCallId varchar(200),OpeningExecutionId uniqueidentifier);
    INSERT @Result EXEC dbo.automatedcalls_BeginSubmission @Queue,@Lease,'submission-worker',@Call,@Send,@Request;
    IF NOT EXISTS(SELECT 1 FROM @Result WHERE ShouldSubmit=1 AND SubmissionState='SUBMITTING' AND SubmissionToken=@Send)
        THROW 51000,'First submission must own the durable send marker.',1;
    DELETE @Result;
    INSERT @Result EXEC dbo.automatedcalls_BeginSubmission @Queue,@Lease,'submission-worker',@Call,@OtherSend,@Request;
    IF NOT EXISTS(SELECT 1 FROM @Result WHERE ShouldSubmit=0 AND SubmissionToken=@Send)
        THROW 51000,'Replayed/in-flight submission must not send twice.',1;
    EXEC dbo.automatedcalls_Status @Call,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567','in-progress',0,@Now;
    EXEC dbo.automatedcalls_SaveSubmission @Queue,@Lease,@Call,@Send,'ACCEPTED','CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND SubmissionState='ACCEPTED' AND CallStatus='in-progress' AND providerCallId='CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Queue AND Status='PROCESSING' AND LeaseToken=@Lease)
        THROW 51000,'Acceptance must preserve early SID/status and leave the queue processing.',1;
    EXEC dbo.automatedcalls_Status @Call,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567','completed',1,@Now;
    EXEC dbo.automatedcalls_SaveSubmission @Queue,@Lease,@Call,@Send,'ACCEPTED','CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Queue AND Status='COMPLETED' AND LeaseToken IS NULL)
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND CallStatus='completed' AND SubmissionState='ACCEPTED')
        THROW 51000,'Submission response must still save after terminal callback clears queue ownership.',1;
    EXEC dbo.automatedcalls_SaveSubmission @Queue,@Lease,@Call,@Send,'ACCEPTED','CAbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND providerCallId='CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND CallStatus='completed' AND SubmissionState='UNKNOWN' AND SubmissionErrorCode='SID_CONFLICT')
        THROW 51000,'Conflicting response SID must not overwrite callback ownership.',1;

    DECLARE @Case int=1,@Bundle nvarchar(max)=N'<CallFlow initialStep="hello"><Step id="hello"><Response><Hangup/></Response></Step></CallFlow>',@Execution uniqueidentifier=NEWID();
    WHILE @Case<=7
    BEGIN
        SET @Lease=NEWID(); SET @Send=NEWID();
        INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil)
        VALUES(42,'submission-test',@Flow,@FlowVersion,N'{}',NEWID(),'PROCESSING',1,@Lease,'submission-worker',DATEADD(minute,5,SYSUTCDATETIME()));
        SET @Queue=SCOPE_IDENTITY();
        INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,TwiML,Parameters)
        VALUES(@Queue,@Lease,'Twilio','submission-test','+15551234567',@Flow,@FlowVersion,'queued',@Bundle,N'{}');
        SET @Call=SCOPE_IDENTITY();
        SET @Request=(SELECT @Call AS CallId,'+15551234567' AS [To] FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
        IF @Case=5 UPDATE dbo.AutomatedCalls SET SubmissionState=NULL WHERE SystemID=@Call;
        IF @Case=7 EXEC dbo.automatedcalls_Status @Call,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567','ringing',0,@Now;
        DELETE @Result;
        INSERT @Result EXEC dbo.automatedcalls_BeginSubmission @Queue,@Lease,'submission-worker',@Call,@Send,@Request;
        IF @Case=5
        BEGIN
            IF EXISTS(SELECT 1 FROM @Result WHERE ShouldSubmit=1) OR EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND SubmissionToken IS NOT NULL)
                THROW 51000,'Legacy attempts must never be guessed safe to submit.',1;
        END
            ELSE IF @Case=7
            BEGIN
                IF NOT EXISTS(SELECT 1 FROM @Result WHERE ShouldSubmit=0 AND SubmissionState='ACCEPTED' AND ProviderCallId='CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
                THROW 51000,'An already provider-bound attempt must never be redialed.',1;
            END
            ELSE IF @Case=6
            BEGIN
                EXEC dbo.automatedcalls_SaveSubmission @Queue,@Lease,@Call,@Send,'ACCEPTED','CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
                IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND SubmissionState='ACCEPTED' AND providerCallId='CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND CallStatus='queued')
                OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Queue AND Status='PROCESSING' AND LeaseToken=@Lease)
                THROW 51000,'Normal response SID binding must not advance the call or queue lifecycle.',1;
            END
        ELSE IF @Case=1
        BEGIN
            EXEC dbo.automatedcalls_Status @Call,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567','completed',1,@Now;
            EXEC dbo.automatedcalls_SaveSubmission @Queue,@Lease,@Call,@Send,'ACCEPTED','CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
            IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND CallStatus='completed' AND SubmissionState='ACCEPTED')
                OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Queue AND Status='COMPLETED' AND LeaseToken IS NULL)
                THROW 51000,'Terminal callback before first response save must remain authoritative.',1;
        END
        ELSE IF @Case=2
        BEGIN
            EXEC dbo.automatedcalls_Start @Call,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567',@ExpectedTwiML=@Bundle,@StepId=N'hello',@ExecutionId=@Execution,@ResponseTwiML=N'<Response><Hangup/></Response>';
            DECLARE @Before nvarchar(max)=(SELECT CallStatus,StartedDateTime,UpdatedDateTime,Parameters FROM dbo.AutomatedCalls WHERE SystemID=@Call FOR JSON PATH);
            EXEC dbo.automatedcalls_SaveSubmission @Queue,@Lease,@Call,@Send,'ACCEPTED','CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
            IF @Before<>(SELECT CallStatus,StartedDateTime,UpdatedDateTime,Parameters FROM dbo.AutomatedCalls WHERE SystemID=@Call FOR JSON PATH)
                OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND providerCallId='CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND SubmissionState='ACCEPTED')
                THROW 51000,'Response save must preserve all start callback lifecycle fields.',1;
        END
        ELSE
        BEGIN
            DECLARE @Outcome varchar(20)=CASE WHEN @Case=3 THEN 'REJECTED' ELSE 'UNKNOWN' END;
            EXEC dbo.automatedcalls_SaveSubmission @Queue,@Lease,@Call,@Send,@Outcome,NULL,'HTTP_500';
            IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@Call AND CallStatus='queued' AND providerCallId IS NULL AND SubmissionState=@Outcome)
                OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Queue AND Status='PROCESSING' AND LeaseToken=@Lease)
                THROW 51000,'Submission failures must remain separate from call and queue lifecycle.',1;
            DELETE @Result;
            INSERT @Result EXEC dbo.automatedcalls_BeginSubmission @Queue,@Lease,'submission-worker',@Call,@OtherSend,@Request;
            IF EXISTS(SELECT 1 FROM @Result WHERE ShouldSubmit=1) THROW 51000,'Failure replay must not authorize another POST.',1;
        END;
        SET @Case+=1;
    END;
    ROLLBACK;
    PRINT 'Submission SQL checks passed: one send marker, early callback binding, separate lifecycle state, callback-only queue finalization, late save and SID conflict; fixtures rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;

DECLARE @Guard int=1;
WHILE @Guard<=6
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;
        INSERT dbo.Client(OID) VALUES('submission-guard');
        INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('submission-guard',N'test');
        DECLARE @GuardFlow int=SCOPE_IDENTITY(),@GuardLease uniqueidentifier=NEWID(),@GuardToken uniqueidentifier=NEWID(),@WrongToken uniqueidentifier=NEWID();
        INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@GuardFlow,1,N'{}',N'<CallFlow/>',N'test');
        DECLARE @GuardFlowVersion int = SCOPE_IDENTITY();
        INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil)
        VALUES(42,'submission-guard',@GuardFlow,@GuardFlowVersion,N'{}',NEWID(),'PROCESSING',1,@GuardLease,'submission-worker',DATEADD(minute,5,SYSUTCDATETIME()));
        DECLARE @GuardQueue int=SCOPE_IDENTITY();
        INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,TwiML,Parameters)
        VALUES(@GuardQueue,@GuardLease,'Twilio','submission-guard','+15551234567',@GuardFlow,@GuardFlowVersion,'queued',N'<CallFlow/>',N'{}');
        DECLARE @GuardCall int=SCOPE_IDENTITY(),@GuardRequest nvarchar(max),@GuardWorker varchar(100)='submission-worker';
        SET @GuardRequest=(SELECT @GuardCall AS CallId,'+15551234567' AS [To] FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
        IF @Guard=1 SET @GuardWorker='SUBMISSION-WORKER';
        IF @Guard=2 UPDATE dbo.AutomatedCallQueue SET LockedUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@GuardQueue;
        IF @Guard=4 UPDATE dbo.AutomatedCalls SET Parameters=NULL WHERE SystemID=@GuardCall;
        IF @Guard>=5
        BEGIN
            UPDATE dbo.AutomatedCalls SET TwiML=N'<CallFlow initialStep="hello"><Step id="hello"><Response><Say>Hello.</Say></Response></Step></CallFlow>' WHERE SystemID=@GuardCall;
            DECLARE @GuardOpening nvarchar(max),@GuardResponse nvarchar(max)=CASE WHEN @Guard=5 THEN N'<Response><Say>Changed.</Say></Response>' ELSE N'<Response><Say>Hello.</Say></Response>' END;
            SET @GuardOpening=(SELECT N'hello' AS StepId,LOWER(CONVERT(nvarchar(36),@GuardToken)) AS ExecutionId,@GuardResponse AS ResponseTwiML FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            SET @GuardRequest=(SELECT @GuardCall AS CallId,'+15551234567' AS [To],JSON_QUERY(@GuardOpening) AS OpeningStep FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
            IF @Guard=6
            BEGIN
                IF OBJECT_ID('dbo.test_InlineSubmissionFailure','TR') IS NOT NULL THROW 51997,'Refusing to overwrite an existing test trigger.',1;
                EXEC(N'CREATE TRIGGER dbo.test_InlineSubmissionFailure ON dbo.AutomatedCallEvents AFTER INSERT AS BEGIN THROW 51998,''Forced opening event persistence failure.'',1; END;');
            END;
        END;
        EXEC dbo.automatedcalls_BeginSubmission @GuardQueue,@GuardLease,@GuardWorker,@GuardCall,@GuardToken,@GuardRequest;
        IF @Guard=3 EXEC dbo.automatedcalls_SaveSubmission @GuardQueue,@GuardLease,@GuardCall,@WrongToken,'ACCEPTED','CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
        THROW 51999,'Invalid submission ownership/preparation unexpectedly succeeded.',1;
    END TRY
    BEGIN CATCH
        DECLARE @GuardError int=ERROR_NUMBER();
        IF XACT_STATE()<>0 ROLLBACK;
        IF (@Guard<=3 AND @GuardError<>51002) OR (@Guard=4 AND @GuardError<>51000)
            OR (@Guard=5 AND @GuardError<>51001) OR (@Guard=6 AND @GuardError<>51998) THROW;
        IF EXISTS(SELECT 1 FROM dbo.Client WHERE OID='submission-guard') THROW 51999,'Failure guard did not roll back its fixture transaction.',1;
        IF @Guard=6 AND OBJECT_ID('dbo.test_InlineSubmissionFailure','TR') IS NOT NULL THROW 51999,'Failed opening event did not roll back its trigger fixture.',1;
    END CATCH;
    SET @Guard+=1;
END;
PRINT 'Submission ownership checks passed: exact worker, expired lease, immutable submission token, persisted preparation, unchanged inline response and atomic event-write rollback; fixtures rolled back.';

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.Client(OID) VALUES('inline-test');
    DECLARE @InlineGraph nvarchar(max)=N'{"schemaVersion":1,"initialStep":"hello","steps":[{"id":"hello","type":"redirect","transitions":{"next":"end"}},{"id":"end","type":"terminal"}]}',
        @InlineBundle nvarchar(max)=N'<CallFlow initialStep="hello"><Step id="hello"><Response><Say>Alex &amp; Co</Say><Redirect>https://feedback.example/next?executionId={{executionId}}</Redirect></Response></Step><Step id="end"><Response><Hangup /></Response></Step></CallFlow>',
        @InlineLease uniqueidentifier=NEWID(),@InlineToken uniqueidentifier=NEWID(),@InlineExecution uniqueidentifier=NEWID(),@InlineNext uniqueidentifier=NEWID();
    INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('inline-test',N'test');
    DECLARE @InlineFlow int=SCOPE_IDENTITY();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@InlineFlow,1,@InlineGraph,@InlineBundle,N'test');
    DECLARE @InlineFlowVersion int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil)
    VALUES(42,'inline-test',@InlineFlow,@InlineFlowVersion,N'{}',NEWID(),'PROCESSING',1,@InlineLease,'inline-worker',DATEADD(minute,5,SYSUTCDATETIME()));
    DECLARE @InlineQueue int=SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,TwiML,Parameters)
    VALUES(@InlineQueue,@InlineLease,'Twilio','inline-test','+15551234567',@InlineFlow,@InlineFlowVersion,'queued',@InlineBundle,N'{}');
    DECLARE @InlineCall int=SCOPE_IDENTITY(),@InlineResponse nvarchar(max),@InlineRequest nvarchar(max),@InlineOpening nvarchar(max);
    SET @InlineResponse=N'<Response><Say>Alex &amp; Co</Say><Redirect>https://feedback.example/next?executionId='+LOWER(CONVERT(nvarchar(36),@InlineExecution))+N'</Redirect></Response>';
    SET @InlineOpening=(SELECT N'hello' AS StepId,LOWER(CONVERT(nvarchar(36),@InlineExecution)) AS ExecutionId,@InlineResponse AS ResponseTwiML FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    SET @InlineRequest=(SELECT @InlineCall AS CallId,'+15551234567' AS [To],JSON_QUERY(@InlineOpening) AS OpeningStep FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    DECLARE @InlineResult TABLE(ShouldSubmit bit,SubmissionState varchar(20),SubmissionToken uniqueidentifier,ProviderCallId varchar(200),OpeningExecutionId uniqueidentifier);
    INSERT @InlineResult EXEC dbo.automatedcalls_BeginSubmission @InlineQueue,@InlineLease,'inline-worker',@InlineCall,@InlineToken,@InlineRequest;
    IF NOT EXISTS(SELECT 1 FROM @InlineResult WHERE ShouldSubmit=1 AND OpeningExecutionId=@InlineExecution)
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId=@InlineCall AND EventType='StepIssued' AND IdempotencyKey='voice:start'
            AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(EventJSON,'$.executionId'))=@InlineExecution AND JSON_VALUE(EventJSON,'$.responseTwiML')=@InlineResponse)
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@InlineCall AND CallStatus='queued' AND providerCallId IS NULL AND StartedDateTime IS NULL)
        THROW 51999,'Inline send marker must atomically persist the opening execution without pretending the call was answered.',1;
    DELETE @InlineResult;
    INSERT @InlineResult EXEC dbo.automatedcalls_BeginSubmission @InlineQueue,@InlineLease,'inline-worker',@InlineCall,@InlineToken,@InlineRequest;
    IF EXISTS(SELECT 1 FROM @InlineResult WHERE ShouldSubmit=1) OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId=@InlineCall)<>1
        THROW 51999,'Inline submission replay issued another opening or send permission.',1;
    DECLARE @InlineSteps TABLE(Outcome varchar(30),ResponseTwiML nvarchar(max),StepId nvarchar(200),ExecutionId uniqueidentifier,Replayed bit);
    INSERT @InlineSteps EXEC dbo.automatedcalls_Start @InlineCall,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567';
    IF NOT EXISTS(SELECT 1 FROM @InlineSteps WHERE Outcome='started' AND Replayed=1 AND ExecutionId=@InlineExecution AND ResponseTwiML=@InlineResponse)
        THROW 51999,'Start endpoint must replay the same inline opening response.',1;
    DELETE @InlineSteps;
    DECLARE @WrongExecution uniqueidentifier=NEWID();
    INSERT @InlineSteps EXEC dbo.automatedcalls_Next @InlineCall,@WrongExecution,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567',N'{}',
        'redirect',@InlineBundle,@InlineGraph,N'hello',N'end',@InlineNext,N'<Response><Hangup /></Response>';
    IF NOT EXISTS(SELECT 1 FROM @InlineSteps WHERE Outcome='call-sid-mismatch')
        OR EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@InlineCall AND providerCallId IS NOT NULL)
        THROW 51999,'An unrelated execution must not bind an early inline SID.',1;
    DELETE @InlineSteps;
    DECLARE @InlineStaleGraph nvarchar(max)=@InlineGraph+N' ';
    INSERT @InlineSteps EXEC dbo.automatedcalls_Next @InlineCall,@InlineExecution,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567',N'{}',
        'redirect',@InlineBundle,@InlineStaleGraph,N'hello',N'end',@InlineNext,N'<Response><Hangup /></Response>';
    IF NOT EXISTS(SELECT 1 FROM @InlineSteps WHERE Outcome='context-changed')
        OR EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@InlineCall AND providerCallId IS NOT NULL)
        OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId=@InlineCall)<>1
        THROW 51999,'A stale inline transition must not bind the SID or mutate execution events.',1;
    DELETE @InlineSteps;
    INSERT @InlineSteps EXEC dbo.automatedcalls_Next @InlineCall,@InlineExecution,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567',N'{}',
        'redirect',@InlineBundle,@InlineGraph,N'hello',N'end',@InlineNext,N'<Response><Hangup /></Response>';
    IF NOT EXISTS(SELECT 1 FROM @InlineSteps WHERE Outcome='advanced' AND ExecutionId=@InlineNext)
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@InlineCall AND providerCallId='CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' AND CallStatus='in-progress')
        THROW 51999,'The pre-issued inline execution must support next before progress callbacks or response SID save.',1;
    EXEC dbo.automatedcalls_SaveSubmission @InlineQueue,@InlineLease,@InlineCall,@InlineToken,'ACCEPTED','CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@InlineCall AND CallStatus='in-progress' AND SubmissionState='ACCEPTED')
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@InlineQueue AND Status='PROCESSING')
        THROW 51999,'Late acceptance must preserve the early inline callback and not complete the queue.',1;
    SET @InlineLease=NEWID(); SET @InlineToken=NEWID(); SET @InlineExecution=NEWID();
    INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil)
    VALUES(42,'inline-test',@InlineFlow,@InlineFlowVersion,N'{}',NEWID(),'PROCESSING',1,@InlineLease,'inline-worker',DATEADD(minute,5,SYSUTCDATETIME()));
    SET @InlineQueue=SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,TwiML,Parameters)
    VALUES(@InlineQueue,@InlineLease,'Twilio','inline-test','+15551234567',@InlineFlow,@InlineFlowVersion,'queued',@InlineBundle,N'{}');
    SET @InlineCall=SCOPE_IDENTITY();
    SET @InlineResponse=N'<Response><Say>Alex &amp; Co</Say><Redirect>https://feedback.example/next?executionId='+LOWER(CONVERT(nvarchar(36),@InlineExecution))+N'</Redirect></Response>';
    SET @InlineOpening=(SELECT N'hello' AS StepId,LOWER(CONVERT(nvarchar(36),@InlineExecution)) AS ExecutionId,@InlineResponse AS ResponseTwiML FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    SET @InlineRequest=(SELECT @InlineCall AS CallId,'+15551234567' AS [To],JSON_QUERY(@InlineOpening) AS OpeningStep FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    EXEC dbo.automatedcalls_BeginSubmission @InlineQueue,@InlineLease,'inline-worker',@InlineCall,@InlineToken,@InlineRequest;
    EXEC dbo.automatedcalls_SaveSubmission @InlineQueue,@InlineLease,@InlineCall,@InlineToken,'ACCEPTED','CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@InlineCall AND CallStatus='queued' AND StartedDateTime IS NULL)
        THROW 51999,'Inline acceptance must not pretend the call is answered.',1;
    DELETE @InlineSteps;
    INSERT @InlineSteps EXEC dbo.automatedcalls_Next @InlineCall,@InlineExecution,'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','+15551234567',N'{}',
        'redirect',@InlineBundle,@InlineGraph,N'hello',N'end',@InlineNext,N'<Response><Hangup /></Response>';
    IF NOT EXISTS(SELECT 1 FROM @InlineSteps WHERE Outcome='advanced')
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@InlineCall AND CallStatus='in-progress' AND StartedDateTime IS NOT NULL AND SubmissionState='ACCEPTED')
        THROW 51999,'The first inline next callback must mark the actual answer even when acceptance was saved first.',1;
    ROLLBACK;
    PRINT 'Inline opening SQL checks passed: durable opening execution, no lifecycle mutation before POST, start replay, guarded early next/SID binding and late acceptance; fixtures rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;