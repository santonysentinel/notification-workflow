SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
IF DB_NAME() NOT IN(N'NotificationWorkflowVoiceTests',N'NotificationWorkflowTranscriptionSchemaTests') THROW 51999,'Owned scratch database required.',1;
BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.Client(OID) VALUES('recovery-test');
    INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('recovery-test',N'test');
    DECLARE @Flow int=SCOPE_IDENTITY(),@Token uniqueidentifier=NEWID(),@OtherToken uniqueidentifier=NEWID();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@Flow,1,N'{}',N'<CallFlow/>',N'test');
    DECLARE @FlowVersion int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil)
    VALUES(42,'recovery-test',@Flow,@FlowVersion,N'{}',NEWID(),'PROCESSING',1,@Token,'recovery-worker',DATEADD(second,10,SYSUTCDATETIME()));
    DECLARE @Queue int=SCOPE_IDENTITY();
    DECLARE @Renewed TABLE(Renewed bit);
    INSERT @Renewed EXEC dbo.automatedcalls_RenewLease @Queue,@Token,'recovery-worker',5;
    IF NOT EXISTS(SELECT 1 FROM @Renewed WHERE Renewed=1) OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Queue AND LockedUntil>DATEADD(minute,4,SYSUTCDATETIME()))
        THROW 51999,'Current owner must renew using SQL UTC.',1;
    DELETE @Renewed;
    INSERT @Renewed EXEC dbo.automatedcalls_RenewLease @Queue,@Token,'RECOVERY-WORKER',5;
    IF EXISTS(SELECT 1 FROM @Renewed WHERE Renewed=1) THROW 51999,'Wrong worker must not renew.',1;
    UPDATE dbo.AutomatedCallQueue SET LeaseToken=@OtherToken WHERE SystemID=@Queue;
    DELETE @Renewed;
    INSERT @Renewed EXEC dbo.automatedcalls_RenewLease @Queue,@Token,'recovery-worker',5;
    IF EXISTS(SELECT 1 FROM @Renewed WHERE Renewed=1) THROW 51999,'Replaced owner must not renew.',1;
    UPDATE dbo.AutomatedCallQueue SET LockedUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@Queue;
    DELETE @Renewed;
    INSERT @Renewed EXEC dbo.automatedcalls_RenewLease @Queue,@OtherToken,'recovery-worker',5;
    IF EXISTS(SELECT 1 FROM @Renewed WHERE Renewed=1) THROW 51999,'Expired ownership cannot be revived.',1;
    UPDATE dbo.AutomatedCallQueue SET Status='READY',LeaseToken=NULL,LockedBy=NULL,LockedUntil=NULL,MaxAttempts=1,RecoveryCount=1,
        AvailableAt=SYSUTCDATETIME(),Priority=1000000000 WHERE SystemID=@Queue;
    DECLARE @NoAttemptClaims TABLE(SystemID int,ActiveAlarmID int,OID varchar(20),FlowId int,VariablesJSON nvarchar(max),EnqueueIdempotencyKey uniqueidentifier,AttemptCount int,MaxAttempts int,LeaseToken uniqueidentifier,LockedUntil datetime2(3),CallDeviceIndex int,OptionsJSON nvarchar(max));
    INSERT @NoAttemptClaims EXEC dbo.activealarms_call_GetNextSetOfJobs 1,'preparation-retry',5;
    IF NOT EXISTS(SELECT 1 FROM @NoAttemptClaims WHERE SystemID=@Queue AND AttemptCount=1 AND MaxAttempts=1)
        THROW 51999,'A bounded pre-submission retry without an attempt must not be stranded by claim history.',1;
    ROLLBACK;
    PRINT 'Recovery lease renewal checks passed; fixtures rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.Client(OID) VALUES('recovery-resume');
    INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('recovery-resume',N'test');
    DECLARE @ResumeFlow int=SCOPE_IDENTITY(),@Original uniqueidentifier=NEWID(),@Submission uniqueidentifier=NEWID();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@ResumeFlow,1,N'{}',N'<CallFlow/>',N'test');
    DECLARE @ResumeFlowVersion int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil,Priority)
    VALUES(42,'recovery-resume',@ResumeFlow,@ResumeFlowVersion,N'{}',NEWID(),'PROCESSING',1,@Original,'original-worker',DATEADD(second,-1,SYSUTCDATETIME()),1000000000);
    DECLARE @ResumeQueue int=SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,TwiML,Parameters)
    VALUES(@ResumeQueue,@Original,'Twilio','recovery-resume','+15551234567',@ResumeFlow,@ResumeFlowVersion,'queued',N'<CallFlow/>',N'{}');
    DECLARE @ResumeCall int=SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId=@ResumeCall WHERE SystemID=@ResumeQueue;
    DECLARE @Claims TABLE(SystemID int,ActiveAlarmID int,OID varchar(20),FlowId int,VariablesJSON nvarchar(max),EnqueueIdempotencyKey uniqueidentifier,AttemptCount int,MaxAttempts int,LeaseToken uniqueidentifier,LockedUntil datetime2(3),CallDeviceIndex int,OptionsJSON nvarchar(max));
    INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1,'replacement-worker',5;
    DECLARE @Replacement uniqueidentifier=(SELECT LeaseToken FROM @Claims WHERE SystemID=@ResumeQueue);
    IF @Replacement IS NULL OR @Replacement=@Original OR NOT EXISTS(SELECT 1 FROM @Claims WHERE AttemptCount=1)
        THROW 51999,'Expired unfinished job must be reclaimed without consuming an attempt.',1;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1,'second-worker',5;
    IF EXISTS(SELECT 1 FROM @Claims WHERE SystemID=@ResumeQueue) THROW 51999,'A second claimer must not receive the same live lease.',1;
    DECLARE @Attempts TABLE(CallId int,QueueId int,QueueLeaseToken uniqueidentifier,OID varchar(20),PhoneE164 varchar(16),FlowId int,TwiML nvarchar(max),Parameters nvarchar(max),TemplateJson nvarchar(max),TemplateTwiML nvarchar(max),VariablesJson nvarchar(max));
    INSERT @Attempts EXEC dbo.automatedcalls_BeginAttempt @ResumeQueue,@Replacement,'replacement-worker','+15557654321';
    IF NOT EXISTS(SELECT 1 FROM @Attempts WHERE CallId=@ResumeCall AND QueueLeaseToken=@Original AND PhoneE164='+15551234567' AND TwiML=N'<CallFlow/>')
        OR (SELECT COUNT(*) FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@ResumeQueue)<>1 THROW 51999,'Reclaim must reuse the original prepared attempt and recipient.',1;
    EXEC dbo.automatedcalls_SavePreparation @ResumeQueue,@Replacement,'replacement-worker',@ResumeCall,N'<CallFlow/>',N'{}';
    DECLARE @Request nvarchar(max)=(SELECT @ResumeCall AS CallId,'+15551234567' AS [To] FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    DECLARE @Markers TABLE(ShouldSubmit bit,SubmissionState varchar(20),SubmissionToken uniqueidentifier,ProviderCallId varchar(200),OpeningExecutionId uniqueidentifier);
    INSERT @Markers EXEC dbo.automatedcalls_BeginSubmission @ResumeQueue,@Original,'replacement-worker',@ResumeCall,@Submission,@Request,@Replacement;
    IF NOT EXISTS(SELECT 1 FROM @Markers WHERE ShouldSubmit=1 AND SubmissionState='SUBMITTING')
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@ResumeQueue AND SubmissionCount=1) THROW 51999,'Submission must use new ownership and retain original attempt identity.',1;
    UPDATE dbo.AutomatedCallQueue SET LockedUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@ResumeQueue;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1,'third-worker',5;
    DECLARE @Third uniqueidentifier=(SELECT LeaseToken FROM @Claims WHERE SystemID=@ResumeQueue),@Replay uniqueidentifier=NEWID();
    DELETE @Markers;
    INSERT @Markers EXEC dbo.automatedcalls_BeginSubmission @ResumeQueue,@Original,'third-worker',@ResumeCall,@Replay,@Request,@Third;
    IF EXISTS(SELECT 1 FROM @Markers WHERE ShouldSubmit=1) OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@ResumeCall AND SubmissionToken=@Submission)
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@ResumeQueue AND SubmissionCount=1) THROW 51999,'A durable ambiguous marker must never authorize a second POST.',1;
    EXEC dbo.automatedcalls_SaveSubmission @ResumeQueue,@Original,@ResumeCall,@Submission,'ACCEPTED','CA11111111111111111111111111111111',NULL;
    DECLARE @Now datetime2(3)=SYSUTCDATETIME();
    EXEC dbo.automatedcalls_Status @ResumeCall,'CA11111111111111111111111111111111','+15551234567','completed',3,@Now,12;
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@ResumeQueue AND Status='COMPLETED' AND LeaseToken IS NULL)
        THROW 51999,'Late acceptance and terminal callback must survive changed lease ownership.',1;
    ROLLBACK;
    PRINT 'Recovery reclaim, immutable prepared identity, no ambiguous redial, late SID save and reclaimed callback checks passed.';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.Client(OID) VALUES('recovery-retry');
    INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('recovery-retry',N'test');
    DECLARE @RetryFlow int=SCOPE_IDENTITY(),@RetryOriginal uniqueidentifier=NEWID(),@RetrySubmission uniqueidentifier=NEWID();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@RetryFlow,1,N'{}',N'<CallFlow/>',N'test');
    DECLARE @RetryFlowVersion int = SCOPE_IDENTITY();
    INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,SubmissionCount,LeaseToken,LockedBy,LockedUntil,Priority)
    VALUES(42,'recovery-retry',@RetryFlow,@RetryFlowVersion,N'{}',NEWID(),'PROCESSING',1,1,@RetryOriginal,'retry-worker',DATEADD(minute,5,SYSUTCDATETIME()),1000000000);
    DECLARE @RetryQueue int=SCOPE_IDENTITY();
    INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,SubmissionState,SubmissionToken,SubmissionStartedAt,SubmissionErrorCode,TwiML,Parameters)
    VALUES(@RetryQueue,@RetryOriginal,'Twilio','recovery-retry','+15551234567',@RetryFlow,@RetryFlowVersion,'queued','REJECTED',@RetrySubmission,SYSUTCDATETIME(),'TWILIO_20429',N'<CallFlow/>',N'{}');
    DECLARE @RejectedCall int=SCOPE_IDENTITY();
    UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId=@RejectedCall WHERE SystemID=@RetryQueue;
    EXEC dbo.automatedcalls_RecoveryDisposition @RetryQueue,@RetryOriginal,'retry-worker','RETRY','TWILIO_20429',30,3,3,24;
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@RetryQueue AND Status='READY' AND RecoveryCount=1 AND PreparationFailureCount=0 AND AvailableAt>DATEADD(second,20,SYSUTCDATETIME()))
        THROW 51999,'Proven rejection must schedule durable bounded backoff with a separate retry counter.',1;
    UPDATE dbo.AutomatedCallQueue SET AvailableAt=SYSUTCDATETIME() WHERE SystemID=@RetryQueue;
    DECLARE @RetryClaims TABLE(SystemID int,ActiveAlarmID int,OID varchar(20),FlowId int,VariablesJSON nvarchar(max),EnqueueIdempotencyKey uniqueidentifier,AttemptCount int,MaxAttempts int,LeaseToken uniqueidentifier,LockedUntil datetime2(3),CallDeviceIndex int,OptionsJSON nvarchar(max));
    INSERT @RetryClaims EXEC dbo.activealarms_call_GetNextSetOfJobs 1,'next-worker',5;
    DECLARE @RetryLease uniqueidentifier=(SELECT LeaseToken FROM @RetryClaims WHERE SystemID=@RetryQueue);
    EXEC dbo.automatedcalls_BeginAttempt @RetryQueue,@RetryLease,'next-worker','+15551234567';
    DECLARE @NewCall int=(SELECT CurrentAttemptId FROM dbo.AutomatedCallQueue WHERE SystemID=@RetryQueue);
    IF @NewCall=@RejectedCall OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@RejectedCall AND QueueLeaseToken=@RetryOriginal AND SubmissionToken=@RetrySubmission AND SubmissionState='REJECTED')
        THROW 51999,'A permitted retry needs a fresh attempt, preserving the rejected submission.',1;
    DECLARE @RetryNow datetime2(3)=SYSUTCDATETIME();
    EXEC dbo.automatedcalls_Status @RejectedCall,'CA22222222222222222222222222222222','+15551234567','completed',1,@RetryNow;
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@RetryQueue AND Status='PROCESSING' AND CurrentAttemptId=@NewCall)
        THROW 51999,'A superseded attempt cannot finalize the current job.',1;
    UPDATE dbo.AutomatedCalls SET providerCallId='CA33333333333333333333333333333333',SubmissionState='ACCEPTED',SubmissionStartedAt=SYSUTCDATETIME() WHERE SystemID=@NewCall;
    EXEC dbo.automatedcalls_Observe @RetryQueue,@RetryLease,'next-worker',@NewCall,'CA33333333333333333333333333333333','completed',17,120,24;
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@RetryQueue AND Status='COMPLETED' AND LeaseToken IS NULL)
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@NewCall AND CallStatus='completed' AND EndedDateTime IS NOT NULL AND CallDurationSeconds=17 AND LastStatusSequenceNumber IS NULL)
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId=@NewCall AND EventType='CallReconciled') THROW 51999,'Known-SID observation must recover missing terminal callbacks without fabricating sequence metadata.',1;
    EXEC dbo.automatedcalls_Status @NewCall,'CA33333333333333333333333333333333','+15551234567','ringing',99,@RetryNow;
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@NewCall AND CallStatus='completed' AND LastStatusSequenceNumber IS NULL)
        THROW 51999,'Callbacks must not regress a reconciled terminal result.',1;
    ROLLBACK;
    PRINT 'Recovery safe rejection/backoff, fresh retry identity, superseded callback and missing-terminal reconciliation checks passed.';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;

DECLARE @RecoveryCase int=0;
WHILE @RecoveryCase<5
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;
        INSERT dbo.Client(OID) VALUES('recovery-bound');
        INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('recovery-bound',N'test');
        DECLARE @BoundFlow int=SCOPE_IDENTITY(),@BoundLease uniqueidentifier=NEWID();
        INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@BoundFlow,1,N'{}',N'<CallFlow/>',N'test');
        DECLARE @BoundFlowVersion int = SCOPE_IDENTITY();
        INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,SubmissionCount,LeaseToken,LockedBy,LockedUntil)
        VALUES(42,'recovery-bound',@BoundFlow,@BoundFlowVersion,N'{}',NEWID(),'PROCESSING',1,CASE WHEN @RecoveryCase=0 THEN 3 ELSE 1 END,@BoundLease,'bound-worker',DATEADD(minute,5,SYSUTCDATETIME()));
        DECLARE @BoundQueue int=SCOPE_IDENTITY();
        INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,SubmissionState,SubmissionStartedAt,SubmissionErrorCode)
        VALUES(@BoundQueue,@BoundLease,'Twilio','recovery-bound','+15551234567',@BoundFlow,@BoundFlowVersion,'queued',CASE WHEN @RecoveryCase=0 THEN 'REJECTED' WHEN @RecoveryCase=4 THEN 'READY' ELSE 'UNKNOWN' END,
            DATEADD(hour,CASE WHEN @RecoveryCase IN(2,3) THEN -25 ELSE 0 END,SYSUTCDATETIME()),CASE WHEN @RecoveryCase=0 THEN 'TWILIO_20429' ELSE 'ACCEPTANCE_UNKNOWN' END);
        DECLARE @BoundCall int=SCOPE_IDENTITY();
        UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId=@BoundCall WHERE SystemID=@BoundQueue;
        IF @RecoveryCase=4 UPDATE dbo.AutomatedCallQueue SET PreparationFailureCount=2 WHERE SystemID=@BoundQueue;
        IF @RecoveryCase=3
        BEGIN
            UPDATE dbo.AutomatedCalls SET SubmissionState='ACCEPTED',providerCallId='CA44444444444444444444444444444444' WHERE SystemID=@BoundCall;
            EXEC dbo.automatedcalls_Observe @BoundQueue,@BoundLease,'bound-worker',@BoundCall,'CA44444444444444444444444444444444','in-progress',NULL,120,24;
        END
        ELSE EXEC dbo.automatedcalls_RecoveryDisposition @BoundQueue,@BoundLease,'bound-worker','RETRY','PREPARATION_UNAVAILABLE',30,3,3,24;
        IF @RecoveryCase=1
        BEGIN
            IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@BoundQueue AND Status='READY' AND RecoveryCount=0 AND SubmissionCount=1)
                THROW 51999,'Unknown acceptance must wait, never schedule a submission retry.',1;
        END
        ELSE IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@BoundQueue AND Status='DEAD_LETTER' AND DeadLetteredAt IS NOT NULL)
            THROW 51999,'Submission budget and maximum recovery age must dead-letter unresolved jobs.',1;
        IF @RecoveryCase=4 AND NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@BoundQueue AND LastErrorCode='RETRY_EXHAUSTED')
            THROW 51999,'Preparation failures have their own finite retry budget.',1;
        IF @RecoveryCase=2
        BEGIN
            DECLARE @BoundNow datetime2(3)=SYSUTCDATETIME();
            EXEC dbo.automatedcalls_Status @BoundCall,'CA55555555555555555555555555555555','+15551234567','completed',2,@BoundNow;
            IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@BoundQueue AND Status='COMPLETED')
                THROW 51999,'An authoritative late callback may resolve a dead-lettered unknown submission.',1;
        END;
        ROLLBACK;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
    SET @RecoveryCase+=1;
END;
PRINT 'Recovery retry budgets, unknown-no-redial, maximum age and late dead-letter resolution checks passed.';

DECLARE @FenceCase int=0;
WHILE @FenceCase<7
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;
        INSERT dbo.Client(OID) VALUES('recovery-fence');
        INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('recovery-fence',N'test');
        DECLARE @FenceFlow int=SCOPE_IDENTITY(),@FenceOld uniqueidentifier=NEWID(),@FenceCurrent uniqueidentifier=NEWID(),@FenceSubmission uniqueidentifier=NEWID();
        INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@FenceFlow,1,N'{}',N'<CallFlow/>',N'test');
        DECLARE @FenceFlowVersion int = SCOPE_IDENTITY();
        INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,FlowVersionId,VariablesJSON,EnqueueIdempotencyKey,Status,AttemptCount,SubmissionCount,LeaseToken,LockedBy,LockedUntil,StartedAt)
        VALUES(42,'recovery-fence',@FenceFlow,@FenceFlowVersion,N'{}',NEWID(),'PROCESSING',1,CASE WHEN @FenceCase=4 THEN 3 ELSE 0 END,
            @FenceCurrent,'current-worker',DATEADD(minute,5,SYSUTCDATETIME()),DATEADD(hour,CASE WHEN @FenceCase=5 THEN -25 ELSE 0 END,SYSUTCDATETIME()));
        DECLARE @FenceQueue int=SCOPE_IDENTITY();
        IF @FenceCase=6
            INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,SubmissionState)
            VALUES(@FenceQueue,NEWID(),'Twilio','recovery-fence','+15551234567',@FenceFlow,@FenceFlowVersion,'queued','UNKNOWN');
        INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,FlowVersionId,CallStatus,SubmissionState,providerCallId,TwiML,Parameters)
        VALUES(@FenceQueue,@FenceOld,'Twilio','recovery-fence','+15551234567',@FenceFlow,@FenceFlowVersion,'queued',CASE WHEN @FenceCase<3 THEN 'ACCEPTED' ELSE 'READY' END,
            CASE WHEN @FenceCase<3 THEN 'CA66666666666666666666666666666666' ELSE NULL END,N'<CallFlow/>',N'{}');
        DECLARE @FenceCall int=SCOPE_IDENTITY();
        UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId=@FenceCall WHERE SystemID=@FenceQueue;
        DECLARE @FenceRequest nvarchar(max)=(SELECT @FenceCall AS CallId,'+15551234567' AS [To] FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
        IF @FenceCase=0 EXEC dbo.automatedcalls_SavePreparation @FenceQueue,@FenceOld,'old-worker',@FenceCall,N'<CallFlow/>',N'{}';
        IF @FenceCase=1 EXEC dbo.automatedcalls_RecoveryDisposition @FenceQueue,@FenceOld,'old-worker','DEAD_LETTER','VALIDATION_FAILED',30,3,3,24;
        IF @FenceCase=2 EXEC dbo.automatedcalls_Observe @FenceQueue,@FenceOld,'old-worker',@FenceCall,'CA66666666666666666666666666666666','completed';
        IF @FenceCase=3 EXEC dbo.automatedcalls_BeginSubmission @FenceQueue,@FenceOld,'old-worker',@FenceCall,@FenceSubmission,@FenceRequest,@FenceOld;
        IF @FenceCase>=4 EXEC dbo.automatedcalls_BeginSubmission @FenceQueue,@FenceOld,'current-worker',@FenceCall,@FenceSubmission,@FenceRequest,@FenceCurrent;
        THROW 51999,'Expected ownership, budget, age or prior-outcome rejection.',1;
    END TRY
    BEGIN CATCH
        IF (@FenceCase<4 AND ERROR_NUMBER()<>51002) OR (@FenceCase IN(4,5) AND ERROR_NUMBER()<>51003) OR (@FenceCase=6 AND ERROR_NUMBER()<>51001) THROW;
        IF XACT_STATE()<>0 ROLLBACK;
        IF EXISTS(SELECT 1 FROM dbo.Client WHERE OID='recovery-fence') THROW 51999,'Fenced mutation must roll back atomically.',1;
    END CATCH;
    SET @FenceCase+=1;
END;
PRINT 'Recovery stale-owner fencing, atomic submission-budget/age guards and earlier-unknown protection checks passed.';