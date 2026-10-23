SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-- AutoCallDB: participant Robo Call devices and answering-machine detection.
-- Deploy after 058.sql (and tenant 059.sql), then the feedback API, then the worker and parsers.
-- Each queue row dials exactly one device. When a call is unsuccessful (busy, no-answer, failed,
-- canceled, or answered by a machine) and the job lists another device, a follow-on queue row is
-- created for it immediately. The tenant keeps waiting on the original (root) queue row.
IF OBJECT_ID(N'dbo.automatedcalls_QueueWaitRelease', N'P') IS NULL
    THROW 51000, 'Deploy 058.sql before Robo Call devices.', 1;
GO
IF COL_LENGTH(N'dbo.AutomatedCallQueue', N'CallDeviceIndex') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD CallDeviceIndex int NOT NULL CONSTRAINT DF_AutoCallQueue_CallDeviceIndex DEFAULT (0)
        CONSTRAINT CK_AutoCallQueue_CallDeviceIndex CHECK (CallDeviceIndex >= 0);
IF COL_LENGTH(N'dbo.AutomatedCallQueue', N'RootQueueId') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD RootQueueId int NULL;
IF COL_LENGTH(N'dbo.AutomatedCalls', N'PhoneType') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD PhoneType varchar(20) NULL;
IF COL_LENGTH(N'dbo.AutomatedCalls', N'DeviceIndex') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD DeviceIndex int NULL;
IF COL_LENGTH(N'dbo.AutomatedCalls', N'AnsweredBy') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD AnsweredBy varchar(30) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallQueue') AND name = N'UX_AutoCallQueue_RootDevice')
    CREATE UNIQUE INDEX UX_AutoCallQueue_RootDevice ON dbo.AutomatedCallQueue(RootQueueId, CallDeviceIndex) WHERE RootQueueId IS NOT NULL;
GO

-- DEVICE notes report an unsuccessful device. A skipped device has no call row, so CallId may be NULL.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox')
           AND name = N'CK_AutoCallNote_Kind' AND definition NOT LIKE N'%DEVICE%')
    ALTER TABLE dbo.AutomatedCallNoteOutbox DROP CONSTRAINT CK_AutoCallNote_Kind;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND name = N'CK_AutoCallNote_Kind')
    ALTER TABLE dbo.AutomatedCallNoteOutbox ADD CONSTRAINT CK_AutoCallNote_Kind CHECK (Kind IN ('COMPLETED', 'TRANSCRIPT', 'RELEASE', 'DEVICE'));
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox')
           AND name = N'CK_AutoCallNote_Target' AND definition NOT LIKE N'%DEVICE%')
    ALTER TABLE dbo.AutomatedCallNoteOutbox DROP CONSTRAINT CK_AutoCallNote_Target;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND name = N'CK_AutoCallNote_Target')
    ALTER TABLE dbo.AutomatedCallNoteOutbox ADD CONSTRAINT CK_AutoCallNote_Target CHECK
        ((Kind = 'RELEASE' AND QueueId > 0 AND ActiveAlarmID > 0 AND SourceId = QueueId)
         OR (Kind = 'DEVICE' AND QueueId > 0 AND ActiveAlarmID > 0)
         OR (Kind IN ('COMPLETED', 'TRANSCRIPT') AND CallId IS NOT NULL));
-- A unique constraint treats NULL CallIds as equal; skipped-device notes need a filtered index instead.
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND name = N'UQ_AutoCallNote_Source')
    ALTER TABLE dbo.AutomatedCallNoteOutbox DROP CONSTRAINT UQ_AutoCallNote_Source;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND name = N'UX_AutoCallNote_Source')
    CREATE UNIQUE INDEX UX_AutoCallNote_Source ON dbo.AutomatedCallNoteOutbox(CallId, Kind, SourceId) WHERE CallId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND name = N'UX_AutoCallNote_Device')
    CREATE UNIQUE INDEX UX_AutoCallNote_Device ON dbo.AutomatedCallNoteOutbox(QueueId) WHERE Kind = 'DEVICE';
GO

-- The tenant waits on the queue row it enqueued, so follow-on device rows release the root row.
CREATE OR ALTER PROCEDURE dbo.automatedcalls_QueueWaitRelease
    @QueueId int
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.AutomatedCallNoteOutbox (CallId, Kind, SourceId, OID, HistoryID, OccurredAt, QueueId, ActiveAlarmID)
    SELECT q.CurrentAttemptId, 'RELEASE', r.SystemID, r.OID, CONVERT(int, JSON_VALUE(r.OptionsJSON, '$.historyId')),
        SYSUTCDATETIME(), r.SystemID, r.ActiveAlarmID
    FROM dbo.AutomatedCallQueue q
    JOIN dbo.AutomatedCallQueue r ON r.SystemID = COALESCE(q.RootQueueId, q.SystemID)
    WHERE q.SystemID = @QueueId AND r.OID IS NOT NULL AND r.ActiveAlarmID > 0
      AND JSON_VALUE(r.OptionsJSON, '$.waitForCompletion') = 'true'
      AND TRY_CONVERT(int, JSON_VALUE(r.OptionsJSON, '$.historyId')) > 0
      AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox o WITH (UPDLOCK, HOLDLOCK) WHERE o.Kind = 'RELEASE' AND o.QueueId = r.SystemID);
END;
GO

-- Machine-answered calls are reported by a DEVICE note instead of the completion note.
CREATE OR ALTER PROCEDURE dbo.automatedcalls_QueueCompletionNote
    @CallId int
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.AutomatedCallNoteOutbox (CallId, Kind, SourceId, OID, HistoryID, OccurredAt, MessagesJSON)
    SELECT i.SystemID, 'COMPLETED', 0, q.OID, CONVERT(int, JSON_VALUE(q.OptionsJSON, '$.historyId')), i.EndedDateTime,
        (SELECT i.PhoneType AS phoneType FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
    FROM dbo.AutomatedCalls i
    JOIN dbo.AutomatedCallQueue q ON q.SystemID = i.AutomatedCallQueueId
    WHERE i.SystemID = @CallId AND i.CallStatus = 'completed'
      AND i.EndedDateTime IS NOT NULL
      AND ISNULL(i.AnsweredBy, '') NOT LIKE 'machine[_]%' AND ISNULL(i.AnsweredBy, '') <> 'fax'
      AND TRY_CONVERT(int, JSON_VALUE(q.OptionsJSON, '$.historyId')) > 0
      AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox o WHERE o.CallId = i.SystemID AND o.Kind = 'COMPLETED' AND o.SourceId = 0);
END;
GO

-- Called inside the transaction that ends a queue row unsuccessfully. Writes one DEVICE note,
-- then either creates the next device's queue row or releases the tenant wait.
CREATE OR ALTER PROCEDURE dbo.automatedcalls_AdvanceDevice
    @QueueId int,@CallId int,@Outcome varchar(30),@AnsweredBy varchar(30),@MessageLeft bit,@OccurredAt datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Index int,@Options nvarchar(max),@Root int,@Type varchar(20),@NextType varchar(20),@HistoryId int;
    SELECT @Index=CallDeviceIndex,@Options=OptionsJSON,@Root=COALESCE(RootQueueId,SystemID)
    FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@QueueId;
    IF @Index IS NULL RETURN;
    IF @Options IS NULL OR ISJSON(@Options)<>1 OR JSON_QUERY(@Options,'$.callDevices') IS NULL
    BEGIN
        EXEC dbo.automatedcalls_QueueWaitRelease @QueueId;
        RETURN;
    END;
    -- OPENJSON by index: a computed JSON_VALUE path needs compatibility level 140.
    SELECT @Type=JSON_VALUE(d.value,'$.type') FROM OPENJSON(JSON_QUERY(@Options,'$.callDevices')) d
    WHERE d.type=5 AND d.[key]=CONVERT(nvarchar(11),@Index);
    SELECT @NextType=JSON_VALUE(d.value,'$.type') FROM OPENJSON(JSON_QUERY(@Options,'$.callDevices')) d
    WHERE d.type=5 AND d.[key]=CONVERT(nvarchar(11),@Index+1);
    SET @HistoryId=TRY_CONVERT(int,JSON_VALUE(@Options,'$.historyId'));
    IF @HistoryId>0 AND NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallNoteOutbox WITH(UPDLOCK,HOLDLOCK) WHERE Kind='DEVICE' AND QueueId=@QueueId)
        INSERT dbo.AutomatedCallNoteOutbox(CallId,Kind,SourceId,OID,HistoryID,OccurredAt,MessagesJSON,QueueId,ActiveAlarmID)
        SELECT @CallId,'DEVICE',@Index,q.OID,@HistoryId,COALESCE(@OccurredAt,SYSUTCDATETIME()),
            (SELECT @Type AS phoneType,@Outcome AS status,@AnsweredBy AS answeredBy,CONVERT(bit,ISNULL(@MessageLeft,0)) AS messageLeft,
                @NextType AS nextPhoneType FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),
            @QueueId,q.ActiveAlarmID
        FROM dbo.AutomatedCallQueue q WHERE q.SystemID=@QueueId AND q.OID IS NOT NULL AND q.ActiveAlarmID>0;
    IF @NextType IS NULL
    BEGIN
        EXEC dbo.automatedcalls_QueueWaitRelease @QueueId;
        RETURN;
    END;
    IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK) WHERE RootQueueId=@Root AND CallDeviceIndex=@Index+1)
        INSERT dbo.AutomatedCallQueue
            (ActiveAlarmID,OID,FlowId,CallStatus,Priority,AvailableAt,MaxAttempts,VariablesJSON,EnqueueIdempotencyKey,
             POGroupNum,OptionsJSON,CallDeviceIndex,RootQueueId)
        SELECT ActiveAlarmID,OID,FlowId,CallStatus,Priority,SYSUTCDATETIME(),MaxAttempts,VariablesJSON,NEWID(),
            POGroupNum,OptionsJSON,@Index+1,@Root
        FROM dbo.AutomatedCallQueue WHERE SystemID=@QueueId;
END;
GO

-- Called after a call row reaches a terminal status. A human-answered completed call releases the wait;
-- anything else moves on to the next device.
CREATE OR ALTER PROCEDURE dbo.automatedcalls_ApplyCallOutcome
    @CallId int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @QueueId int,@Status varchar(20),@AnsweredBy varchar(30),@Ended datetime2(3),@MessageLeft bit=0;
    SELECT @QueueId=c.AutomatedCallQueueId,@Status=c.CallStatus,@AnsweredBy=c.AnsweredBy,@Ended=COALESCE(c.EndedDateTime,SYSUTCDATETIME())
    FROM dbo.AutomatedCalls c WHERE c.SystemID=@CallId;
    IF @QueueId IS NULL OR @Status IS NULL OR @Status NOT IN('completed','busy','failed','no-answer','canceled') RETURN;
    -- A late callback for a job the worker already gave up on never tries another device.
    IF EXISTS(SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID=@QueueId AND Status='DEAD_LETTER')
    BEGIN
        EXEC dbo.automatedcalls_QueueWaitRelease @QueueId;
        RETURN;
    END;
    IF @Status='completed' AND ISNULL(@AnsweredBy,'') NOT LIKE 'machine[_]%' AND ISNULL(@AnsweredBy,'')<>'fax'
    BEGIN
        EXEC dbo.automatedcalls_QueueWaitRelease @QueueId;
        RETURN;
    END;
    IF @AnsweredBy LIKE 'machine[_]end[_]%' AND EXISTS(SELECT 1 FROM dbo.AutomatedCallEvents
        WHERE CallId=@CallId AND IdempotencyKey='voice:start' AND ISJSON(EventJSON)=1 AND JSON_VALUE(EventJSON,'$.stepId')<>'$hangup')
        SET @MessageLeft=1;
    EXEC dbo.automatedcalls_AdvanceDevice @QueueId,@CallId,@Status,@AnsweredBy,@MessageLeft,@Ended;
END;
GO

-- The owned job's device has no usable phone number: end this row and move on without dialling.
CREATE OR ALTER PROCEDURE dbo.automatedcalls_SkipDevice
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Until datetime2(3),@Now datetime2(3)=SYSUTCDATETIME();
        SELECT @Until=LockedUntil FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@QueueId AND Status='PROCESSING' AND LeaseToken=@LeaseToken
            AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>@Now;
        IF @Until IS NULL THROW 51002,'Skipping a device requires current ownership.',1;
        IF EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@QueueId AND (providerCallId IS NOT NULL OR SubmissionToken IS NOT NULL))
            THROW 51001,'A submitted attempt cannot skip its device.',1;
        UPDATE dbo.AutomatedCallQueue SET Status='FAILED',FailedAt=@Now,UpdatedAt=@Now,UpdatedDateTime=@Now,
            LastErrorCode='NO_PHONE_NUMBER',LastErrorMessage=N'No phone number on file for the call device.',
            LockedBy=NULL,LockedAt=NULL,LockedUntil=NULL,LeaseToken=NULL
        WHERE SystemID=@QueueId;
        EXEC dbo.automatedcalls_AdvanceDevice @QueueId,NULL,'no-phone-number',NULL,0,@Now;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue claim expired while skipping the device.',1;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
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
    @EnqueueIdempotencyKey uniqueidentifier = NULL,
    @POGroupNum varchar(20) = NULL,
    @OptionsJSON nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET ANSI_WARNINGS ON;
    SET ARITHABORT ON;
    SET CONCAT_NULL_YIELDS_NULL ON;
    SET NUMERIC_ROUNDABORT OFF;
    IF @ActiveAlarmID IS NULL OR @ActiveAlarmID <= 0 OR @OID IS NULL OR LEN(LTRIM(RTRIM(@OID))) = 0
        OR @FlowId IS NULL OR @FlowId <= 0 OR @MaxAttempts IS NULL OR @MaxAttempts <= 0
        OR @Priority IS NULL OR @EnqueueIdempotencyKey IS NULL
        OR @EnqueueIdempotencyKey = '00000000-0000-0000-0000-000000000000'
        OR @VariablesJSON IS NULL OR ISJSON(@VariablesJSON) <> 1 OR LEFT(LTRIM(@VariablesJSON), 1) <> '{'
        THROW 51000, 'A complete auto-call request snapshot is required.', 1;
    IF @POGroupNum IS NOT NULL AND LEN(LTRIM(RTRIM(@POGroupNum))) = 0
        THROW 51000, 'POGroupNum must be nonblank when supplied.', 1;
    IF @OptionsJSON IS NOT NULL
    BEGIN
        IF ISJSON(@OptionsJSON) <> 1 OR LEFT(LTRIM(@OptionsJSON), 1) <> '{'
            THROW 51000, 'OptionsJSON must be an object.', 1;
        IF (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON)) - (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'waitForCompletion' AND type = 3) <> 3
            OR NOT EXISTS (SELECT 1 FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'historyId' AND type = 2 AND TRY_CONVERT(int, value) > 0)
            OR NOT EXISTS (SELECT 1 FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'postRecordingTranscript' AND type = 3)
            OR (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'waitForCompletion') > 1
            OR NOT EXISTS (SELECT 1 FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'callDevices' AND type = 4)
            THROW 51000, 'Options require a positive historyId, boolean postRecordingTranscript, callDevices and optional boolean waitForCompletion.', 1;
        -- One or two devices in dialling order; Custom carries its own E.164 number, other types never do.
        IF (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON, '$.callDevices')) NOT BETWEEN 1 AND 2
            OR EXISTS
            (
                SELECT 1 FROM OPENJSON(@OptionsJSON, '$.callDevices') d
                WHERE d.type <> 5
                   OR (SELECT COUNT(*) FROM OPENJSON(d.value)) <> 2
                   OR NOT EXISTS (SELECT 1 FROM OPENJSON(d.value) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'type' AND type = 1
                        AND value COLLATE Latin1_General_100_BIN2 IN ('CellPhone', 'HomePhone', 'WorkPhone', 'Custom'))
                   OR NOT EXISTS (SELECT 1 FROM OPENJSON(d.value) p WHERE p.[key] COLLATE Latin1_General_100_BIN2 = 'phoneNumber'
                        AND ((JSON_VALUE(d.value, '$.type') = 'Custom' AND p.type = 1
                              AND DATALENGTH(p.value) BETWEEN 6 AND 32 AND LEFT(p.value, 1) = '+'
                              AND SUBSTRING(p.value, 2, 1) LIKE '[1-9]' AND SUBSTRING(p.value, 2, 15) NOT LIKE '%[^0-9]%')
                          OR (JSON_VALUE(d.value, '$.type') <> 'Custom' AND p.type = 0)))
            )
            THROW 51000, 'callDevices requires one or two devices with a known type; only Custom carries an E.164 phoneNumber.', 1;
    END;

    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @JobId int, @ExistingAlarm int, @ExistingOid varchar(20), @ExistingFlow int,
            @ExistingVariables nvarchar(max), @ExistingPOGroupNum varchar(20), @ExistingOptions nvarchar(max);
        SELECT @JobId = SystemID, @ExistingAlarm = ActiveAlarmID, @ExistingOid = OID,
            @ExistingFlow = FlowId, @ExistingVariables = VariablesJSON, @ExistingPOGroupNum = POGroupNum,
            @ExistingOptions = OptionsJSON
        FROM dbo.AutomatedCallQueue WITH (UPDLOCK, HOLDLOCK)
        WHERE EnqueueIdempotencyKey = @EnqueueIdempotencyKey;

        IF @JobId IS NOT NULL
        BEGIN
            IF @ExistingAlarm <> @ActiveAlarmID OR @ExistingFlow <> @FlowId
                OR @ExistingOid COLLATE Latin1_General_100_BIN2 <> @OID COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@ExistingOid) <> DATALENGTH(@OID)
                OR @ExistingVariables COLLATE Latin1_General_100_BIN2 <> @VariablesJSON COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@ExistingVariables) <> DATALENGTH(@VariablesJSON)
                OR (@ExistingPOGroupNum IS NULL AND @POGroupNum IS NOT NULL)
                OR (@ExistingPOGroupNum IS NOT NULL AND @POGroupNum IS NULL)
                OR @ExistingPOGroupNum COLLATE Latin1_General_100_BIN2 <> @POGroupNum COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@ExistingPOGroupNum) <> DATALENGTH(@POGroupNum)
                OR (@ExistingOptions IS NULL AND @OptionsJSON IS NOT NULL)
                OR (@ExistingOptions IS NOT NULL AND @OptionsJSON IS NULL)
                OR JSON_VALUE(@ExistingOptions, '$.historyId') <> JSON_VALUE(@OptionsJSON, '$.historyId')
                OR JSON_VALUE(@ExistingOptions, '$.postRecordingTranscript') <> JSON_VALUE(@OptionsJSON, '$.postRecordingTranscript')
                OR ISNULL(JSON_VALUE(@ExistingOptions, '$.waitForCompletion'), 'false') <> ISNULL(JSON_VALUE(@OptionsJSON, '$.waitForCompletion'), 'false')
                OR ISNULL(JSON_QUERY(@ExistingOptions, '$.callDevices'), N'') COLLATE Latin1_General_100_BIN2
                    <> ISNULL(JSON_QUERY(@OptionsJSON, '$.callDevices'), N'') COLLATE Latin1_General_100_BIN2
                THROW 51001, 'Enqueue key conflicts with the original request snapshot.', 1;
        END
        ELSE
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.CallFlowTemplates WHERE SystemID = @FlowId AND DeletedDateTime IS NULL)
                THROW 51000, 'An existing published call flow is required.', 1;
            INSERT dbo.AutomatedCallQueue
                (ActiveAlarmID, OID, FlowId, CallStatus, Priority, AvailableAt, MaxAttempts,
                 VariablesJSON, EnqueueIdempotencyKey, POGroupNum, OptionsJSON)
            VALUES
                (@ActiveAlarmID, @OID, @FlowId, @CallStatus, @Priority, COALESCE(@AvailableAt, SYSUTCDATETIME()), @MaxAttempts,
                 @VariablesJSON, @EnqueueIdempotencyKey, @POGroupNum, @OptionsJSON);
            SET @JobId = SCOPE_IDENTITY();
        END;
        COMMIT;
        SELECT @JobId AS SystemID;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
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
        inserted.EnqueueIdempotencyKey,inserted.AttemptCount,inserted.MaxAttempts,inserted.LeaseToken,inserted.LockedUntil,
        inserted.CallDeviceIndex,inserted.OptionsJSON;
END;
GO

-- The attempt records which device it dials, taken from the queue row's own device entry.
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
        DECLARE @Oid varchar(20),@FlowId int,@Until datetime2(3),@CallId int,@State varchar(20),@Sid varchar(200),@Error varchar(100),
            @DeviceIndex int,@Options nvarchar(max),@PhoneType varchar(20);
        SELECT @CallId=CurrentAttemptId FROM dbo.AutomatedCallQueue WHERE SystemID=@QueueId;
        SELECT @State=SubmissionState,@Sid=providerCallId,@Error=SubmissionErrorCode FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@CallId;
        SELECT @Oid=OID,@FlowId=FlowId,@Until=LockedUntil,@DeviceIndex=CallDeviceIndex,@Options=OptionsJSON FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@QueueId AND Status='PROCESSING' AND LeaseToken=@LeaseToken
            AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>SYSUTCDATETIME();
        IF @Until IS NULL THROW 51002,'Queue claim is not currently owned by this worker.',1;
        IF @CallId IS NOT NULL AND (@Sid IS NOT NULL OR @State IS NULL OR @State NOT IN('READY','REJECTED') OR (@State='REJECTED' AND (@Error IS NULL OR @Error<>'TWILIO_20429')))
            THROW 51001,'Existing submission requires reconciliation, not a new attempt.',1;
        IF @Options IS NOT NULL AND ISJSON(@Options)=1
            SELECT @PhoneType=JSON_VALUE(d.value,'$.type') FROM OPENJSON(JSON_QUERY(@Options,'$.callDevices')) d
            WHERE d.type=5 AND d.[key]=CONVERT(nvarchar(11),@DeviceIndex);
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
                INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,CallStatus,PhoneType,DeviceIndex)
                VALUES(@QueueId,@LeaseToken,'Twilio',@Oid,@PhoneE164,@FlowId,'queued',@PhoneType,CASE WHEN @PhoneType IS NOT NULL THEN @DeviceIndex END);
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

CREATE OR ALTER PROCEDURE dbo.automatedcalls_ClaimNote
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    -- Transcripts follow the completion note, which unsuccessful and machine-answered calls never get.
    UPDATE o SET Status = 'SKIPPED', LastErrorCode = 'CALL_NOT_COMPLETED'
    FROM dbo.AutomatedCallNoteOutbox o JOIN dbo.AutomatedCalls c ON c.SystemID = o.CallId
    WHERE o.Status = 'READY' AND o.Kind = 'TRANSCRIPT'
      AND (c.CallStatus IN ('failed','busy','no-answer','canceled') OR c.AnsweredBy LIKE 'machine[_]%' OR c.AnsweredBy = 'fax');
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
        inserted.QueueId, inserted.ActiveAlarmID;
END;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_Start
    @CallId int,
    @ProviderCallId varchar(200),
    @PhoneE164 varchar(16),
    @ExpectedTwiML nvarchar(max) = NULL,
    @StepId nvarchar(200) = NULL,
    @ExecutionId uniqueidentifier = NULL,
    @ResponseTwiML nvarchar(max) = NULL,
    @AnsweredBy varchar(30) = NULL
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

        DECLARE @Found bit = 0,
                @Provider varchar(100),
                @ExistingCallId varchar(200),
                @ExistingPhone varchar(16),
                @CallStatus varchar(20),
                @EndedDateTime datetime2(3),
                @TwiML nvarchar(max),
                @EventJSON nvarchar(max),
                @EventFound bit = 0,
                @Replayed bit = 0,
                @Outcome varchar(30) = 'started';

        SELECT @Found = 1,
               @Provider = Provider,
               @ExistingCallId = providerCallId,
               @ExistingPhone = PhoneE164,
               @TwiML = TwiML,
               @CallStatus = CallStatus,
               @EndedDateTime = EndedDateTime
        FROM dbo.AutomatedCalls WITH (UPDLOCK, HOLDLOCK)
        WHERE SystemID = @CallId;

        IF @Found = 0
            SET @Outcome = 'not-found';
        ELSE IF @Provider IS NULL OR LOWER(@Provider) <> 'twilio'
            SET @Outcome = 'provider-mismatch';
        ELSE IF @ExistingPhone IS NULL OR @ExistingPhone <> @PhoneE164
            SET @Outcome = 'destination-mismatch';
        ELSE IF @ExistingCallId IS NOT NULL
             AND @ExistingCallId COLLATE Latin1_General_100_BIN2 <> @ProviderCallId COLLATE Latin1_General_100_BIN2
            SET @Outcome = 'call-sid-mismatch';

        IF @Outcome = 'started'
        BEGIN
            SELECT @EventFound = 1, @EventJSON = EventJSON
            FROM dbo.AutomatedCallEvents
            WHERE CallId = @CallId AND IdempotencyKey = 'voice:start';

            IF @EventFound = 1
            BEGIN
                IF @EventJSON IS NULL OR ISJSON(@EventJSON) <> 1
                    SET @Outcome = 'invalid-twiml';
                ELSE
                BEGIN
                    SET @StepId = NULL;
                    SET @ExecutionId = NULL;
                    SET @ResponseTwiML = NULL;
                    SELECT @StepId = stepId,
                           @ExecutionId = TRY_CONVERT(uniqueidentifier, executionId),
                           @ResponseTwiML = responseTwiML
                    FROM OPENJSON(@EventJSON)
                    WITH
                    (
                        stepId nvarchar(200),
                        executionId nvarchar(36),
                        responseTwiML nvarchar(max)
                    );
                    IF @ExecutionId IS NULL OR NULLIF(@StepId, '') IS NULL
                              OR NULLIF(LTRIM(RTRIM(@ResponseTwiML)), '') IS NULL
                        SET @Outcome = 'invalid-twiml';
                    ELSE
                        SET @Replayed = 1;
                END;
            END
            ELSE
            BEGIN
                IF @EndedDateTime IS NOT NULL OR @CallStatus IN ('completed', 'busy', 'failed', 'no-answer', 'canceled')
                    SET @Outcome = 'call-ended';
                ELSE IF @ExecutionId IS NULL OR NULLIF(LTRIM(RTRIM(@StepId)), '') IS NULL
                   OR NULLIF(LTRIM(RTRIM(@ResponseTwiML)), '') IS NULL
                   OR @ExpectedTwiML IS NULL OR @TwiML IS NULL
                    SET @Outcome = 'invalid-twiml';
                ELSE IF DATALENGTH(@TwiML) <> DATALENGTH(@ExpectedTwiML)
                     OR @TwiML COLLATE Latin1_General_100_BIN2 <> @ExpectedTwiML COLLATE Latin1_General_100_BIN2
                    SET @Outcome = 'bundle-changed';
            END;
        END;

        IF @Outcome = 'started' AND @Replayed = 0
        BEGIN
            UPDATE dbo.AutomatedCalls
            SET providerCallId = COALESCE(providerCallId, @ProviderCallId),
                CallStatus = CASE
                    WHEN CallStatus IS NULL OR CallStatus IN ('queued', 'initiated', 'ringing')
                    THEN 'in-progress'
                    ELSE CallStatus
                END,
                AnsweredBy = COALESCE(AnsweredBy, NULLIF(LTRIM(RTRIM(@AnsweredBy)), '')),
                StartedDateTime = COALESCE(StartedDateTime, SYSUTCDATETIME()),
                UpdatedDateTime = SYSUTCDATETIME()
            WHERE SystemID = @CallId;

            SET @EventJSON = (
                SELECT @StepId AS stepId,
                       CONVERT(nvarchar(36), @ExecutionId) AS executionId,
                       @ProviderCallId AS providerCallId,
                       @ResponseTwiML AS responseTwiML,
                       NULLIF(LTRIM(RTRIM(@AnsweredBy)), '') AS answeredBy
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            );

            INSERT INTO dbo.AutomatedCallEvents
                (CallId, CallEventDateTime, EventType, EventJSON, IdempotencyKey)
            VALUES
                (@CallId, SYSUTCDATETIME(), 'StepIssued', @EventJSON, 'voice:start');
        END;

        COMMIT TRANSACTION;

        SELECT @Outcome AS Outcome,
             CASE WHEN @Outcome = 'started' THEN @ResponseTwiML END AS ResponseTwiML,
             CASE WHEN @Outcome = 'started' THEN @StepId END AS StepId,
             CASE WHEN @Outcome = 'started' THEN @ExecutionId END AS ExecutionId,
             @Replayed AS Replayed;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
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
                -- Answering-machine detection reports AnsweredBy on the status callbacks too.
                UPDATE dbo.AutomatedCalls SET providerCallId=COALESCE(providerCallId,@ProviderCallId),CallStatus=@CallStatus,
                    LastStatusSequenceNumber=@SequenceNumber,LastStatusDateTime=@Timestamp,UpdatedDateTime=@Now,
                    AnsweredBy=COALESCE(AnsweredBy,NULLIF(LTRIM(RTRIM(LEFT(JSON_VALUE(@ParametersJSON,'$.AnsweredBy'),30))),'')),
                    EndedDateTime=CASE WHEN @Terminal=1 THEN @Timestamp ELSE EndedDateTime END,
                    CallDurationSeconds=CASE WHEN @Terminal=1 THEN @CallDurationSeconds ELSE CallDurationSeconds END WHERE SystemID=@CallId;
                IF @CallStatus='completed' EXEC dbo.automatedcalls_QueueCompletionNote @CallId;
                IF @Terminal=1 AND @QueueId IS NOT NULL EXEC dbo.automatedcalls_ApplyCallOutcome @CallId;
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
        -- Giving up on recovery is a system failure: release the wait without trying another device.
        IF @Terminal=1 EXEC dbo.automatedcalls_ApplyCallOutcome @CallId;
        ELSE IF @Exhausted=1 EXEC dbo.automatedcalls_QueueWaitRelease @QueueId;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Ownership expired during observation.',1;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO

CREATE OR ALTER PROC [dbo].[automatedcalls_GetAllCallForPOGroup]
    @pogroupnum varchar(20),
    @startdate datetime,
    @enddate datetime
AS
BEGIN
    SET NOCOUNT ON;

    IF NULLIF(LTRIM(RTRIM(@pogroupnum)), '') IS NULL
        THROW 51000, 'POGroupNum is required.', 1;
    IF @startdate IS NULL OR @enddate IS NULL OR @startdate >= @enddate
        THROW 51000, 'A UTC startdate earlier than enddate is required.', 1;

    SELECT
        c.SystemID AS CallId,
        q.SystemID AS AutomatedCallQueueId,
        q.ActiveAlarmID,
        q.POGroupNum,
        COALESCE(c.OID, q.OID) AS OID,
        c.PhoneE164,
        COALESCE(c.FlowId, q.FlowId) AS FlowId,
        c.Provider,
        c.providerCallId AS ProviderCallId,
        c.CallStatus,
        c.SubmissionState,
        c.SubmissionErrorCode,
        q.Status AS QueueStatus,
        c.CreatedDateTime,
        c.StartedDateTime,
        c.EndedDateTime,
        c.UpdatedDateTime,
        c.CallDurationSeconds,
        COALESCE((
            SELECT
                r.SystemID AS RecordingId,
                r.Provider,
                r.RecordingSid,
                r.RecordingStatus,
                r.DurationSeconds,
                r.Channels,
                r.RecordingStartTime,
                r.CreatedDateTime,
                r.UpdatedDateTime
            FROM dbo.AutomatedCallRecordings AS r
            WHERE r.CallId = c.SystemID
            ORDER BY r.SystemID
            FOR JSON PATH, INCLUDE_NULL_VALUES
        ), N'[]') AS RecordingsJSON,
        COALESCE((
            SELECT
                j.SystemID AS TranscriptionJobId,
                j.RecordingId,
                j.Provider,
                j.ConfigurationVersion,
                j.Status AS TranscriptionStatus,
                j.AttemptCount,
                j.MaxAttempts,
                j.CompletedSubmissionId,
                j.CompletedDateTime,
                j.LastErrorCode,
                j.CreatedDateTime,
                j.UpdatedDateTime,
                latest.SubmissionId,
                latest.SubmissionStatus,
                latest.AttemptNumber AS SubmissionAttemptNumber,
                latest.SubmittedDateTime,
                latest.CallbackReceivedDateTime,
                latest.LastErrorCode AS SubmissionErrorCode,
                -- Message timestamps are offsets from the start of the recording.
                JSON_QUERY(COALESCE((
                    SELECT
                        t.Sentence,
                        t.AudioChannel,
                        t.StartTimeMilliseconds,
                        t.EndTimeMilliseconds,
                        t.TranscriptText
                    FROM dbo.AutomatedCallTranscripts AS t
                    WHERE t.SubmissionId = j.CompletedSubmissionId
                    ORDER BY t.Sentence, t.AudioChannel
                    FOR JSON PATH, INCLUDE_NULL_VALUES
                ), N'[]')) AS Messages
            FROM dbo.AutomatedCallTranscriptionJobs AS j
            INNER JOIN dbo.AutomatedCallRecordings AS r ON r.SystemID = j.RecordingId
            OUTER APPLY
            (
                SELECT TOP (1)
                    s.SystemID AS SubmissionId,
                    s.Status AS SubmissionStatus,
                    s.AttemptNumber,
                    s.SubmittedDateTime,
                    s.CallbackReceivedDateTime,
                    s.LastErrorCode
                FROM dbo.AutomatedCallTranscriptionSubmissions AS s
                WHERE s.TranscriptionJobId = j.SystemID AND s.RecordingId = j.RecordingId
                ORDER BY s.AttemptNumber DESC, s.SystemID DESC
            ) AS latest
            WHERE r.CallId = c.SystemID
            ORDER BY j.RecordingId, j.ConfigurationVersion, j.SystemID
            FOR JSON PATH, INCLUDE_NULL_VALUES
        ), N'[]') AS TranscriptionsJSON,
        q.CreatedDateTime AS QueueCreatedDateTime,
        q.UpdatedAt AS QueueUpdatedAt,
        q.AvailableAt AS QueueAvailableAt,
        q.AttemptCount AS QueueAttemptCount,
        q.MaxAttempts AS QueueMaxAttempts,
        q.LastErrorCode AS QueueLastErrorCode,
        -- A fallback device gets its own queue row; RootQueueId points at the row the workflow enqueued.
        COALESCE(c.PhoneType, (SELECT JSON_VALUE(d.value, '$.type')
            FROM OPENJSON(JSON_QUERY(CASE WHEN ISJSON(q.OptionsJSON) = 1 THEN q.OptionsJSON END, '$.callDevices')) AS d
            WHERE d.type = 5 AND d.[key] = CONVERT(nvarchar(11), q.CallDeviceIndex))) AS PhoneType,
        c.AnsweredBy,
        q.CallDeviceIndex,
        q.RootQueueId
    FROM dbo.AutomatedCallQueue AS q
    OUTER APPLY
    (
        SELECT TOP (1)
            attempt.SystemID, attempt.OID, attempt.PhoneE164, attempt.FlowId,
            attempt.Provider, attempt.providerCallId, attempt.CallStatus,
            attempt.SubmissionState, attempt.SubmissionErrorCode,
            attempt.CreatedDateTime, attempt.StartedDateTime, attempt.EndedDateTime,
            attempt.UpdatedDateTime, attempt.CallDurationSeconds, attempt.PhoneType, attempt.AnsweredBy
        FROM dbo.AutomatedCalls AS attempt
        WHERE attempt.AutomatedCallQueueId = q.SystemID
        ORDER BY attempt.CreatedDateTime DESC, attempt.SystemID DESC
    ) AS c
    WHERE (q.POGroupNum = @pogroupnum OR q.POGroupNum LIKE @pogroupnum + '%')
      AND q.CreatedDateTime >= @startdate
      AND q.CreatedDateTime < @enddate
    ORDER BY q.CreatedDateTime DESC, q.SystemID DESC;
END;
GO
