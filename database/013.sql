SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
IF COL_LENGTH('dbo.AutomatedCallQueue','VariablesJSON') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD VariablesJSON nvarchar(max) NULL;
IF COL_LENGTH('dbo.AutomatedCallQueue','EnqueueIdempotencyKey') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD EnqueueIdempotencyKey uniqueidentifier NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.AutomatedCallQueue') AND name='CK_AutomatedCallQueue_RequestSnapshot')
    ALTER TABLE dbo.AutomatedCallQueue ADD CONSTRAINT CK_AutomatedCallQueue_RequestSnapshot CHECK (
        EnqueueIdempotencyKey IS NULL OR
        (VariablesJSON IS NOT NULL AND ISJSON(VariablesJSON)=1 AND LEFT(LTRIM(VariablesJSON),1)='{'
         AND OID IS NOT NULL AND LEN(LTRIM(RTRIM(OID)))>0 AND FlowId IS NOT NULL AND FlowId>0));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.AutomatedCallQueue') AND name='UX_AutomatedCallQueue_EnqueueIdempotencyKey')
    CREATE UNIQUE INDEX UX_AutomatedCallQueue_EnqueueIdempotencyKey ON dbo.AutomatedCallQueue(EnqueueIdempotencyKey)
    WHERE EnqueueIdempotencyKey IS NOT NULL;
GO
CREATE OR ALTER PROCEDURE dbo.activealarms_call_InsertJob
    @ActiveAlarmID int,
    @OID varchar(20) = NULL,
    @FlowId int = NULL,
    @CallStatus int = NULL,
    @Priority int = 0,
    @AvailableAt datetime2(3) = NULL,
    @MaxAttempts int = 10,
    @VariablesJSON nvarchar(max) = NULL,
    @EnqueueIdempotencyKey uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET ANSI_WARNINGS ON;
    SET ARITHABORT ON;
    SET CONCAT_NULL_YIELDS_NULL ON;
    SET NUMERIC_ROUNDABORT OFF;
    IF @ActiveAlarmID IS NULL OR @ActiveAlarmID<=0 OR @OID IS NULL OR LEN(LTRIM(RTRIM(@OID)))=0
        OR @FlowId IS NULL OR @FlowId<=0 OR @MaxAttempts IS NULL OR @MaxAttempts<=0
        OR @Priority IS NULL OR @EnqueueIdempotencyKey IS NULL
        OR @EnqueueIdempotencyKey='00000000-0000-0000-0000-000000000000'
        OR @VariablesJSON IS NULL OR ISJSON(@VariablesJSON)<>1 OR LEFT(LTRIM(@VariablesJSON),1)<>'{'
        THROW 51000,'A complete auto-call request snapshot is required.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @JobId int, @ExistingAlarm int, @ExistingOid varchar(20), @ExistingFlow int, @ExistingVariables nvarchar(max);
        SELECT @JobId=SystemID,@ExistingAlarm=ActiveAlarmID,@ExistingOid=OID,@ExistingFlow=FlowId,@ExistingVariables=VariablesJSON
        FROM dbo.AutomatedCallQueue WITH (UPDLOCK,HOLDLOCK) WHERE EnqueueIdempotencyKey=@EnqueueIdempotencyKey;
        IF @JobId IS NOT NULL
        BEGIN
            IF @ExistingAlarm<>@ActiveAlarmID OR @ExistingFlow<>@FlowId
                OR @ExistingOid COLLATE Latin1_General_100_BIN2<>@OID COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@ExistingOid)<>DATALENGTH(@OID)
                OR @ExistingVariables COLLATE Latin1_General_100_BIN2<>@VariablesJSON COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@ExistingVariables)<>DATALENGTH(@VariablesJSON)
                THROW 51001,'Enqueue key conflicts with the original request snapshot.',1;
        END
        ELSE
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.CallFlowTemplates WHERE SystemID=@FlowId AND DeletedDateTime IS NULL)
                THROW 51000,'An existing published call flow is required.',1;
            INSERT dbo.AutomatedCallQueue
                (ActiveAlarmID,OID,FlowId,CallStatus,Priority,AvailableAt,MaxAttempts,VariablesJSON,EnqueueIdempotencyKey)
            VALUES (@ActiveAlarmID,@OID,@FlowId,@CallStatus,@Priority,COALESCE(@AvailableAt,SYSUTCDATETIME()),@MaxAttempts,@VariablesJSON,@EnqueueIdempotencyKey);
            SET @JobId=SCOPE_IDENTITY();
        END;
        COMMIT;
        SELECT @JobId AS SystemID;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.activealarms_call_GetNextSetOfJobs
    @BatchSize int,
    @WorkerId varchar(100),
    @LeaseMinutes int
AS
BEGIN
    SET NOCOUNT ON;
    IF @BatchSize IS NULL OR @BatchSize<=0 OR @WorkerId IS NULL OR LEN(LTRIM(RTRIM(@WorkerId)))=0
        OR @LeaseMinutes IS NULL OR @LeaseMinutes<=0
        THROW 51000,'Valid batch size, worker identity and lease duration are required.',1;
    DECLARE @Now datetime2(3)=SYSUTCDATETIME();
    ;WITH JobsToClaim AS
    (
        SELECT TOP (@BatchSize) * FROM dbo.AutomatedCallQueue WITH (UPDLOCK,READPAST,ROWLOCK)
        WHERE Status='READY' AND AvailableAt<=@Now AND AttemptCount<MaxAttempts
            AND EnqueueIdempotencyKey IS NOT NULL AND VariablesJSON IS NOT NULL
            AND OID IS NOT NULL AND FlowId IS NOT NULL
        ORDER BY Priority DESC,AvailableAt,SystemID
    )
    UPDATE JobsToClaim SET Status='PROCESSING',LockedBy=@WorkerId,LockedAt=@Now,
        LockedUntil=DATEADD(MINUTE,@LeaseMinutes,@Now),LeaseToken=NEWID(),AttemptCount=AttemptCount+1,
        LastAttemptAt=@Now,StartedAt=COALESCE(StartedAt,@Now),UpdatedAt=@Now
    OUTPUT inserted.SystemID,inserted.ActiveAlarmID,inserted.OID,inserted.FlowId,inserted.VariablesJSON,
        inserted.EnqueueIdempotencyKey,inserted.AttemptCount,inserted.MaxAttempts,inserted.LeaseToken,inserted.LockedUntil;
END;
GO