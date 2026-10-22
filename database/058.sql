SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-- AutoCallDB: deploy after 044.sql and before the parsers/TenantNotes worker that use WaitForCallCompletion.
-- Pair with tenant 057.sql. Every terminal path of a waiting job queues one RELEASE task; the TenantNotes
-- worker delivers it to the tenant so the parser's next step runs as soon as the call ends.
IF OBJECT_ID(N'dbo.AutomatedCallNoteOutbox', N'U') IS NULL
    THROW 51000, 'Deploy 044.sql before the Robo Call wait release.', 1;
GO
IF COL_LENGTH(N'dbo.AutomatedCallNoteOutbox', N'QueueId') IS NULL
    ALTER TABLE dbo.AutomatedCallNoteOutbox ADD QueueId int NULL;
IF COL_LENGTH(N'dbo.AutomatedCallNoteOutbox', N'ActiveAlarmID') IS NULL
    ALTER TABLE dbo.AutomatedCallNoteOutbox ADD ActiveAlarmID int NULL;
GO
-- A preparation failure can end a job before any call row exists, so RELEASE rows may have no CallId.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND name = N'CallId' AND is_nullable = 0)
BEGIN
    DECLARE @Sql nvarchar(max) = N'';
    SELECT @Sql += N'ALTER TABLE dbo.AutomatedCallNoteOutbox DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
    FROM sys.foreign_keys fk
    JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
    JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
    WHERE fk.parent_object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND c.name = N'CallId';
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND name = N'UQ_AutoCallNote_Source')
        SET @Sql += N'ALTER TABLE dbo.AutomatedCallNoteOutbox DROP CONSTRAINT UQ_AutoCallNote_Source;';
    SET @Sql += N'ALTER TABLE dbo.AutomatedCallNoteOutbox ALTER COLUMN CallId int NULL;'
        + N'ALTER TABLE dbo.AutomatedCallNoteOutbox ADD CONSTRAINT FK_AutoCallNote_Call FOREIGN KEY (CallId) REFERENCES dbo.AutomatedCalls(SystemID);'
        + N'ALTER TABLE dbo.AutomatedCallNoteOutbox ADD CONSTRAINT UQ_AutoCallNote_Source UNIQUE (CallId, Kind, SourceId);';
    EXEC sys.sp_executesql @Sql;
END;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_AutoCallNote_Kind')
BEGIN
    DECLARE @Sql nvarchar(max) = N'';
    SELECT @Sql += N'ALTER TABLE dbo.AutomatedCallNoteOutbox DROP CONSTRAINT ' + QUOTENAME(cc.name) + N';'
    FROM sys.check_constraints cc
    JOIN sys.columns c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
    WHERE cc.parent_object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND c.name = N'Kind';
    SET @Sql += N'ALTER TABLE dbo.AutomatedCallNoteOutbox ADD CONSTRAINT CK_AutoCallNote_Kind CHECK (Kind IN (''COMPLETED'', ''TRANSCRIPT'', ''RELEASE''));';
    EXEC sys.sp_executesql @Sql;
END;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_AutoCallNote_Target')
    ALTER TABLE dbo.AutomatedCallNoteOutbox ADD CONSTRAINT CK_AutoCallNote_Target CHECK
        ((Kind = 'RELEASE' AND QueueId > 0 AND ActiveAlarmID > 0 AND SourceId = QueueId)
         OR (Kind <> 'RELEASE' AND CallId IS NOT NULL));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallNoteOutbox') AND name = N'UX_AutoCallNote_Release')
    CREATE UNIQUE INDEX UX_AutoCallNote_Release ON dbo.AutomatedCallNoteOutbox(QueueId) WHERE Kind = 'RELEASE';
GO

-- Called inside every transaction that makes a queue job terminal. Only jobs enqueued with
-- waitForCompletion=true get a task, and each job gets at most one.
CREATE OR ALTER PROCEDURE dbo.automatedcalls_QueueWaitRelease
    @QueueId int
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.AutomatedCallNoteOutbox (CallId, Kind, SourceId, OID, HistoryID, OccurredAt, QueueId, ActiveAlarmID)
    SELECT q.CurrentAttemptId, 'RELEASE', q.SystemID, q.OID, CONVERT(int, JSON_VALUE(q.OptionsJSON, '$.historyId')),
        SYSUTCDATETIME(), q.SystemID, q.ActiveAlarmID
    FROM dbo.AutomatedCallQueue q
    WHERE q.SystemID = @QueueId AND q.OID IS NOT NULL AND q.ActiveAlarmID > 0
      AND JSON_VALUE(q.OptionsJSON, '$.waitForCompletion') = 'true'
      AND TRY_CONVERT(int, JSON_VALUE(q.OptionsJSON, '$.historyId')) > 0
      AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox o WITH (UPDLOCK, HOLDLOCK) WHERE o.Kind = 'RELEASE' AND o.QueueId = q.SystemID);
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
        IF (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON)) - (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'waitForCompletion' AND type = 3) <> 2
            OR NOT EXISTS (SELECT 1 FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'historyId' AND type = 2 AND TRY_CONVERT(int, value) > 0)
            OR NOT EXISTS (SELECT 1 FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'postRecordingTranscript' AND type = 3)
            OR (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'waitForCompletion') > 1
            THROW 51000, 'Options require a positive historyId, boolean postRecordingTranscript and optional boolean waitForCompletion.', 1;
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

CREATE OR ALTER PROCEDURE dbo.automatedcalls_ClaimNote
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    UPDATE o SET Status = 'SKIPPED', LastErrorCode = 'CALL_NOT_COMPLETED'
    FROM dbo.AutomatedCallNoteOutbox o JOIN dbo.AutomatedCalls c ON c.SystemID = o.CallId
    WHERE o.Status = 'READY' AND o.Kind = 'TRANSCRIPT' AND c.CallStatus IN ('failed','busy','no-answer','canceled');
    UPDATE dbo.AutomatedCallNoteOutbox SET Status = 'DEAD_LETTER', LastErrorCode = 'DELIVERY_ATTEMPTS_EXHAUSTED'
    WHERE AttemptCount >= 10 AND (Status = 'READY' OR (Status = 'PROCESSING' AND LockedUntil <= @Now));
    ;WITH NextNote AS
    (
        SELECT TOP (1) o.* FROM dbo.AutomatedCallNoteOutbox o WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE o.AttemptCount < 10 AND
            ((o.Status = 'READY' AND o.AvailableAt <= @Now) OR (o.Status = 'PROCESSING' AND o.LockedUntil <= @Now))
          AND (o.Kind IN ('COMPLETED', 'RELEASE') OR
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
                IF @Terminal=1 AND @QueueId IS NOT NULL EXEC dbo.automatedcalls_QueueWaitRelease @QueueId;
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
        IF @Terminal=1 OR @Exhausted=1 EXEC dbo.automatedcalls_QueueWaitRelease @QueueId;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Ownership expired during observation.',1;
        COMMIT;
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
        IF @Disposition='DEAD_LETTER' EXEC dbo.automatedcalls_QueueWaitRelease @QueueId;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Ownership expired during disposition.',1;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_FailPreparation
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Owned int,@Until datetime2(3);
        SELECT @Owned=SystemID,@Until=LockedUntil FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@QueueId AND Status='PROCESSING' AND LeaseToken=@LeaseToken
            AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>SYSUTCDATETIME();
        IF @Owned IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken AND
            (providerCallId IS NOT NULL OR (TwiML IS NOT NULL AND Parameters IS NOT NULL)))
        BEGIN
            UPDATE dbo.AutomatedCalls SET CallStatus='failed',UpdatedDateTime=SYSUTCDATETIME()
            WHERE AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken AND providerCallId IS NULL;
            UPDATE dbo.AutomatedCallQueue SET Status='FAILED',FailedAt=SYSUTCDATETIME(),UpdatedAt=SYSUTCDATETIME(),
                LastErrorCode='PREPARATION_FAILED',LastErrorMessage=N'Attempt preparation failed.',LockedBy=NULL,LockedAt=NULL,LockedUntil=NULL,LeaseToken=NULL
            WHERE SystemID=@QueueId;
            EXEC dbo.automatedcalls_QueueWaitRelease @QueueId;
            IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue claim expired during failure disposition.',1;
        END;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
