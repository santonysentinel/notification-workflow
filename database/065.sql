SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
-- AutoCallDB: deploy after 062-064, before Action 20 producers and TenantNotes.
-- Officer identity is pinned inside OptionsJSON and copied unchanged to fallback jobs.
IF COL_LENGTH(N'dbo.AutomatedCallQueue', N'FlowVersionId') IS NULL
    THROW 51000, 'Deploy the call-flow version split before Officer Robo Call.', 1;
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
        IF (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON))
            - (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'waitForCompletion' AND type = 3)
            - (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'officer' AND type = 5) <> 3
            OR NOT EXISTS (SELECT 1 FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'historyId' AND type = 2 AND TRY_CONVERT(int, value) > 0)
            OR NOT EXISTS (SELECT 1 FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'postRecordingTranscript' AND type = 3)
            OR (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'waitForCompletion') > 1
            OR (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'officer') > 1
            OR NOT EXISTS (SELECT 1 FROM OPENJSON(@OptionsJSON) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'callDevices' AND type = 4)
            THROW 51000, 'Options require a positive historyId, boolean postRecordingTranscript, callDevices and optional boolean waitForCompletion.', 1;

        DECLARE @Officer nvarchar(max) = JSON_QUERY(@OptionsJSON, '$.officer'), @OfficerType nvarchar(20);
        IF @Officer IS NOT NULL
        BEGIN
            SET @OfficerType = JSON_VALUE(@Officer, '$.selection.type');
            IF (SELECT COUNT(*) FROM OPENJSON(@Officer)) <> 3
                OR NOT EXISTS (SELECT 1 FROM OPENJSON(@Officer) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'selection' AND type = 5)
                OR (SELECT COUNT(*) FROM OPENJSON(@Officer, '$.selection')) <> 4
                OR @OfficerType IS NULL OR @OfficerType COLLATE Latin1_General_100_BIN2 NOT IN ('Primary','RoleBased','Custom')
                OR NOT EXISTS (SELECT 1 FROM OPENJSON(@Officer, '$.selection') WHERE [key] COLLATE Latin1_General_100_BIN2 = 'type' AND type = 1)
                OR NOT EXISTS (SELECT 1 FROM OPENJSON(@Officer) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'id'
                    AND ((@OfficerType = 'Custom' AND type = 0) OR (@OfficerType <> 'Custom' AND type = 2 AND TRY_CONVERT(int,value) > 0)))
                OR NOT EXISTS (SELECT 1 FROM OPENJSON(@Officer) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'name'
                    AND ((@OfficerType = 'Custom' AND type = 0) OR (@OfficerType <> 'Custom' AND type = 1 AND LEN(LTRIM(RTRIM(value))) > 0 AND DATALENGTH(value) <= 400)))
                OR NOT EXISTS (SELECT 1 FROM OPENJSON(@Officer, '$.selection') WHERE [key] COLLATE Latin1_General_100_BIN2 = 'roleId'
                    AND ((@OfficerType <> 'RoleBased' AND type = 0) OR (@OfficerType = 'RoleBased' AND type = 2 AND TRY_CONVERT(int,value) > 0)))
                OR NOT EXISTS (SELECT 1 FROM OPENJSON(@Officer, '$.selection') WHERE [key] COLLATE Latin1_General_100_BIN2 = 'roleType'
                    AND ((@OfficerType <> 'RoleBased' AND type = 0) OR (@OfficerType = 'RoleBased' AND type = 1 AND LEN(LTRIM(RTRIM(value))) > 0)))
                OR NOT EXISTS (SELECT 1 FROM OPENJSON(@Officer, '$.selection') WHERE [key] COLLATE Latin1_General_100_BIN2 = 'rolePOGroup'
                    AND ((@OfficerType <> 'RoleBased' AND type = 0) OR (@OfficerType = 'RoleBased' AND type = 1 AND LEN(LTRIM(RTRIM(value))) > 0 AND DATALENGTH(value) <= 40)))
                THROW 51000, 'Officer options require a valid selection and pinned officer identity; Custom has no officer identity.', 1;
        END;
        IF (SELECT COUNT(*) FROM OPENJSON(@OptionsJSON, '$.callDevices')) NOT BETWEEN 1 AND 2
            OR EXISTS
            (
                SELECT 1 FROM OPENJSON(@OptionsJSON, '$.callDevices') d
                WHERE d.type <> 5
                   OR (SELECT COUNT(*) FROM OPENJSON(d.value)) <> 2
                   OR NOT EXISTS (SELECT 1 FROM OPENJSON(d.value) WHERE [key] COLLATE Latin1_General_100_BIN2 = 'type' AND type = 1
                        AND value COLLATE Latin1_General_100_BIN2 IN ('CellPhone', 'HomePhone', 'WorkPhone', 'Custom'))
                   OR (@Officer IS NOT NULL AND JSON_VALUE(d.value,'$.type') NOT IN ('CellPhone','Custom'))
                   OR (@OfficerType = 'Custom' AND JSON_VALUE(d.value,'$.type') <> 'Custom')
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
                OR ISNULL(JSON_QUERY(@ExistingOptions, '$.officer'), N'') COLLATE Latin1_General_100_BIN2
                    <> ISNULL(JSON_QUERY(@OptionsJSON, '$.officer'), N'') COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(ISNULL(JSON_QUERY(@ExistingOptions, '$.officer'), N'')) <> DATALENGTH(ISNULL(JSON_QUERY(@OptionsJSON, '$.officer'), N''))
                THROW 51001, 'Enqueue key conflicts with the original request snapshot.', 1;
        END
        ELSE
        BEGIN
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

CREATE OR ALTER PROCEDURE dbo.automatedcalls_ClaimNote
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    DECLARE @Claimed TABLE
    (
        SystemID bigint, DeliveryId uniqueidentifier, LeaseToken uniqueidentifier, CallId int, Kind varchar(20),
        OID varchar(20), HistoryID int, OccurredAt datetime2(3), RecordingId int, MessagesJSON nvarchar(max),
        QueueId int, ActiveAlarmID int
    );
    UPDATE o SET Status = 'SKIPPED', LastErrorCode = 'CALL_NOT_COMPLETED'
    FROM dbo.AutomatedCallNoteOutbox o JOIN dbo.AutomatedCalls c ON c.SystemID = o.CallId
    WHERE o.Status = 'READY' AND o.Kind = 'TRANSCRIPT'
      AND (c.CallStatus IN ('failed','busy','no-answer','canceled') OR c.AnsweredBy LIKE 'machine[_]%' OR c.AnsweredBy = 'fax');
    UPDATE o SET Status = 'SKIPPED', LastErrorCode = 'RECORD_STEP_MESSAGE'
    FROM dbo.AutomatedCallNoteOutbox o
    LEFT JOIN dbo.AutomatedCallRecordings r ON r.SystemID = o.RecordingId
    WHERE o.Status = 'READY' AND o.Kind = 'TRANSCRIPT' AND ISNULL(r.RecordingSource, '') <> 'RecordVerb'
      AND (EXISTS (SELECT 1 FROM dbo.AutomatedCallRecordings m WHERE m.CallId = o.CallId AND m.RecordingSource = 'RecordVerb')
           OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents e WHERE e.CallId = o.CallId AND e.EventType = 'InputReceived'
                      AND JSON_VALUE(CASE WHEN ISJSON(e.EventJSON) = 1 THEN e.EventJSON ELSE N'{}' END, '$.inputType') = 'recording'));
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
        inserted.QueueId, inserted.ActiveAlarmID
    INTO @Claimed;
    SELECT n.SystemID, n.DeliveryId, n.LeaseToken, n.CallId, n.Kind, n.OID, n.HistoryID, n.OccurredAt, n.RecordingId,
        n.MessagesJSON, n.QueueId, n.ActiveAlarmID,
        r.RecordingSource, c.PhoneType,
        (SELECT TOP (1) JSON_VALUE(e.EventJSON, '$.stepId')
         FROM dbo.AutomatedCallEvents e
         WHERE e.CallId = n.CallId AND e.EventType = 'InputReceived'
           AND JSON_VALUE(CASE WHEN ISJSON(e.EventJSON) = 1 THEN e.EventJSON ELSE N'{}' END, '$.inputType') = 'recording'
           AND JSON_VALUE(CASE WHEN ISJSON(e.EventJSON) = 1 THEN e.EventJSON ELSE N'{}' END, '$.input.recordingSid')
               COLLATE Latin1_General_100_BIN2 = r.RecordingSid COLLATE Latin1_General_100_BIN2
         ORDER BY e.SystemID) AS MessageStepId,
        JSON_QUERY(q.OptionsJSON, '$.officer') AS OfficerJSON
    FROM @Claimed n
    LEFT JOIN dbo.AutomatedCallRecordings r ON n.Kind = 'TRANSCRIPT' AND r.SystemID = n.RecordingId
    LEFT JOIN dbo.AutomatedCalls c ON c.SystemID = n.CallId
    LEFT JOIN dbo.AutomatedCallQueue q ON q.SystemID = COALESCE(n.QueueId, c.AutomatedCallQueueId);
END;
GO

CREATE OR ALTER PROC dbo.automatedcalls_GetAllCallForPOGroup
    @pogroupnum varchar(20), @startdate datetime, @enddate datetime
AS
BEGIN
    SET NOCOUNT ON;
    IF NULLIF(LTRIM(RTRIM(@pogroupnum)), '') IS NULL
        THROW 51000, 'POGroupNum is required.', 1;
    IF @startdate IS NULL OR @enddate IS NULL OR @startdate >= @enddate
        THROW 51000, 'A UTC startdate earlier than enddate is required.', 1;
    SELECT
        c.SystemID AS CallId, q.SystemID AS AutomatedCallQueueId, q.ActiveAlarmID, q.POGroupNum,
        COALESCE(c.OID, q.OID) AS OID, c.PhoneE164, COALESCE(c.FlowId, q.FlowId) AS FlowId,
        c.Provider, c.providerCallId AS ProviderCallId, c.CallStatus, c.SubmissionState, c.SubmissionErrorCode,
        q.Status AS QueueStatus, c.CreatedDateTime, c.StartedDateTime, c.EndedDateTime,
        c.UpdatedDateTime, c.CallDurationSeconds,
        COALESCE((
            SELECT r.SystemID AS RecordingId, r.Provider, r.RecordingSid, r.RecordingStatus,
                r.DurationSeconds, r.Channels, r.RecordingStartTime, r.CreatedDateTime, r.UpdatedDateTime
            FROM dbo.AutomatedCallRecordings r WHERE r.CallId = c.SystemID
            ORDER BY r.SystemID FOR JSON PATH, INCLUDE_NULL_VALUES
        ), N'[]') AS RecordingsJSON,
        COALESCE((
            SELECT j.SystemID AS TranscriptionJobId, j.RecordingId, j.Provider, j.ConfigurationVersion,
                j.Status AS TranscriptionStatus, j.AttemptCount, j.MaxAttempts, j.CompletedSubmissionId,
                j.CompletedDateTime, j.LastErrorCode, j.CreatedDateTime, j.UpdatedDateTime,
                latest.SubmissionId, latest.SubmissionStatus, latest.AttemptNumber AS SubmissionAttemptNumber,
                latest.SubmittedDateTime, latest.CallbackReceivedDateTime, latest.LastErrorCode AS SubmissionErrorCode,
                JSON_QUERY(COALESCE((
                    SELECT t.Sentence, t.AudioChannel, t.StartTimeMilliseconds, t.EndTimeMilliseconds, t.TranscriptText
                    FROM dbo.AutomatedCallTranscripts t WHERE t.SubmissionId = j.CompletedSubmissionId
                    ORDER BY t.Sentence, t.AudioChannel FOR JSON PATH, INCLUDE_NULL_VALUES
                ), N'[]')) AS Messages
            FROM dbo.AutomatedCallTranscriptionJobs j
            INNER JOIN dbo.AutomatedCallRecordings r ON r.SystemID = j.RecordingId
            OUTER APPLY (
                SELECT TOP (1) s.SystemID AS SubmissionId, s.Status AS SubmissionStatus, s.AttemptNumber,
                    s.SubmittedDateTime, s.CallbackReceivedDateTime, s.LastErrorCode
                FROM dbo.AutomatedCallTranscriptionSubmissions s
                WHERE s.TranscriptionJobId = j.SystemID AND s.RecordingId = j.RecordingId
                ORDER BY s.AttemptNumber DESC, s.SystemID DESC
            ) latest
            WHERE r.CallId = c.SystemID ORDER BY j.RecordingId, j.ConfigurationVersion, j.SystemID
            FOR JSON PATH, INCLUDE_NULL_VALUES
        ), N'[]') AS TranscriptionsJSON,
        q.CreatedDateTime AS QueueCreatedDateTime, q.UpdatedAt AS QueueUpdatedAt, q.AvailableAt AS QueueAvailableAt,
        q.AttemptCount AS QueueAttemptCount, q.MaxAttempts AS QueueMaxAttempts, q.LastErrorCode AS QueueLastErrorCode,
        COALESCE(c.PhoneType, (SELECT JSON_VALUE(d.value, '$.type')
            FROM OPENJSON(JSON_QUERY(CASE WHEN ISJSON(q.OptionsJSON) = 1 THEN q.OptionsJSON END, '$.callDevices')) d
            WHERE d.type = 5 AND d.[key] = CONVERT(nvarchar(11), q.CallDeviceIndex))) AS PhoneType,
        c.AnsweredBy, q.CallDeviceIndex, q.RootQueueId,
        CASE WHEN JSON_QUERY(q.OptionsJSON, '$.officer') IS NULL THEN 'Participant' ELSE 'Officer' END AS RecipientKind,
        TRY_CONVERT(int, JSON_VALUE(q.OptionsJSON, '$.officer.id')) AS OfficerId,
        JSON_VALUE(q.OptionsJSON, '$.officer.name') AS OfficerName,
        JSON_VALUE(q.OptionsJSON, '$.officer.selection.type') AS OfficerType,
        JSON_VALUE(q.OptionsJSON, '$.officer.selection.roleType') AS OfficerRoleType
    FROM dbo.AutomatedCallQueue q
    OUTER APPLY (
        SELECT TOP (1) attempt.SystemID, attempt.OID, attempt.PhoneE164, attempt.FlowId,
            attempt.Provider, attempt.providerCallId, attempt.CallStatus, attempt.SubmissionState, attempt.SubmissionErrorCode,
            attempt.CreatedDateTime, attempt.StartedDateTime, attempt.EndedDateTime,
            attempt.UpdatedDateTime, attempt.CallDurationSeconds, attempt.PhoneType, attempt.AnsweredBy
        FROM dbo.AutomatedCalls attempt WHERE attempt.AutomatedCallQueueId = q.SystemID
        ORDER BY attempt.CreatedDateTime DESC, attempt.SystemID DESC
    ) c
    WHERE (q.POGroupNum = @pogroupnum OR q.POGroupNum LIKE @pogroupnum + '%')
      AND q.CreatedDateTime >= @startdate AND q.CreatedDateTime < @enddate
    ORDER BY q.CreatedDateTime DESC, q.SystemID DESC;
END;
GO
