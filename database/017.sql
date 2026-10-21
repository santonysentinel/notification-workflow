SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
IF COL_LENGTH('dbo.AutomatedCallQueue','CurrentAttemptId') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD CurrentAttemptId int NULL;
IF COL_LENGTH('dbo.AutomatedCallQueue','SubmissionCount') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD SubmissionCount int NOT NULL CONSTRAINT DF_AutoCallQueue_SubmissionCount DEFAULT(0);
IF COL_LENGTH('dbo.AutomatedCallQueue','RecoveryCount') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD RecoveryCount int NOT NULL CONSTRAINT DF_AutoCallQueue_RecoveryCount DEFAULT(0);
IF COL_LENGTH('dbo.AutomatedCallQueue','PreparationFailureCount') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD PreparationFailureCount int NOT NULL CONSTRAINT DF_AutoCallQueue_PreparationFailureCount DEFAULT(0);
GO
UPDATE q SET CurrentAttemptId=a.SystemID
FROM dbo.AutomatedCallQueue q
CROSS APPLY(SELECT TOP(1) SystemID FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=q.SystemID AND QueueLeaseToken IS NOT NULL ORDER BY SystemID DESC) a
WHERE q.CurrentAttemptId IS NULL AND q.EnqueueIdempotencyKey IS NOT NULL
    AND NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls older WHERE older.AutomatedCallQueueId=q.SystemID AND older.SystemID<a.SystemID
        AND (older.providerCallId IS NOT NULL OR older.SubmissionState IS NULL OR older.SubmissionState IN('SUBMITTING','UNKNOWN','ACCEPTED')));
UPDATE q SET SubmissionCount=a.Total
FROM dbo.AutomatedCallQueue q
CROSS APPLY(SELECT COUNT(*) AS Total FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=q.SystemID AND SubmissionToken IS NOT NULL) a
WHERE q.SubmissionCount<a.Total;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_RenewLease
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100),@LeaseMinutes int
AS
BEGIN
    SET NOCOUNT ON;
    IF @LeaseMinutes IS NULL OR @LeaseMinutes NOT BETWEEN 1 AND 60 THROW 51000,'Invalid lease duration.',1;
    DECLARE @Now datetime2(3)=SYSUTCDATETIME();
    UPDATE dbo.AutomatedCallQueue SET LockedUntil=DATEADD(minute,@LeaseMinutes,@Now),UpdatedAt=@Now
    WHERE SystemID=@QueueId AND Status='PROCESSING' AND LeaseToken=@LeaseToken
        AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>@Now;
    SELECT CAST(CASE WHEN @@ROWCOUNT=1 THEN 1 ELSE 0 END AS bit) AS Renewed;
END;
GO
CREATE OR ALTER PROCEDURE dbo.activealarms_call_GetNextSetOfJobs
    @BatchSize int,@WorkerId varchar(100),@LeaseMinutes int
AS
BEGIN
    SET NOCOUNT ON;
    IF @BatchSize IS NULL OR @BatchSize<=0 OR @WorkerId IS NULL OR LEN(LTRIM(RTRIM(@WorkerId)))=0
        OR @LeaseMinutes IS NULL OR @LeaseMinutes<=0 THROW 51000,'Valid batch size, worker identity and lease duration are required.',1;
    DECLARE @Now datetime2(3)=SYSUTCDATETIME();
    ;WITH JobsToClaim AS
    (
        SELECT TOP(@BatchSize) * FROM dbo.AutomatedCallQueue WITH(UPDLOCK,READPAST,ROWLOCK)
        WHERE ((Status='READY' AND AvailableAt<=@Now AND (AttemptCount<MaxAttempts OR CurrentAttemptId IS NOT NULL OR RecoveryCount>0))
            OR (Status='PROCESSING' AND LockedUntil<=@Now))
            AND EnqueueIdempotencyKey IS NOT NULL AND VariablesJSON IS NOT NULL AND OID IS NOT NULL AND FlowId IS NOT NULL
        ORDER BY Priority DESC,AvailableAt,SystemID
    )
    UPDATE JobsToClaim SET Status='PROCESSING',LockedBy=@WorkerId,LockedAt=@Now,
        LockedUntil=DATEADD(minute,@LeaseMinutes,@Now),LeaseToken=NEWID(),
        AttemptCount=CASE WHEN Status='READY' AND AttemptCount<MaxAttempts THEN AttemptCount+1 ELSE AttemptCount END,
        LastAttemptAt=@Now,StartedAt=COALESCE(StartedAt,@Now),UpdatedAt=@Now
    OUTPUT inserted.SystemID,inserted.ActiveAlarmID,inserted.OID,inserted.FlowId,inserted.VariablesJSON,
        inserted.EnqueueIdempotencyKey,inserted.AttemptCount,inserted.MaxAttempts,inserted.LeaseToken,inserted.LockedUntil;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_ReadRecovery
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@QueueId AND Status='PROCESSING'
        AND LeaseToken=@LeaseToken AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>SYSUTCDATETIME())
        THROW 51002,'Recovery requires current ownership.',1;
    IF EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue q WHERE q.SystemID=@QueueId AND q.CurrentAttemptId IS NULL
        AND EXISTS(SELECT 1 FROM dbo.AutomatedCalls a WHERE a.AutomatedCallQueueId=q.SystemID))
        THROW 51001,'Unidentified existing attempt requires operator disposition.',1;
    SELECT COALESCE(a.SystemID,0) AS CallId,a.AutomatedCallQueueId AS QueueId,a.QueueLeaseToken,q.LeaseToken AS CurrentLeaseToken,
        a.OID,a.PhoneE164,a.FlowId,a.TwiML,a.Parameters,t.TemplateJSON AS TemplateJson,t.TwiML AS TemplateTwiML,q.VariablesJSON AS VariablesJson,
        a.SubmissionState,a.SubmissionErrorCode,a.providerCallId AS ProviderCallId,a.SubmissionRequestJSON,a.CallStatus,q.SubmissionCount,q.RecoveryCount
    FROM dbo.AutomatedCallQueue q LEFT JOIN dbo.AutomatedCalls a ON a.SystemID=q.CurrentAttemptId AND a.AutomatedCallQueueId=q.SystemID
    LEFT JOIN dbo.CallFlowTemplates t ON t.SystemID=a.FlowId
    WHERE q.SystemID=@QueueId AND q.LeaseToken=@LeaseToken;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_BeginAttempt
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100),@PhoneE164 varchar(16)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @LeaseToken IS NULL OR @WorkerId IS NULL OR LEN(LTRIM(RTRIM(@WorkerId)))=0
        OR @PhoneE164 IS NULL OR LEN(@PhoneE164) NOT BETWEEN 3 AND 16 OR LEFT(@PhoneE164,1)<>'+'
        OR SUBSTRING(@PhoneE164,2,1) NOT LIKE '[1-9]' OR SUBSTRING(@PhoneE164,2,15) LIKE '%[^0-9]%'
        THROW 51000,'Valid claim identity and E.164 recipient are required.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Oid varchar(20),@FlowId int,@Until datetime2(3),@CallId int,@State varchar(20),@Sid varchar(200),@Error varchar(100);
        SELECT @CallId=CurrentAttemptId FROM dbo.AutomatedCallQueue WHERE SystemID=@QueueId;
        SELECT @State=SubmissionState,@Sid=providerCallId,@Error=SubmissionErrorCode FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@CallId;
        SELECT @Oid=OID,@FlowId=FlowId,@Until=LockedUntil FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@QueueId AND Status='PROCESSING' AND LeaseToken=@LeaseToken
            AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>SYSUTCDATETIME();
        IF @Until IS NULL THROW 51002,'Queue claim is not currently owned by this worker.',1;
        IF @CallId IS NOT NULL AND (@Sid IS NOT NULL OR @State IS NULL OR @State NOT IN('READY','REJECTED') OR (@State='REJECTED' AND (@Error IS NULL OR @Error<>'TWILIO_20429')))
            THROW 51001,'Existing submission requires reconciliation, not a new attempt.',1;
        IF @State='REJECTED' SET @CallId=NULL;
        IF @CallId IS NULL
        BEGIN
            IF @State IS NULL AND EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@QueueId AND QueueLeaseToken IS NULL)
                THROW 51001,'Legacy attempt identity requires operator disposition.',1;
            SELECT @CallId=SystemID FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken;
            IF @CallId IS NULL
            BEGIN
                IF NOT EXISTS(SELECT 1 FROM dbo.CallFlowTemplates WHERE SystemID=@FlowId AND DeletedDateTime IS NULL AND TemplateJSON IS NOT NULL AND TwiML IS NOT NULL)
                    THROW 51000,'Published flow data is required for preparation.',1;
                INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,CallStatus)
                VALUES(@QueueId,@LeaseToken,'Twilio',@Oid,@PhoneE164,@FlowId,'queued');
                SET @CallId=SCOPE_IDENTITY();
            END;
        END;
        IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@CallId AND AutomatedCallQueueId=@QueueId AND Provider='Twilio' AND OID=@Oid AND FlowId=@FlowId AND PhoneE164 IS NOT NULL)
            THROW 51001,'Existing attempt identity conflicts with the claim.',1;
        UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId=@CallId WHERE SystemID=@QueueId;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue claim expired during preparation.',1;
        SELECT a.SystemID AS CallId,a.AutomatedCallQueueId AS QueueId,a.QueueLeaseToken,a.OID,a.PhoneE164,a.FlowId,
            a.TwiML,a.Parameters,t.TemplateJSON AS TemplateJson,t.TwiML AS TemplateTwiML,q.VariablesJSON AS VariablesJson
        FROM dbo.AutomatedCalls a JOIN dbo.AutomatedCallQueue q ON q.SystemID=a.AutomatedCallQueueId
        JOIN dbo.CallFlowTemplates t ON t.SystemID=a.FlowId WHERE a.SystemID=@CallId;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_SavePreparation
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100),@CallId int,@TwiML nvarchar(max),@Parameters nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @TwiML IS NULL OR LEN(@TwiML)=0 OR @Parameters IS NULL OR ISJSON(@Parameters)<>1 OR LEFT(LTRIM(@Parameters),1)<>'{'
        THROW 51000,'Personalized bundle and parameter object are required.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Until datetime2(3),@ExistingXml nvarchar(max),@ExistingParameters nvarchar(max),@State varchar(20),@Sid varchar(200),@OriginalToken uniqueidentifier;
        SELECT @ExistingXml=TwiML,@ExistingParameters=Parameters,@State=SubmissionState,@Sid=providerCallId,@OriginalToken=QueueLeaseToken
        FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@CallId AND AutomatedCallQueueId=@QueueId AND Provider='Twilio';
        SELECT @Until=LockedUntil FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@QueueId AND Status='PROCESSING' AND LeaseToken=@LeaseToken
            AND (CurrentAttemptId=@CallId OR (CurrentAttemptId IS NULL AND @OriginalToken=@LeaseToken))
            AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>SYSUTCDATETIME();
        IF @Until IS NULL OR @OriginalToken IS NULL THROW 51002,'Preparation requires current ownership of this attempt.',1;
        IF @ExistingXml IS NOT NULL OR @ExistingParameters IS NOT NULL
        BEGIN
            IF @ExistingXml IS NULL OR @ExistingParameters IS NULL OR DATALENGTH(@ExistingXml)<>DATALENGTH(@TwiML)
                OR @ExistingXml COLLATE Latin1_General_100_BIN2<>@TwiML COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@ExistingParameters)<>DATALENGTH(@Parameters)
                OR @ExistingParameters COLLATE Latin1_General_100_BIN2<>@Parameters COLLATE Latin1_General_100_BIN2
                THROW 51001,'Personalized attempt snapshot is immutable.',1;
        END
        ELSE
        BEGIN
            IF @Sid IS NOT NULL OR @State IS NULL OR @State<>'READY' THROW 51001,'Submitted attempts cannot be prepared.',1;
            UPDATE dbo.AutomatedCalls SET TwiML=@TwiML,Parameters=@Parameters,UpdatedDateTime=SYSUTCDATETIME() WHERE SystemID=@CallId;
        END;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue claim expired during preparation.',1;
        COMMIT;
        SELECT @CallId AS CallId;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_RecoveryDisposition
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100),@Disposition varchar(20),@ErrorCode varchar(100),
    @DelaySeconds int,@MaxSubmissionAttempts int,@MaxPreparationAttempts int,@MaxRecoveryHours int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Disposition NOT IN('RETRY','WAIT','DEAD_LETTER') OR @DelaySeconds NOT BETWEEN 1 AND 3600
        OR @MaxSubmissionAttempts NOT BETWEEN 1 AND 10 OR @MaxPreparationAttempts NOT BETWEEN 1 AND 10 OR @MaxRecoveryHours NOT BETWEEN 1 AND 168
        THROW 51000,'Invalid recovery disposition.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @CallId int,@State varchar(20),@Sid varchar(200),@Error varchar(100),@Started datetime2(3),@Until datetime2(3),
            @Count int,@Retries int,@Now datetime2(3)=SYSUTCDATETIME();
        SELECT @CallId=CurrentAttemptId FROM dbo.AutomatedCallQueue WHERE SystemID=@QueueId;
        SELECT @State=SubmissionState,@Sid=providerCallId,@Error=SubmissionErrorCode,@Started=SubmissionStartedAt
        FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@CallId;
        SELECT @Until=LockedUntil,@Count=SubmissionCount,@Retries=PreparationFailureCount,@Started=COALESCE(@Started,StartedAt,CreatedAt)
        FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@QueueId AND Status='PROCESSING' AND LeaseToken=@LeaseToken
            AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>@Now;
        IF @Until IS NULL THROW 51002,'Disposition requires current ownership.',1;
        IF @Disposition='RETRY' AND (@Sid IS NOT NULL OR (@CallId IS NOT NULL AND (@State IS NULL OR @State NOT IN('READY','REJECTED') OR (@State='REJECTED' AND (@Error IS NULL OR @Error<>'TWILIO_20429')))))
            SET @Disposition='WAIT';
        IF @Disposition='DEAD_LETTER' AND @ErrorCode='VALIDATION_FAILED' AND (@Sid IS NOT NULL OR @State IN('SUBMITTING','UNKNOWN','ACCEPTED'))
            SET @Disposition='WAIT';
        IF @Started<=DATEADD(hour,-@MaxRecoveryHours,@Now) OR (@Disposition='RETRY' AND (@Count>=@MaxSubmissionAttempts OR (@State IS NULL OR @State='READY') AND @Retries>=@MaxPreparationAttempts-1))
        BEGIN
            SET @Disposition='DEAD_LETTER';
            SET @ErrorCode=CASE WHEN @Started<=DATEADD(hour,-@MaxRecoveryHours,@Now) THEN 'RECOVERY_EXHAUSTED' ELSE 'RETRY_EXHAUSTED' END;
        END;
        UPDATE dbo.AutomatedCallQueue SET Status=CASE WHEN @Disposition='DEAD_LETTER' THEN 'DEAD_LETTER' ELSE 'READY' END,
            AvailableAt=DATEADD(second,@DelaySeconds,@Now),RecoveryCount=RecoveryCount+CASE WHEN @Disposition='RETRY' THEN 1 ELSE 0 END,
            PreparationFailureCount=PreparationFailureCount+CASE WHEN @Disposition='RETRY' AND (@State IS NULL OR @State='READY') THEN 1 ELSE 0 END,
            DeadLetteredAt=CASE WHEN @Disposition='DEAD_LETTER' THEN @Now ELSE DeadLetteredAt END,
            LastErrorCode=@ErrorCode,LastErrorMessage=NULL,LockedBy=NULL,LockedAt=NULL,LockedUntil=NULL,LeaseToken=NULL,UpdatedAt=@Now
        WHERE SystemID=@QueueId;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Ownership expired during disposition.',1;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_Observe
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100),@CallId int,@ProviderCallId varchar(200),
    @CallStatus varchar(20),@CallDurationSeconds int=NULL,@ReconcileSeconds int=120,@MaxRecoveryHours int=24
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @CallStatus COLLATE Latin1_General_100_BIN2 NOT IN('queued','initiated','ringing','in-progress','completed','busy','failed','no-answer','canceled')
        OR @CallStatus IS NULL OR @CallDurationSeconds<0 OR @ReconcileSeconds NOT BETWEEN 1 AND 3600 OR @MaxRecoveryHours NOT BETWEEN 1 AND 168 THROW 51000,'Invalid provider observation.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Current varchar(20),@Until datetime2(3),@Now datetime2(3)=SYSUTCDATETIME(),@Terminal bit=0,@Started datetime2(3),@Exhausted bit=0;
        SELECT @Current=CallStatus,@Started=SubmissionStartedAt FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@CallId AND AutomatedCallQueueId=@QueueId AND providerCallId COLLATE Latin1_General_100_BIN2=@ProviderCallId COLLATE Latin1_General_100_BIN2 AND Provider='Twilio';
        IF @Current IS NULL THROW 51001,'Observation identity conflicts with the stored call.',1;
        SELECT @Until=LockedUntil,@Started=COALESCE(@Started,StartedAt,CreatedAt) FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@QueueId AND CurrentAttemptId=@CallId AND Status='PROCESSING' AND LeaseToken=@LeaseToken
            AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>@Now;
        IF @Until IS NULL THROW 51002,'Observation requires current ownership.',1;
        IF @Current IN('completed','busy','failed','no-answer','canceled') SET @CallStatus=@Current;
        SET @Terminal=CASE WHEN @CallStatus IN('completed','busy','failed','no-answer','canceled') THEN 1 ELSE 0 END;
        SET @Exhausted=CASE WHEN @Terminal=0 AND @Started<=DATEADD(hour,-@MaxRecoveryHours,@Now) THEN 1 ELSE 0 END;
        IF @Terminal=1
        BEGIN
            UPDATE dbo.AutomatedCalls SET CallStatus=@CallStatus,EndedDateTime=COALESCE(EndedDateTime,@Now),
                CallDurationSeconds=COALESCE(CallDurationSeconds,@CallDurationSeconds),UpdatedDateTime=@Now WHERE SystemID=@CallId;
        END;
        DECLARE @EventJSON nvarchar(max)=(SELECT @ProviderCallId AS callSid,@CallStatus AS callStatus,@CallDurationSeconds AS durationSeconds FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
        INSERT dbo.AutomatedCallEvents(CallId,CallEventDateTime,EventType,EventJSON,IdempotencyKey)
        VALUES(@CallId,@Now,'CallReconciled',@EventJSON,'voice:reconcile:'+LOWER(CONVERT(varchar(36),@LeaseToken)));
        UPDATE dbo.AutomatedCallQueue SET Status=CASE WHEN @Exhausted=1 THEN 'DEAD_LETTER' WHEN @Terminal=0 THEN 'READY' WHEN @CallStatus='completed' THEN 'COMPLETED' ELSE 'FAILED' END,
            AvailableAt=DATEADD(second,@ReconcileSeconds,@Now),CompletedAt=CASE WHEN @CallStatus='completed' THEN COALESCE(CompletedAt,@Now) ELSE CompletedAt END,
            FailedAt=CASE WHEN @Terminal=1 AND @CallStatus<>'completed' THEN COALESCE(FailedAt,@Now) ELSE FailedAt END,
            LastErrorCode=CASE WHEN @Exhausted=1 THEN 'RECOVERY_EXHAUSTED' WHEN @Terminal=1 AND @CallStatus<>'completed' THEN @CallStatus ELSE NULL END,LastErrorMessage=NULL,
            DeadLetteredAt=CASE WHEN @Exhausted=1 THEN @Now ELSE DeadLetteredAt END,
            LockedBy=NULL,LockedAt=NULL,LockedUntil=NULL,LeaseToken=NULL,UpdatedAt=@Now WHERE SystemID=@QueueId;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Ownership expired during observation.',1;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_BeginSubmission
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100),@CallId int,
    @SubmissionToken uniqueidentifier,@RequestJSON nvarchar(max),@CurrentLeaseToken uniqueidentifier=NULL,@MaxSubmissionAttempts int=3,@MaxRecoveryHours int=24
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @SubmissionToken IS NULL OR @SubmissionToken='00000000-0000-0000-0000-000000000000'
        OR @RequestJSON IS NULL OR ISJSON(@RequestJSON)<>1 OR LEFT(LTRIM(@RequestJSON),1)<>'{' OR @MaxSubmissionAttempts NOT BETWEEN 1 AND 10 OR @MaxRecoveryHours NOT BETWEEN 1 AND 168
        THROW 51000,'A submission token and request snapshot are required.',1;
    SET @CurrentLeaseToken=COALESCE(@CurrentLeaseToken,@LeaseToken);
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Status varchar(20),@QueueToken uniqueidentifier,@Owner varchar(100),@Until datetime2(3),@CurrentAttempt int,@Count int,@QueueMax int,@QueueStarted datetime2(3),
            @Found bit=0,@State varchar(20),@Sid varchar(200),@Phone varchar(16),@Xml nvarchar(max),@Parameters nvarchar(max),
            @CallStatus varchar(20),@Token uniqueidentifier,@ShouldSubmit bit=0,@StoredRequest nvarchar(max),
            @OpeningStepId nvarchar(200),@OpeningExecutionId uniqueidentifier,@OpeningResponse nvarchar(max),
            @BundleXml xml,@ResponseXml xml,@ExpectedResponse xml;
        SELECT @Found=1,@State=SubmissionState,@Token=SubmissionToken,@Sid=providerCallId,@Phone=PhoneE164,
            @Xml=TwiML,@Parameters=Parameters,@CallStatus=CallStatus,@StoredRequest=SubmissionRequestJSON
        FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@CallId AND AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken AND Provider='Twilio';
        IF @Found=0 THROW 51002,'Attempt does not belong to the original claim.',1;
        SELECT @Status=Status,@QueueToken=LeaseToken,@Owner=LockedBy,@Until=LockedUntil,@CurrentAttempt=CurrentAttemptId,@Count=SubmissionCount,@QueueMax=MaxAttempts,@QueueStarted=COALESCE(StartedAt,CreatedAt)
        FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@QueueId;
        IF @Sid IS NOT NULL AND (@State IS NULL OR @State IN('READY','SUBMITTING'))
        BEGIN
            UPDATE dbo.AutomatedCalls SET SubmissionState='ACCEPTED',SubmissionCompletedAt=COALESCE(SubmissionCompletedAt,SYSUTCDATETIME()) WHERE SystemID=@CallId;
            SET @State='ACCEPTED';
        END;
        IF @State='READY' AND @Sid IS NULL
        BEGIN
            IF @Status IS NULL OR @Status<>'PROCESSING' OR @QueueToken IS NULL OR @QueueToken<>@CurrentLeaseToken
                OR (@CurrentAttempt IS NOT NULL AND @CurrentAttempt<>@CallId)
                OR (@CurrentAttempt IS NULL AND @CurrentLeaseToken<>@LeaseToken)
                OR @Owner IS NULL OR @WorkerId IS NULL OR @Owner COLLATE Latin1_General_100_BIN2<>@WorkerId COLLATE Latin1_General_100_BIN2
                OR @Until IS NULL OR @Until<=SYSUTCDATETIME() THROW 51002,'Submission requires current queue lease ownership.',1;
            IF @Count>=@MaxSubmissionAttempts OR @Count>=@QueueMax THROW 51003,'Submission budget exhausted.',1;
            IF @QueueStarted<=DATEADD(hour,-@MaxRecoveryHours,SYSUTCDATETIME()) THROW 51003,'Submission recovery age exhausted.',1;
            IF EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@QueueId AND SystemID<@CallId
                AND (providerCallId IS NOT NULL OR SubmissionState IS NULL OR SubmissionState IN('SUBMITTING','UNKNOWN','ACCEPTED')))
                THROW 51001,'Prior submission outcome requires operator disposition.',1;
            IF @Xml IS NULL OR @Parameters IS NULL OR @CallStatus IS NULL OR @CallStatus<>'queued'
                THROW 51000,'Only a persisted prepared attempt may be submitted.',1;
            IF TRY_CONVERT(int,JSON_VALUE(@RequestJSON,'$.CallId')) IS NULL OR TRY_CONVERT(int,JSON_VALUE(@RequestJSON,'$.CallId'))<>@CallId
                OR JSON_VALUE(@RequestJSON,'$.To') IS NULL OR JSON_VALUE(@RequestJSON,'$.To')<>@Phone
                THROW 51000,'Submission request identity does not match the attempt.',1;
            IF JSON_QUERY(@RequestJSON,'$.OpeningStep') IS NOT NULL
            BEGIN
                SELECT @OpeningStepId=StepId,@OpeningExecutionId=TRY_CONVERT(uniqueidentifier,ExecutionId),@OpeningResponse=ResponseTwiML
                FROM OPENJSON(@RequestJSON,'$.OpeningStep') WITH(StepId nvarchar(200),ExecutionId nvarchar(100),ResponseTwiML nvarchar(max));
                SET @BundleXml=TRY_CONVERT(xml,@Xml); SET @ResponseXml=TRY_CONVERT(xml,@OpeningResponse);
                IF NULLIF(LTRIM(RTRIM(@OpeningStepId)),N'') IS NULL OR @OpeningExecutionId IS NULL
                    OR @OpeningExecutionId='00000000-0000-0000-0000-000000000000' OR @BundleXml IS NULL OR @ResponseXml IS NULL
                    OR @ResponseXml.value('count(/*)','int')<>1 OR @ResponseXml.exist('/Response')<>1 OR @OpeningResponse LIKE N'%{{%'
                    OR @BundleXml.value('(/CallFlow/@initialStep)[1]','nvarchar(200)') COLLATE Latin1_General_100_BIN2<>@OpeningStepId COLLATE Latin1_General_100_BIN2
                    OR @BundleXml.value('count(/CallFlow/Step[@id=sql:variable("@OpeningStepId")]/Response)','int')<>1
                    THROW 51000,'Inline opening step is invalid.',1;
                SET @ExpectedResponse=TRY_CONVERT(xml,REPLACE(CONVERT(nvarchar(max),@BundleXml.query('/CallFlow/Step[@id=sql:variable("@OpeningStepId")]/Response')),
                    N'{{executionId}}',LOWER(CONVERT(nvarchar(36),@OpeningExecutionId))));
                IF @ExpectedResponse IS NULL OR CONVERT(nvarchar(max),@ExpectedResponse) COLLATE Latin1_General_100_BIN2<>CONVERT(nvarchar(max),@ResponseXml) COLLATE Latin1_General_100_BIN2
                    THROW 51001,'Inline response does not match the prepared opening step.',1;
                IF EXISTS(SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId=@CallId AND IdempotencyKey='voice:start')
                    THROW 51001,'Opening execution has already been issued.',1;
            END;
            UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId=@CallId,SubmissionCount=SubmissionCount+1 WHERE SystemID=@QueueId;
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
CREATE OR ALTER PROCEDURE dbo.automatedcalls_Status
    @CallId int,@ProviderCallId varchar(200),@PhoneE164 varchar(16),@CallStatus varchar(20),
    @SequenceNumber int,@Timestamp datetime2(3),@CallDurationSeconds int=NULL,@SipResponseCode int=NULL,@ParametersJSON nvarchar(max)=N'{}'
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
        DECLARE @Found bit=0,@Provider varchar(100),@ExistingSid varchar(200),@ExistingPhone varchar(16),
            @CurrentStatus varchar(20),@LastSequence int,@EndedDateTime datetime2(3),@QueueId int,@QueueLeaseToken uniqueidentifier,
            @Outcome varchar(30)='recorded',@Applied bit=0,@QueueFinalized bit=0,@IgnoreReason varchar(40),@Terminal bit=0,
            @Now datetime2(3)=SYSUTCDATETIME(),@Key varchar(255)='voice:status:'+@ProviderCallId+':'+CONVERT(varchar(11),@SequenceNumber);
        SELECT @Found=1,@Provider=Provider,@ExistingSid=providerCallId,@ExistingPhone=PhoneE164,
            @CurrentStatus=CallStatus,@LastSequence=LastStatusSequenceNumber,@EndedDateTime=EndedDateTime,
            @QueueId=AutomatedCallQueueId,@QueueLeaseToken=QueueLeaseToken
        FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@CallId;
        IF @Found=0 SET @Outcome='not-found';
        ELSE IF @Provider IS NULL OR LOWER(@Provider)<>'twilio' SET @Outcome='provider-mismatch';
        ELSE IF @ExistingPhone IS NULL OR @PhoneE164 IS NULL OR @ExistingPhone<>@PhoneE164 SET @Outcome='destination-mismatch';
        ELSE IF NULLIF(@ProviderCallId,'') IS NULL OR (@ExistingSid IS NOT NULL AND @ExistingSid COLLATE Latin1_General_100_BIN2<>@ProviderCallId COLLATE Latin1_General_100_BIN2) SET @Outcome='call-sid-mismatch';
        ELSE IF @CallStatus IS NULL OR @CallStatus COLLATE Latin1_General_100_BIN2 NOT IN('queued','initiated','ringing','in-progress','completed','busy','failed','no-answer','canceled')
            OR @SequenceNumber IS NULL OR @SequenceNumber<0 OR @Timestamp IS NULL OR @CallDurationSeconds<0 OR @SipResponseCode NOT BETWEEN 100 AND 699
            OR @ParametersJSON IS NULL OR ISJSON(@ParametersJSON)<>1 SET @Outcome='invalid-status';
        ELSE IF EXISTS(SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId=@CallId AND IdempotencyKey=@Key) SET @Outcome='duplicate';
        IF @Outcome='recorded'
        BEGIN
            IF @EndedDateTime IS NOT NULL OR @CurrentStatus IN('completed','busy','failed','no-answer','canceled') SET @IgnoreReason='already-terminal';
            ELSE IF @LastSequence IS NOT NULL AND @SequenceNumber<=@LastSequence SET @IgnoreReason='older-sequence';
            ELSE IF CASE @CallStatus WHEN 'queued' THEN 0 WHEN 'initiated' THEN 1 WHEN 'ringing' THEN 2 WHEN 'in-progress' THEN 3 ELSE 4 END
                < CASE @CurrentStatus WHEN 'queued' THEN 0 WHEN 'initiated' THEN 1 WHEN 'ringing' THEN 2 WHEN 'in-progress' THEN 3 ELSE -1 END SET @IgnoreReason='status-regression';
            ELSE SET @Applied=1;
            SET @Terminal=CASE WHEN @CallStatus IN('completed','busy','failed','no-answer','canceled') THEN 1 ELSE 0 END;
            IF @Applied=1
            BEGIN
                UPDATE dbo.AutomatedCalls SET providerCallId=COALESCE(providerCallId,@ProviderCallId),CallStatus=@CallStatus,
                    LastStatusSequenceNumber=@SequenceNumber,LastStatusDateTime=@Timestamp,UpdatedDateTime=@Now,
                    EndedDateTime=CASE WHEN @Terminal=1 THEN @Timestamp ELSE EndedDateTime END,
                    CallDurationSeconds=CASE WHEN @Terminal=1 THEN @CallDurationSeconds ELSE CallDurationSeconds END WHERE SystemID=@CallId;
                IF @CallStatus='completed' EXEC dbo.automatedcalls_QueueCompletionNote @CallId;
                IF @Terminal=1 AND @QueueId IS NOT NULL AND @QueueLeaseToken IS NOT NULL
                BEGIN
                    UPDATE dbo.AutomatedCallQueue SET Status=CASE WHEN @CallStatus='completed' THEN 'COMPLETED' ELSE 'FAILED' END,
                        CompletedAt=CASE WHEN @CallStatus='completed' THEN @Timestamp END,FailedAt=CASE WHEN @CallStatus<>'completed' THEN @Timestamp END,
                        LastErrorCode=CASE WHEN @CallStatus<>'completed' THEN 'twilio:'+@CallStatus END,
                        LastErrorMessage=CASE WHEN @CallStatus<>'completed' THEN 'Twilio call ended with status '+@CallStatus END,
                        LockedBy=NULL,LockedAt=NULL,LockedUntil=NULL,LeaseToken=NULL,UpdatedAt=@Now,UpdatedDateTime=@Now
                    WHERE SystemID=@QueueId AND Status IN('PROCESSING','READY','DEAD_LETTER')
                        AND (CurrentAttemptId=@CallId OR (CurrentAttemptId IS NULL AND Status='PROCESSING' AND LeaseToken=@QueueLeaseToken));
                    IF @@ROWCOUNT=1 SET @QueueFinalized=1;
                END;
            END
            ELSE IF @ExistingSid IS NULL UPDATE dbo.AutomatedCalls SET providerCallId=@ProviderCallId,UpdatedDateTime=@Now WHERE SystemID=@CallId;
            DECLARE @EventJSON nvarchar(max)=(SELECT @ProviderCallId AS providerCallId,@CallStatus AS callStatus,@SequenceNumber AS sequenceNumber,
                CONVERT(varchar(33),@Timestamp,126)+'Z' AS timestamp,CONVERT(varchar(33),@Now,126)+'Z' AS receivedDateTime,
                @CallDurationSeconds AS callDurationSeconds,@SipResponseCode AS sipResponseCode,@Applied AS applied,@IgnoreReason AS ignoreReason,
                @QueueFinalized AS queueFinalized,JSON_QUERY(@ParametersJSON) AS parameters FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
            INSERT dbo.AutomatedCallEvents(CallId,CallEventDateTime,EventType,EventJSON,IdempotencyKey)
            VALUES(@CallId,@Timestamp,'CallStatusReceived',@EventJSON,@Key);
        END;
        COMMIT;
        SELECT @Outcome AS Outcome,@Applied AS Applied,@QueueFinalized AS QueueFinalized,@IgnoreReason AS IgnoreReason;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO