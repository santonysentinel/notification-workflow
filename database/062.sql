SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-- AutoCallDB: call flow versions move out of CallFlowTemplates into CallFlowTemplateVersions.
-- Deploy after 061.sql, then the worker. The feedback API needs no change.
--
-- CallFlowTemplates is now one row per flow (FlowID family). Its SystemID is the stable FlowId that
-- workflow profiles, queue rows, calls and CallFlowTemplatePOGroups reference.
-- CallFlowTemplateVersions holds each published revision. The active version is the template's only
-- version with DeletedDateTime IS NULL; superseded versions are soft-deleted.
-- Queue rows and calls pin the version they were enqueued with (FlowVersionId), so a call in progress
-- keeps running its version after a newer one is published.
--
-- Existing data is converted in place, once, when CallFlowTemplates still has its TemplateJSON column:
-- each FlowID family keeps one template row (the family's non-deleted row, else its newest row), every
-- old row becomes a version of it, and queue rows, calls and PO group associations are re-pointed.
-- This script can be re-run. After it, do not re-run 001-061 scripts that define procedures reading
-- CallFlowTemplates.TemplateJSON/TwiML/Version (for example 060.sql, 061.sql, 024.sql, 025.sql, 031.sql, 032.sql).
IF COL_LENGTH(N'dbo.AutomatedCalls', N'AnsweredBy') IS NULL
    THROW 51000, 'Deploy 060.sql and 061.sql before call flow versions.', 1;
GO

IF OBJECT_ID(N'dbo.CallFlowTemplateVersions', N'U') IS NULL
    CREATE TABLE dbo.CallFlowTemplateVersions
    (
        SystemID int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_CallFlowTemplateVersions PRIMARY KEY,
        Version int NULL,
        TemplateJSON nvarchar(max) NULL,
        TwiML nvarchar(max) NULL,
        CallFlowTemplateID int NOT NULL
            CONSTRAINT FK_CallFlowTemplateVersions_Template REFERENCES dbo.CallFlowTemplates (SystemID),
        CreatedDateTime datetime2(3) NOT NULL CONSTRAINT DF_CallFlowTemplateVersions_Created DEFAULT (SYSUTCDATETIME()),
        UpdatedDateTime datetime2(3) NULL,
        DeletedDateTime datetime2(3) NULL,
        CreatedBy nvarchar(200) NOT NULL,
        UpdatedBy nvarchar(200) NULL,
        DeletedBy nvarchar(200) NULL
    );
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CallFlowTemplateVersions') AND name = N'UX_CallFlowTemplateVersions_Version')
    CREATE UNIQUE INDEX UX_CallFlowTemplateVersions_Version ON dbo.CallFlowTemplateVersions (CallFlowTemplateID, Version) WHERE Version IS NOT NULL;
-- Publication requires an unfiltered unique template/version key.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CallFlowTemplateVersions') AND name = N'UX_CallFlowTemplateVersions_Template_Version')
    CREATE UNIQUE INDEX UX_CallFlowTemplateVersions_Template_Version ON dbo.CallFlowTemplateVersions (CallFlowTemplateID, Version);
-- At most one active version per template.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CallFlowTemplateVersions') AND name = N'UX_CallFlowTemplateVersions_Active')
    CREATE UNIQUE INDEX UX_CallFlowTemplateVersions_Active ON dbo.CallFlowTemplateVersions (CallFlowTemplateID) WHERE DeletedDateTime IS NULL;
-- Lets queue rows and calls reference (FlowId, FlowVersionId), so a pinned version always belongs to its flow.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CallFlowTemplateVersions') AND name = N'UX_CallFlowTemplateVersions_Template')
    CREATE UNIQUE INDEX UX_CallFlowTemplateVersions_Template ON dbo.CallFlowTemplateVersions (CallFlowTemplateID, SystemID);
GO
IF COL_LENGTH(N'dbo.AutomatedCallQueue', N'FlowVersionId') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD FlowVersionId int NULL;
IF COL_LENGTH(N'dbo.AutomatedCalls', N'FlowVersionId') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD FlowVersionId int NULL;
GO

IF COL_LENGTH(N'dbo.CallFlowTemplates', N'TemplateJSON') IS NOT NULL
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        IF EXISTS (SELECT 1 FROM dbo.CallFlowTemplateVersions)
            THROW 51000, 'CallFlowTemplateVersions already has rows; CallFlowTemplates cannot be converted again.', 1;

        CREATE TABLE #FlowRows
        (
            OldId int NOT NULL PRIMARY KEY, TemplateId int NOT NULL, Version int NOT NULL,
            DeletedDateTime datetime2(3) NULL, DeletedBy nvarchar(200) NULL
        );
        CREATE TABLE #VersionIds (OldId int NOT NULL PRIMARY KEY, VersionId int NOT NULL);

        -- Rows without a FlowID are their own family. Version numbers are kept when they are complete and
        -- distinct within the family, otherwise renumbered in publication order.
        EXEC sys.sp_executesql N'
            WITH Rows AS
            (
                SELECT SystemID, Version, DeletedDateTime, DeletedBy,
                    COALESCE(CONVERT(nvarchar(36), FlowID), N''#'' + CONVERT(nvarchar(11), SystemID)) AS Family
                FROM dbo.CallFlowTemplates WITH (TABLOCKX, HOLDLOCK)
            ),
            Families AS
            (
                SELECT Family,
                    COALESCE(MAX(CASE WHEN DeletedDateTime IS NULL THEN SystemID END), MAX(SystemID)) AS TemplateId,
                    CASE WHEN COUNT(*) = COUNT(DISTINCT Version) THEN 1 ELSE 0 END AS KeepNumbers
                FROM Rows GROUP BY Family
            )
            INSERT #FlowRows (OldId, TemplateId, Version, DeletedDateTime, DeletedBy)
            SELECT r.SystemID, f.TemplateId,
                CASE WHEN f.KeepNumbers = 1 THEN r.Version
                     ELSE ROW_NUMBER() OVER (PARTITION BY r.Family ORDER BY ISNULL(r.Version, 0), r.SystemID) END,
                CASE WHEN r.SystemID = f.TemplateId THEN r.DeletedDateTime ELSE COALESCE(r.DeletedDateTime, SYSUTCDATETIME()) END,
                CASE WHEN r.SystemID = f.TemplateId OR r.DeletedDateTime IS NOT NULL THEN r.DeletedBy ELSE N''migration-062'' END
            FROM Rows r JOIN Families f ON f.Family = r.Family;';

        EXEC sys.sp_executesql N'
            MERGE dbo.CallFlowTemplateVersions AS target
            USING
            (
                SELECT m.OldId, m.TemplateId, m.Version, m.DeletedDateTime, m.DeletedBy, c.TemplateJSON, c.TwiML,
                    c.CreatedDateTime, c.CreatedBy, c.UpdatedDateTime, c.UpdatedBy
                FROM #FlowRows m JOIN dbo.CallFlowTemplates c ON c.SystemID = m.OldId
            ) AS source
            ON 1 = 0
            WHEN NOT MATCHED THEN
                INSERT (CallFlowTemplateID, Version, TemplateJSON, TwiML, CreatedDateTime, CreatedBy, UpdatedDateTime, UpdatedBy, DeletedDateTime, DeletedBy)
                VALUES (source.TemplateId, source.Version, source.TemplateJSON, source.TwiML, source.CreatedDateTime,
                    COALESCE(NULLIF(source.CreatedBy, N''''), N''migration-062''), source.UpdatedDateTime, source.UpdatedBy,
                    source.DeletedDateTime, source.DeletedBy)
            OUTPUT source.OldId, inserted.SystemID INTO #VersionIds (OldId, VersionId);';

        UPDATE q SET FlowId = m.TemplateId, FlowVersionId = i.VersionId
        FROM dbo.AutomatedCallQueue q JOIN #FlowRows m ON m.OldId = q.FlowId JOIN #VersionIds i ON i.OldId = m.OldId;
        UPDATE c SET FlowId = m.TemplateId, FlowVersionId = i.VersionId
        FROM dbo.AutomatedCalls c JOIN #FlowRows m ON m.OldId = c.FlowId JOIN #VersionIds i ON i.OldId = m.OldId;

        -- PO group associations follow their template; two active associations that now name the same
        -- template collapse into the older one.
        IF OBJECT_ID(N'dbo.CallFlowTemplatePOGroups', N'U') IS NOT NULL
            EXEC sys.sp_executesql N'
                WITH Ranked AS
                (
                    SELECT p.SystemID, ROW_NUMBER() OVER (PARTITION BY p.POGroupNum, COALESCE(m.TemplateId, p.FlowID) ORDER BY p.SystemID) AS Position
                    FROM dbo.CallFlowTemplatePOGroups p LEFT JOIN #FlowRows m ON m.OldId = p.FlowID
                    WHERE p.DeletedDate IS NULL
                )
                UPDATE p SET DeletedDate = GETUTCDATE()
                FROM dbo.CallFlowTemplatePOGroups p JOIN Ranked r ON r.SystemID = p.SystemID WHERE r.Position > 1;
                UPDATE p SET FlowID = m.TemplateId
                FROM dbo.CallFlowTemplatePOGroups p JOIN #FlowRows m ON m.OldId = p.FlowID WHERE m.OldId <> m.TemplateId;';

        DELETE c FROM dbo.CallFlowTemplates c JOIN #FlowRows m ON m.OldId = c.SystemID WHERE m.OldId <> m.TemplateId;

        DECLARE @Drop nvarchar(max) = N'';
        SELECT @Drop = @Drop + N'DROP INDEX ' + QUOTENAME(i.name) + N' ON dbo.CallFlowTemplates;'
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.CallFlowTemplates') AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
          AND (i.filter_definition LIKE N'%Version%' OR i.filter_definition LIKE N'%TemplateJSON%' OR i.filter_definition LIKE N'%TwiML%'
               OR EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND col.name IN (N'Version', N'TemplateJSON', N'TwiML')));
        SELECT @Drop = @Drop + N'ALTER TABLE dbo.CallFlowTemplates DROP CONSTRAINT ' + QUOTENAME(d.name) + N';'
        FROM sys.default_constraints d
        JOIN sys.columns col ON col.object_id = d.parent_object_id AND col.column_id = d.parent_column_id
        WHERE d.parent_object_id = OBJECT_ID(N'dbo.CallFlowTemplates') AND col.name IN (N'Version', N'TemplateJSON', N'TwiML');
        SET @Drop = @Drop + N'ALTER TABLE dbo.CallFlowTemplates DROP COLUMN Version, TemplateJSON, TwiML;';
        EXEC sys.sp_executesql @Drop;

        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CallFlowTemplates') AND name = N'UX_CallFlowTemplates_FlowID')
    CREATE UNIQUE INDEX UX_CallFlowTemplates_FlowID ON dbo.CallFlowTemplates (FlowID) WHERE FlowID IS NOT NULL;
IF OBJECT_ID(N'dbo.FK_AutomatedCallQueue_FlowVersion', N'F') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue WITH CHECK ADD CONSTRAINT FK_AutomatedCallQueue_FlowVersion
        FOREIGN KEY (FlowId, FlowVersionId) REFERENCES dbo.CallFlowTemplateVersions (CallFlowTemplateID, SystemID);
IF OBJECT_ID(N'dbo.FK_AutomatedCalls_FlowVersion', N'F') IS NULL
    ALTER TABLE dbo.AutomatedCalls WITH CHECK ADD CONSTRAINT FK_AutomatedCalls_FlowVersion
        FOREIGN KEY (FlowId, FlowVersionId) REFERENCES dbo.CallFlowTemplateVersions (CallFlowTemplateID, SystemID);
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
    @OptionsJSON nvarchar(max) = NULL,
    @FlowVersionId int = NULL
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
        OR @Priority IS NULL OR @EnqueueIdempotencyKey IS NULL OR @FlowVersionId <= 0
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
            -- The caller pins the version it validated; without one, the job pins the active version.
            -- A version superseded since the caller read it is still accepted.
            IF @FlowVersionId IS NULL
                SELECT @FlowVersionId = v.SystemID
                FROM dbo.CallFlowTemplates t
                JOIN dbo.CallFlowTemplateVersions v ON v.CallFlowTemplateID = t.SystemID AND v.DeletedDateTime IS NULL
                WHERE t.SystemID = @FlowId AND t.DeletedDateTime IS NULL;
            IF @FlowVersionId IS NULL OR NOT EXISTS
            (
                SELECT 1 FROM dbo.CallFlowTemplates t
                JOIN dbo.CallFlowTemplateVersions v ON v.CallFlowTemplateID = t.SystemID
                WHERE t.SystemID = @FlowId AND t.DeletedDateTime IS NULL AND v.SystemID = @FlowVersionId
                  AND v.TemplateJSON IS NOT NULL AND v.TwiML IS NOT NULL
            )
                THROW 51000, 'An existing published call flow is required.', 1;
            INSERT dbo.AutomatedCallQueue
                (ActiveAlarmID, OID, FlowId, CallStatus, Priority, AvailableAt, MaxAttempts,
                 VariablesJSON, EnqueueIdempotencyKey, POGroupNum, OptionsJSON, FlowVersionId)
            VALUES
                (@ActiveAlarmID, @OID, @FlowId, @CallStatus, @Priority, COALESCE(@AvailableAt, SYSUTCDATETIME()), @MaxAttempts,
                 @VariablesJSON, @EnqueueIdempotencyKey, @POGroupNum, @OptionsJSON, @FlowVersionId);
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

-- A follow-on device row dials the same flow version as the row it follows.
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
             POGroupNum,OptionsJSON,CallDeviceIndex,RootQueueId,FlowVersionId)
        SELECT ActiveAlarmID,OID,FlowId,CallStatus,Priority,SYSUTCDATETIME(),MaxAttempts,VariablesJSON,NEWID(),
            POGroupNum,OptionsJSON,@Index+1,@Root,FlowVersionId
        FROM dbo.AutomatedCallQueue WHERE SystemID=@QueueId;
END;
GO

-- The attempt runs the version its queue row pinned, even if a newer version has been published since.
-- A deleted template (flow retired) can no longer start attempts.
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
            @DeviceIndex int,@Options nvarchar(max),@PhoneType varchar(20),@FlowVersionId int;
        SELECT @CallId=CurrentAttemptId FROM dbo.AutomatedCallQueue WHERE SystemID=@QueueId;
        SELECT @State=SubmissionState,@Sid=providerCallId,@Error=SubmissionErrorCode FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@CallId;
        SELECT @Oid=OID,@FlowId=FlowId,@FlowVersionId=FlowVersionId,@Until=LockedUntil,@DeviceIndex=CallDeviceIndex,@Options=OptionsJSON FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK)
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
                IF NOT EXISTS(SELECT 1 FROM dbo.CallFlowTemplates t JOIN dbo.CallFlowTemplateVersions v ON v.CallFlowTemplateID=t.SystemID
                    WHERE t.SystemID=@FlowId AND t.DeletedDateTime IS NULL AND v.SystemID=@FlowVersionId AND v.TemplateJSON IS NOT NULL AND v.TwiML IS NOT NULL)
                    THROW 51000,'Published flow data is required for preparation.',1;
                INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,CallStatus,PhoneType,DeviceIndex,FlowVersionId)
                VALUES(@QueueId,@LeaseToken,'Twilio',@Oid,@PhoneE164,@FlowId,'queued',@PhoneType,CASE WHEN @PhoneType IS NOT NULL THEN @DeviceIndex END,@FlowVersionId);
                SET @CallId=SCOPE_IDENTITY();
            END;
        END;
        IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@CallId AND AutomatedCallQueueId=@QueueId AND Provider='Twilio' AND OID=@Oid AND FlowId=@FlowId AND FlowVersionId=@FlowVersionId AND PhoneE164 IS NOT NULL)
            THROW 51001,'Existing attempt identity conflicts with the claim.',1;
        UPDATE dbo.AutomatedCallQueue SET CurrentAttemptId=@CallId WHERE SystemID=@QueueId;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue claim expired during preparation.',1;
        SELECT a.SystemID AS CallId,a.AutomatedCallQueueId AS QueueId,a.QueueLeaseToken,a.OID,a.PhoneE164,a.FlowId,
            a.TwiML,a.Parameters,t.TemplateJSON AS TemplateJson,t.TwiML AS TemplateTwiML,q.VariablesJSON AS VariablesJson
        FROM dbo.AutomatedCalls a JOIN dbo.AutomatedCallQueue q ON q.SystemID=a.AutomatedCallQueueId
        JOIN dbo.CallFlowTemplateVersions t ON t.SystemID=a.FlowVersionId WHERE a.SystemID=@CallId;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
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
    LEFT JOIN dbo.CallFlowTemplateVersions t ON t.SystemID=a.FlowVersionId
    WHERE q.SystemID=@QueueId AND q.LeaseToken=@LeaseToken;
END;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetNextContext
    @CallId int,
    @ExecutionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT calls.TwiML, templates.TemplateJSON, source.EventJSON AS SourceEventJSON
    FROM dbo.AutomatedCalls AS calls
    LEFT JOIN dbo.CallFlowTemplateVersions AS templates ON templates.SystemID = calls.FlowVersionId
    OUTER APPLY
    (
        SELECT TOP (1) events.EventJSON
        FROM dbo.AutomatedCallEvents AS events
        WHERE events.CallId = calls.SystemID AND events.EventType = 'StepIssued'
          AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(
              CASE WHEN ISJSON(events.EventJSON) = 1 THEN events.EventJSON ELSE N'{}' END,
              '$.executionId')) = @ExecutionId
        ORDER BY events.SystemID DESC
    ) AS source
    WHERE calls.SystemID = @CallId;
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
        LEFT JOIN dbo.CallFlowTemplateVersions AS templates WITH(HOLDLOCK) ON templates.SystemID=calls.FlowVersionId
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

-- Publishes a new version of a flow family. With @Activate = 1 (the default) the new version becomes the
-- active one and the previous active version is soft-deleted; with @Activate = 0 it is stored inactive
-- (soft-deleted) for a later AutoCall_ActivateFlowVersion. The first publication creates the template row.
CREATE OR ALTER PROCEDURE dbo.AutoCall_PublishFlow
    @FlowID uniqueidentifier,
    @ExpectedVersion int,
    @TemplateJSON nvarchar(max),
    @TwiML nvarchar(max),
    @CreatedBy nvarchar(200),
    @Name varchar(200) = NULL,
    @Platform varchar(20) = NULL,
    @POGroupNum varchar(20) = NULL,
    @Activate bit = 1
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET ANSI_NULLS ON;
    SET ANSI_PADDING ON;
    SET ANSI_WARNINGS ON;
    SET ARITHABORT ON;
    SET CONCAT_NULL_YIELDS_NULL ON;
    SET QUOTED_IDENTIFIER ON;
    SET NUMERIC_ROUNDABORT OFF;

    IF @FlowID IS NULL OR @FlowID = '00000000-0000-0000-0000-000000000000'
        OR @ExpectedVersion IS NULL OR @ExpectedVersion < 0
        OR @TemplateJSON IS NULL OR DATALENGTH(@TemplateJSON) = 0
        OR @TwiML IS NULL OR DATALENGTH(@TwiML) = 0
        OR NULLIF(LTRIM(RTRIM(@CreatedBy)), N'') IS NULL OR @Activate IS NULL
        THROW 51003, 'A family UUID, expected latest version, both artifacts and audit identity are required.', 1;

    IF OBJECT_ID(N'dbo.CallFlowTemplateVersions', N'U') IS NULL OR COL_LENGTH(N'dbo.CallFlowTemplates', N'TemplateJSON') IS NOT NULL
        THROW 51000, 'Migration 062 is required before publication.', 1;

    DECLARE @OwnTransaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;
    DECLARE @LockResource nvarchar(255) = N'AutoCallFlow:' + LOWER(CONVERT(nvarchar(36), @FlowID));
    DECLARE @LockResult int, @Latest int, @NextVersion int, @TemplateId int, @TemplateDeleted datetime2(3),
        @VersionId int, @Now datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        IF @OwnTransaction = 1
            BEGIN TRANSACTION;

        EXEC @LockResult = sys.sp_getapplock
            @Resource = @LockResource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @LockResult < 0
            THROW 51002, 'Publication lock could not be acquired.', 1;

        SELECT @TemplateId = SystemID, @TemplateDeleted = DeletedDateTime
        FROM dbo.CallFlowTemplates WITH (UPDLOCK, HOLDLOCK)
        WHERE FlowID = @FlowID;
        IF @TemplateDeleted IS NOT NULL
            THROW 51001, 'This flow has been deleted and cannot be published.', 1;

        SELECT @Latest = ISNULL(MAX(Version), 0)
        FROM dbo.CallFlowTemplateVersions WITH (UPDLOCK, HOLDLOCK)
        WHERE CallFlowTemplateID = @TemplateId;

        IF @Latest <> @ExpectedVersion
            THROW 51001, 'This flow has a newer published revision. Open the latest revision before publishing.', 1;
        IF @Latest = 2147483647
            THROW 51002, 'No further versions can be allocated.', 1;
        SET @NextVersion = @Latest + 1;

        IF @TemplateId IS NULL
        BEGIN
            INSERT dbo.CallFlowTemplates (FlowID, Name, POGroupNum, Platform, CreatedDateTime, CreatedBy)
            VALUES (@FlowID, @Name, @POGroupNum, @Platform, @Now, @CreatedBy);
            SET @TemplateId = SCOPE_IDENTITY();
        END
        ELSE
            UPDATE dbo.CallFlowTemplates
            SET Name = COALESCE(@Name, Name), POGroupNum = COALESCE(@POGroupNum, POGroupNum), Platform = COALESCE(@Platform, Platform),
                UpdatedDateTime = @Now, UpdatedBy = @CreatedBy
            WHERE SystemID = @TemplateId;

        IF @Activate = 1
            UPDATE dbo.CallFlowTemplateVersions SET DeletedDateTime = @Now, DeletedBy = @CreatedBy
            WHERE CallFlowTemplateID = @TemplateId AND DeletedDateTime IS NULL;

        INSERT dbo.CallFlowTemplateVersions (CallFlowTemplateID, Version, TemplateJSON, TwiML, CreatedDateTime, CreatedBy, DeletedDateTime, DeletedBy)
        VALUES (@TemplateId, @NextVersion, @TemplateJSON, @TwiML, @Now, @CreatedBy,
            CASE WHEN @Activate = 1 THEN NULL ELSE @Now END, CASE WHEN @Activate = 1 THEN NULL ELSE @CreatedBy END);
        SET @VersionId = SCOPE_IDENTITY();

        IF @OwnTransaction = 1
            COMMIT TRANSACTION;

        SELECT @FlowID AS FlowID, @TemplateId AS SystemID, @NextVersion AS Version, @VersionId AS VersionID, @Activate AS Active;
    END TRY
    BEGIN CATCH
        IF @OwnTransaction = 1 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

-- Makes an existing version the active one (rollback or switching), soft-deleting the current active version.
-- Calls already queued keep the version they pinned.
CREATE OR ALTER PROCEDURE dbo.AutoCall_ActivateFlowVersion
    @FlowID uniqueidentifier,
    @Version int,
    @ActivatedBy nvarchar(200)
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET ANSI_NULLS ON;
    SET ANSI_PADDING ON;
    SET ANSI_WARNINGS ON;
    SET ARITHABORT ON;
    SET CONCAT_NULL_YIELDS_NULL ON;
    SET QUOTED_IDENTIFIER ON;
    SET NUMERIC_ROUNDABORT OFF;

    IF @FlowID IS NULL OR @FlowID = '00000000-0000-0000-0000-000000000000' OR @Version IS NULL OR @Version <= 0
        OR NULLIF(LTRIM(RTRIM(@ActivatedBy)), N'') IS NULL
        THROW 51003, 'A family UUID, version and audit identity are required.', 1;

    DECLARE @OwnTransaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;
    DECLARE @LockResource nvarchar(255) = N'AutoCallFlow:' + LOWER(CONVERT(nvarchar(36), @FlowID));
    DECLARE @LockResult int, @TemplateId int, @TemplateDeleted datetime2(3), @VersionId int, @WasActive bit,
        @Complete bit, @Now datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        IF @OwnTransaction = 1
            BEGIN TRANSACTION;

        EXEC @LockResult = sys.sp_getapplock
            @Resource = @LockResource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @LockResult < 0
            THROW 51002, 'Publication lock could not be acquired.', 1;

        SELECT @TemplateId = SystemID, @TemplateDeleted = DeletedDateTime
        FROM dbo.CallFlowTemplates WITH (UPDLOCK, HOLDLOCK)
        WHERE FlowID = @FlowID;
        IF @TemplateId IS NULL
            THROW 51003, 'The flow does not exist.', 1;
        IF @TemplateDeleted IS NOT NULL
            THROW 51001, 'This flow has been deleted and cannot be activated.', 1;

        SELECT @VersionId = SystemID, @WasActive = CASE WHEN DeletedDateTime IS NULL THEN 1 ELSE 0 END,
            @Complete = CASE WHEN TemplateJSON IS NOT NULL AND TwiML IS NOT NULL THEN 1 ELSE 0 END
        FROM dbo.CallFlowTemplateVersions WITH (UPDLOCK, HOLDLOCK)
        WHERE CallFlowTemplateID = @TemplateId AND Version = @Version;
        IF @VersionId IS NULL
            THROW 51003, 'The flow version does not exist.', 1;
        IF @Complete = 0
            THROW 51003, 'The flow version has no graph or TwiML.', 1;

        IF @WasActive = 0
        BEGIN
            UPDATE dbo.CallFlowTemplateVersions SET DeletedDateTime = @Now, DeletedBy = @ActivatedBy
            WHERE CallFlowTemplateID = @TemplateId AND DeletedDateTime IS NULL;
            UPDATE dbo.CallFlowTemplateVersions SET DeletedDateTime = NULL, DeletedBy = NULL, UpdatedDateTime = @Now, UpdatedBy = @ActivatedBy
            WHERE SystemID = @VersionId;
        END;

        IF @OwnTransaction = 1
            COMMIT TRANSACTION;

        SELECT @FlowID AS FlowID, @TemplateId AS SystemID, @Version AS Version, @VersionId AS VersionID;
    END TRY
    BEGIN CATCH
        IF @OwnTransaction = 1 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

-- Catalog procedures list each non-deleted flow with its active version. Version, TemplateJSON, TwiML and the
-- audit columns describe the active version, as the per-version rows did before; VersionID is new.
CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetAgencyCallFlow (@pogroupnum varchar(20))
AS
BEGIN
    SET NOCOUNT ON;
    IF @pogroupnum IS NOT NULL
        SELECT t.FlowID, t.Name, v.Version, t.POGroupNum, t.[Platform], v.TemplateJSON, v.TwiML,
            v.CreatedDateTime, v.UpdatedDateTime, v.DeletedDateTime, v.CreatedBy, v.UpdatedBy, v.DeletedBy, v.SystemID AS VersionID
        FROM dbo.CallFlowTemplates t
        JOIN dbo.CallFlowTemplateVersions v ON v.CallFlowTemplateID = t.SystemID AND v.DeletedDateTime IS NULL
        WHERE t.DeletedDateTime IS NULL
          AND (t.POGroupNum = @pogroupnum OR t.POGroupNum LIKE @pogroupnum + '%');
END;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetAllCallFlowsByPlatform (@platform varchar(20))
AS
BEGIN
    SET NOCOUNT ON;
    IF @platform IS NOT NULL
        SELECT t.SystemID, t.FlowID, t.Name, v.Version, t.POGroupNum, t.[Platform], v.TemplateJSON, v.TwiML,
            v.CreatedDateTime, v.UpdatedDateTime, v.DeletedDateTime, v.CreatedBy, v.UpdatedBy, v.DeletedBy, v.SystemID AS VersionID
        FROM dbo.CallFlowTemplates t
        JOIN dbo.CallFlowTemplateVersions v ON v.CallFlowTemplateID = t.SystemID AND v.DeletedDateTime IS NULL
        WHERE t.DeletedDateTime IS NULL AND t.[Platform] = @platform;
END;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetAllAvailableFlowsForPOGroup (@pogroupnum varchar(20))
AS
BEGIN
    SET NOCOUNT ON;
    IF @pogroupnum IS NOT NULL
        SELECT t.SystemID, t.FlowID, t.Name, v.Version, t.POGroupNum, t.[Platform], v.TemplateJSON, v.TwiML,
            v.CreatedDateTime, v.UpdatedDateTime, v.DeletedDateTime, v.CreatedBy, v.UpdatedBy, v.DeletedBy, v.SystemID AS VersionID
        FROM dbo.CallFlowTemplates t
        JOIN dbo.CallFlowTemplateVersions v ON v.CallFlowTemplateID = t.SystemID AND v.DeletedDateTime IS NULL
        JOIN dbo.CallFlowTemplatePOGroups cfp ON cfp.FlowID = t.SystemID
        WHERE t.DeletedDateTime IS NULL AND cfp.POGroupNum = @pogroupnum AND cfp.DeletedDate IS NULL;
END;
GO
