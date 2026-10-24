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
    INSERT dbo.Client(OID) VALUES('prep-test');
    INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('prep-test',N'test');
    DECLARE @Flow int=SCOPE_IDENTITY(),@Key uniqueidentifier=NEWID();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@Flow,1,N'{"schemaVersion":1}',N'<CallFlow/>',N'test');
    DECLARE @FlowVersion int = SCOPE_IDENTITY();
    DECLARE @Jobs TABLE(SystemID int);
    INSERT @Jobs EXEC dbo.activealarms_call_InsertJob 42,'prep-test',@Flow,@VariablesJSON=N'{"OffenderName":"Alex"}',@EnqueueIdempotencyKey=@Key;
    DECLARE @Queue int=(SELECT SystemID FROM @Jobs),@Token uniqueidentifier=NEWID();
    UPDATE dbo.AutomatedCallQueue SET Status='PROCESSING',LockedBy='prep-worker',LeaseToken=@Token,LockedUntil=DATEADD(minute,5,SYSUTCDATETIME()) WHERE SystemID=@Queue;
    DECLARE @Attempts TABLE(CallId int,QueueId int,QueueLeaseToken uniqueidentifier,OID varchar(20),PhoneE164 varchar(16),FlowId int,TwiML nvarchar(max),Parameters nvarchar(max),TemplateJson nvarchar(max),TemplateTwiML nvarchar(max),VariablesJson nvarchar(max));
    INSERT @Attempts EXEC dbo.automatedcalls_BeginAttempt @Queue,@Token,'prep-worker','+15551234567';
    DECLARE @Call int=(SELECT CallId FROM @Attempts);
    DELETE @Attempts;
    INSERT @Attempts EXEC dbo.automatedcalls_BeginAttempt @Queue,@Token,'prep-worker','+15557654321';
    IF NOT EXISTS(SELECT 1 FROM @Attempts WHERE CallId=@Call AND QueueId=@Queue AND QueueLeaseToken=@Token AND OID='prep-test' AND PhoneE164='+15551234567' AND FlowId=@Flow AND VariablesJson=N'{"OffenderName":"Alex"}')
        OR (SELECT COUNT(*) FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@Queue AND QueueLeaseToken=@Token)<>1
        THROW 51000,'Replay must preserve one attempt and its original recipient.',1;
    EXEC dbo.automatedcalls_SavePreparation @Queue,@Token,'prep-worker',@Call,N'<CallFlow><Step><Response><Say>Alex</Say></Response></Step></CallFlow>',N'{"callId":42}';
    DECLARE @Before nvarchar(max)=(SELECT * FROM dbo.AutomatedCalls WHERE SystemID=@Call FOR JSON PATH);
    EXEC dbo.automatedcalls_SavePreparation @Queue,@Token,'prep-worker',@Call,N'<CallFlow><Step><Response><Say>Alex</Say></Response></Step></CallFlow>',N'{"callId":42}';
    EXEC dbo.automatedcalls_FailPreparation @Queue,@Token,'prep-worker';
    IF @Before<>(SELECT * FROM dbo.AutomatedCalls WHERE SystemID=@Call FOR JSON PATH)
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Queue AND Status='PROCESSING')
        THROW 51000,'Prepared replay/failure recovery must not mutate the prepared attempt or queue.',1;
    UPDATE dbo.AutomatedCallQueue SET LockedUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@Queue;
    BEGIN TRY
        EXEC dbo.automatedcalls_SavePreparation @Queue,@Token,'prep-worker',@Call,N'<CallFlow/>',N'{}';
        THROW 51000,'Expected expired lease rejection.',1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER()<>51002 THROW;
    END CATCH;
    IF @@TRANCOUNT<>0 OR EXISTS(SELECT 1 FROM dbo.CallFlowTemplates WHERE SystemID=@Flow)
        THROW 51000,'Expired lease rejection must roll back owned fixtures.',1;
    PRINT 'Preparation SQL checks passed: one attempt per claim, immutable recipient/snapshot, ambiguous save protection, expired lease fencing; fixtures rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;

DECLARE @Case int=0;
WHILE @Case<5
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;
        INSERT dbo.Client(OID) VALUES('prep-fence');
        INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('prep-fence',N'test');
        DECLARE @CaseFlow int=SCOPE_IDENTITY(),@CaseToken uniqueidentifier=NEWID();
        INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@CaseFlow,1,N'{"schemaVersion":1}',N'<CallFlow/>',N'test');
        DECLARE @CaseFlowVersion int = SCOPE_IDENTITY();
        INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil)
        VALUES(42,'prep-fence',@CaseFlow,@CaseFlowVersion,N'{}',NEWID(),'PROCESSING',1,@CaseToken,'prep-worker',DATEADD(minute,5,SYSUTCDATETIME()));
        DECLARE @CaseQueue int=SCOPE_IDENTITY();
        EXEC dbo.automatedcalls_BeginAttempt @CaseQueue,@CaseToken,'prep-worker','+15551234567';
        DECLARE @CaseCall int=(SELECT SystemID FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@CaseQueue);
        IF @Case=0
            EXEC dbo.automatedcalls_BeginAttempt @CaseQueue,@CaseToken,'other-worker','+15551234567';
        IF @Case=1
        BEGIN
            UPDATE dbo.AutomatedCallQueue SET LeaseToken=NEWID() WHERE SystemID=@CaseQueue;
            EXEC dbo.automatedcalls_SavePreparation @CaseQueue,@CaseToken,'prep-worker',@CaseCall,N'<CallFlow/>',N'{}';
        END;
        IF @Case=2
            EXEC dbo.automatedcalls_SavePreparation @CaseQueue,@CaseToken,'prep-worker',2147483647,N'<CallFlow/>',N'{}';
        IF @Case=3
        BEGIN
            EXEC dbo.automatedcalls_SavePreparation @CaseQueue,@CaseToken,'prep-worker',@CaseCall,N'<CallFlow/>',N'{}';
            EXEC dbo.automatedcalls_SavePreparation @CaseQueue,@CaseToken,'prep-worker',@CaseCall,N'<CallFlow changed="true"/>',N'{}';
        END;
        IF @Case=4
            INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId)
            VALUES(@CaseQueue,@CaseToken,'Twilio','prep-fence','+15551234567',@CaseFlow,@CaseFlowVersion);
        THROW 51000,'Expected preparation ownership/immutability/uniqueness rejection.',1;
    END TRY
    BEGIN CATCH
        IF (@Case<3 AND ERROR_NUMBER()<>51002) OR (@Case=3 AND ERROR_NUMBER()<>51001) OR (@Case=4 AND ERROR_NUMBER() NOT IN(2601,2627)) THROW;
        IF XACT_STATE()<>0 ROLLBACK;
        IF EXISTS(SELECT 1 FROM dbo.CallFlowTemplates WHERE SystemID=@CaseFlow) THROW 51000,'Fence fixtures survived rollback.',1;
    END CATCH;
    SET @Case+=1;
END;
BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.Client(OID) VALUES('prep-fail');
    INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('prep-fail',N'test');
    DECLARE @FailFlow int=SCOPE_IDENTITY(),@FailToken uniqueidentifier=NEWID();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@FailFlow,1,N'{"schemaVersion":1}',N'<CallFlow/>',N'test');
    DECLARE @FailFlowVersion int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil)
    VALUES(42,'prep-fail',@FailFlow,@FailFlowVersion,N'{}',NEWID(),'PROCESSING',1,@FailToken,'prep-worker',DATEADD(minute,5,SYSUTCDATETIME()));
    DECLARE @FailQueue int=SCOPE_IDENTITY();
    EXEC dbo.automatedcalls_BeginAttempt @FailQueue,@FailToken,'prep-worker','+15551234567';
    EXEC dbo.automatedcalls_FailPreparation @FailQueue,@FailToken,'PREP-WORKER';
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@FailQueue AND Status='PROCESSING')
        THROW 51000,'Case-different worker must not dispose another owner claim.',1;
    EXEC dbo.automatedcalls_FailPreparation @FailQueue,@FailToken,'prep-worker';
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@FailQueue AND Status='FAILED' AND LeaseToken IS NULL AND LastErrorCode='PREPARATION_FAILED')
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@FailQueue AND CallStatus='failed' AND TwiML IS NULL AND Parameters IS NULL)
        THROW 51000,'Unprepared failure disposition must be atomic and fenced.',1;
    ROLLBACK;
    PRINT 'Wrong owner/replaced lease/call identity/immutable snapshot/unique index and unprepared failure disposition checks passed; fixtures rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;