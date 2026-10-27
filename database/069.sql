SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
-- AutoCallDB: deploy after 065, before enabling OfficePhone for Action 20.
-- Participant devices are unchanged; OfficePhone is only valid with an officer snapshot.
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
                        AND (value COLLATE Latin1_General_100_BIN2 IN ('CellPhone', 'HomePhone', 'WorkPhone', 'Custom')
                            OR (@Officer IS NOT NULL AND value COLLATE Latin1_General_100_BIN2 = 'OfficePhone')))
                   OR (@Officer IS NOT NULL AND JSON_VALUE(d.value,'$.type') NOT IN ('CellPhone','OfficePhone','Custom'))
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
