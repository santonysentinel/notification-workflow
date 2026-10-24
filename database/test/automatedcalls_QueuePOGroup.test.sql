SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;

IF DB_NAME() <> N'NotificationWorkflowQueueGroupTests'
    THROW 51000, 'Run queue POGroup tests only in the owned scratch database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.CallFlowTemplates(Name, CreatedBy) VALUES('group-snapshot', N'test');
    DECLARE @Flow int = SCOPE_IDENTITY(), @Key uniqueidentifier = NEWID(), @LegacyKey uniqueidentifier = NEWID();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID, Version, TemplateJSON, TwiML, CreatedBy) VALUES(@Flow, 1, N'{"schemaVersion":1}', N'<CallFlow/>', N'test');
    DECLARE @FlowVersion int = SCOPE_IDENTITY();
    DECLARE @Result TABLE (SystemID int);
    INSERT @Result EXEC dbo.activealarms_call_InsertJob
        @ActiveAlarmID = 42, @OID = 'oid-42', @FlowId = @Flow, @VariablesJSON = N'{"offenderName":"Alex"}',
        @EnqueueIdempotencyKey = @Key, @POGroupNum = 'AbC123';
    DECLARE @JobId int = (SELECT SystemID FROM @Result), @Before nvarchar(max);
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.AutomatedCallQueue
        WHERE SystemID = @JobId AND ActiveAlarmID = 42 AND OID = 'oid-42'
          AND POGroupNum COLLATE Latin1_General_100_BIN2 = 'AbC123'
          AND VariablesJSON = N'{"offenderName":"Alex"}'
    )
        THROW 51000, 'Enqueue did not persist the POGroup alongside the existing request snapshot.', 1;

    SET @Before = (SELECT * FROM dbo.AutomatedCallQueue WHERE SystemID = @JobId FOR JSON PATH, INCLUDE_NULL_VALUES);
    DELETE @Result;
    INSERT @Result EXEC dbo.activealarms_call_InsertJob
        @ActiveAlarmID = 42, @OID = 'oid-42', @FlowId = @Flow, @Priority = 99,
        @VariablesJSON = N'{"offenderName":"Alex"}', @EnqueueIdempotencyKey = @Key, @POGroupNum = 'AbC123';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE SystemID = @JobId)
        OR @Before <> (SELECT * FROM dbo.AutomatedCallQueue WHERE SystemID = @JobId FOR JSON PATH, INCLUDE_NULL_VALUES)
        THROW 51000, 'Matching replays must preserve the original queue row and POGroup.', 1;

    DELETE @Result;
    INSERT @Result EXEC dbo.activealarms_call_InsertJob
        @ActiveAlarmID = 43, @OID = 'oid-43', @FlowId = @Flow, @VariablesJSON = N'{}',
        @EnqueueIdempotencyKey = @LegacyKey;
    DECLARE @LegacyJobId int = (SELECT SystemID FROM @Result);
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallQueue WHERE SystemID = @LegacyJobId AND POGroupNum IS NULL)
        THROW 51000, 'Existing callers without a group must remain compatible and unassigned.', 1;
    DELETE @Result;
    INSERT @Result EXEC dbo.activealarms_call_InsertJob
        @ActiveAlarmID = 43, @OID = 'oid-43', @FlowId = @Flow, @VariablesJSON = N'{}',
        @EnqueueIdempotencyKey = @LegacyKey;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE SystemID = @LegacyJobId)
        THROW 51000, 'Legacy unassigned replays must remain idempotent.', 1;

    ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

DECLARE @Conflicts TABLE (OriginalGroup varchar(20), ReplayGroup varchar(20));
INSERT @Conflicts VALUES ('group', 'different'), ('group', 'GROUP'), ('group', 'group '),
                        ('group', NULL), (NULL, 'group');
DECLARE @OriginalGroup varchar(20), @ReplayGroup varchar(20);
DECLARE ConflictingGroups CURSOR LOCAL FAST_FORWARD FOR SELECT OriginalGroup, ReplayGroup FROM @Conflicts;
OPEN ConflictingGroups;
FETCH NEXT FROM ConflictingGroups INTO @OriginalGroup, @ReplayGroup;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;
        INSERT dbo.CallFlowTemplates(Name, CreatedBy) VALUES('group-conflict', N'test');
        SET @Flow = SCOPE_IDENTITY();
        INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID, Version, TemplateJSON, TwiML, CreatedBy) VALUES(@Flow, 1, N'{}', N'<CallFlow/>', N'test');
        SET @Key = NEWID();
        DELETE @Result;
        INSERT @Result EXEC dbo.activealarms_call_InsertJob
            @ActiveAlarmID = 42, @OID = 'oid-42', @FlowId = @Flow, @VariablesJSON = N'{}',
            @EnqueueIdempotencyKey = @Key, @POGroupNum = @OriginalGroup;
        EXEC dbo.activealarms_call_InsertJob
            @ActiveAlarmID = 42, @OID = 'oid-42', @FlowId = @Flow, @VariablesJSON = N'{}',
            @EnqueueIdempotencyKey = @Key, @POGroupNum = @ReplayGroup;
        THROW 51002, 'Conflicting group replay was accepted.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51001
        BEGIN
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            THROW;
        END;
    END CATCH;
    IF @@TRANCOUNT <> 0 OR EXISTS (SELECT 1 FROM dbo.CallFlowTemplates WHERE SystemID = @Flow)
        THROW 51000, 'Conflicting replays must roll back without retaining test fixtures.', 1;
    FETCH NEXT FROM ConflictingGroups INTO @OriginalGroup, @ReplayGroup;
END;
CLOSE ConflictingGroups;
DEALLOCATE ConflictingGroups;

BEGIN TRY
    EXEC dbo.activealarms_call_InsertJob
        @ActiveAlarmID = 42, @OID = 'oid-42', @FlowId = 1, @VariablesJSON = N'{}',
        @EnqueueIdempotencyKey = @Key, @POGroupNum = ' ';
    THROW 51002, 'Blank supplied group was accepted.', 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() <> 51000 OR ERROR_MESSAGE() <> 'POGroupNum must be nonblank when supplied.' THROW;
END CATCH;
PRINT 'Queue POGroup persistence, exact replay fencing and legacy compatibility verified; test fixtures rolled back.';
