SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
IF DB_NAME() NOT IN (N'NotificationWorkflowVoiceTests',N'NotificationWorkflowTranscriptionSchemaTests')
    THROW 51000,'Run queue tests only in an owned scratch database.',1;
BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.Client(OID) VALUES('oid-42'),('oid-44'),('oid-45');
    INSERT dbo.CallFlowTemplates(Name,CreatedBy) VALUES('queue-test',N'test');
    DECLARE @Flow int=SCOPE_IDENTITY(), @First uniqueidentifier=NEWID(), @Second uniqueidentifier=NEWID(), @Future uniqueidentifier=NEWID();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID,Version,TemplateJSON,TwiML,CreatedBy) VALUES(@Flow,1,N'{"schemaVersion":1}',N'<CallFlow/>',N'test');
    DECLARE @FlowVersion int = SCOPE_IDENTITY();
    DECLARE @Result TABLE(SystemID int);
    INSERT @Result EXEC dbo.activealarms_call_InsertJob 42,'oid-42',@Flow,@VariablesJSON=N'{"name":"Alex"}',@EnqueueIdempotencyKey=@First;
    DECLARE @Job int=(SELECT SystemID FROM @Result),@Before nvarchar(max);
    SET @Before=(SELECT * FROM dbo.AutomatedCallQueue WHERE SystemID=@Job FOR JSON PATH);
    DELETE @Result;
    INSERT @Result EXEC dbo.activealarms_call_InsertJob 42,'oid-42',@Flow,@Priority=99,@VariablesJSON=N'{"name":"Alex"}',@EnqueueIdempotencyKey=@First;
    IF NOT EXISTS(SELECT 1 FROM @Result WHERE SystemID=@Job) OR @Before<>(SELECT * FROM dbo.AutomatedCallQueue WHERE SystemID=@Job FOR JSON PATH)
        THROW 51000,'Duplicate enqueue must return original job without changing it.',1;
    INSERT dbo.AutomatedCallQueue(ActiveAlarmID,Priority) VALUES(43,1000);
    DECLARE @Legacy int=SCOPE_IDENTITY();
    DELETE @Result;
    INSERT @Result EXEC dbo.activealarms_call_InsertJob 44,'oid-44',@Flow,@Priority=10,@VariablesJSON=N'{}',@EnqueueIdempotencyKey=@Second;
    DECLARE @PriorityJob int=(SELECT SystemID FROM @Result),@Later datetime2(3)=DATEADD(day,1,SYSUTCDATETIME());
    DELETE @Result;
    INSERT @Result EXEC dbo.activealarms_call_InsertJob 45,'oid-45',@Flow,@AvailableAt=@Later,@VariablesJSON=N'{}',@EnqueueIdempotencyKey=@Future;
    DECLARE @Claims TABLE(SystemID int,ActiveAlarmID int,OID varchar(20),FlowId int,VariablesJSON nvarchar(max),EnqueueIdempotencyKey uniqueidentifier,AttemptCount int,MaxAttempts int,LeaseToken uniqueidentifier,LockedUntil datetime2(3),CallDeviceIndex int,OptionsJSON nvarchar(max));
    INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1,'queue-test',5;
    IF NOT EXISTS(SELECT 1 FROM @Claims WHERE SystemID=@PriorityJob AND ActiveAlarmID=44 AND FlowId=@Flow AND VariablesJSON=N'{}' AND EnqueueIdempotencyKey=@Second AND AttemptCount=1 AND LeaseToken IS NOT NULL)
        THROW 51000,'Claim must return priority job and full request/lease snapshot.',1;
    DELETE @Claims;
    INSERT @Claims EXEC dbo.activealarms_call_GetNextSetOfJobs 1000,'queue-test',5;
    IF NOT EXISTS(SELECT 1 FROM @Claims WHERE SystemID=@Job AND VariablesJSON=N'{"name":"Alex"}')
        OR EXISTS(SELECT 1 FROM @Claims WHERE SystemID IN (@Legacy) OR EnqueueIdempotencyKey=@Future)
        OR NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Legacy AND Status='READY' AND AttemptCount=0 AND LeaseToken IS NULL)
        THROW 51000,'Legacy/future rows must not be claimed; valid snapshots must survive claim.',1;
    DELETE @Result;
    INSERT @Result EXEC dbo.activealarms_call_InsertJob 42,'oid-42',@Flow,@VariablesJSON=N'{"name":"Alex"}',@EnqueueIdempotencyKey=@First;
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@Job AND Status='PROCESSING' AND AttemptCount=1)
        THROW 51000,'Enqueue replay must not reset processing jobs.',1;
    BEGIN TRY
        EXEC dbo.activealarms_call_InsertJob 42,'oid-42',@Flow,@VariablesJSON=N'{"name":"changed"}',@EnqueueIdempotencyKey=@First;
        THROW 51000,'Expected conflicting enqueue rejection.',1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER()<>51001 THROW;
    END CATCH;
    IF @@TRANCOUNT<>0 OR EXISTS(SELECT 1 FROM dbo.CallFlowTemplates WHERE SystemID=@Flow)
        THROW 51000,'Conflict must roll back the transaction; fixtures must be removed.',1;
    PRINT 'Queue contract checks passed: snapshot, idempotency, claims, legacy exclusion, future scheduling, conflict; fixtures rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;

DECLARE @Invalid TABLE(VariablesJSON nvarchar(max),EnqueueKey uniqueidentifier);
INSERT @Invalid VALUES(NULL,NEWID()),(N'[]',NEWID()),(N'not-json',NEWID()),(N'{}',NULL),(N'{}','00000000-0000-0000-0000-000000000000');
DECLARE @InvalidVariables nvarchar(max),@InvalidKey uniqueidentifier;
DECLARE InvalidSnapshots CURSOR LOCAL FAST_FORWARD FOR SELECT VariablesJSON,EnqueueKey FROM @Invalid;
OPEN InvalidSnapshots;
FETCH NEXT FROM InvalidSnapshots INTO @InvalidVariables,@InvalidKey;
WHILE @@FETCH_STATUS=0
BEGIN
    BEGIN TRY
        EXEC dbo.activealarms_call_InsertJob 42,'oid-42',1,@VariablesJSON=@InvalidVariables,@EnqueueIdempotencyKey=@InvalidKey;
        THROW 51000,'Expected incomplete snapshot rejection.',1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER()<>51000 OR ERROR_MESSAGE()<>'A complete auto-call request snapshot is required.' THROW;
    END CATCH;
    FETCH NEXT FROM InvalidSnapshots INTO @InvalidVariables,@InvalidKey;
END;
CLOSE InvalidSnapshots;
DEALLOCATE InvalidSnapshots;
BEGIN TRY
    EXEC dbo.activealarms_call_GetNextSetOfJobs 0,'queue-test',5;
    THROW 51000,'Expected invalid claim rejection.',1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER()<>51000 OR ERROR_MESSAGE()<>'Valid batch size, worker identity and lease duration are required.' THROW;
END CATCH;
BEGIN TRY
    EXEC dbo.activealarms_call_GetNextSetOfJobs 1,' ',5;
    THROW 51000,'Expected missing worker rejection.',1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER()<>51000 OR ERROR_MESSAGE()<>'Valid batch size, worker identity and lease duration are required.' THROW;
END CATCH;
BEGIN TRY
    EXEC dbo.activealarms_call_GetNextSetOfJobs 1,'queue-test',0;
    THROW 51000,'Expected invalid lease rejection.',1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER()<>51000 OR ERROR_MESSAGE()<>'Valid batch size, worker identity and lease duration are required.' THROW;
END CATCH;
PRINT 'Invalid request snapshots and claim arguments rejected.';