SET NOCOUNT ON;
SET XACT_ABORT OFF;

IF DB_NAME() <> N'NotificationWorkflowAddNoteTests'
    THROW 51000, 'Run Add Note tests only in the owned scratch database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.Client (OID, POGroupNum) VALUES ('note-participant', 'current-group');
    INSERT dbo.History
        (POGroupNum, OID, EventID, EventDescription, EventDateTime,
         Comment, NotifiedDateTime, EquipmentID)
    VALUES
        ('original-group', 'note-participant', 'ALARM', 'Original alarm', 1700000000,
         'Original comment', 1700000001, 'original-device');
    DECLARE @HistoryID int = SCOPE_IDENTITY();
    DECLARE @Before int = dbo.fDtUnix(GETUTCDATE());
    DECLARE @Note varchar(1000) = REPLICATE('n', 1000);

    EXEC dbo.activealarms_AddNote @oid = 'note-participant', @note = @Note, @HistoryID = @HistoryID;

    IF (SELECT COUNT(*) FROM dbo.History) <> 2
        OR NOT EXISTS
        (
            SELECT 1 FROM dbo.History
            WHERE SystemID <> @HistoryID AND POGroupNum = 'original-group' AND OID = 'note-participant'
              AND EventID = '888' AND EventDescription = 'System Entered Note'
              AND EventDateTime = 1700000000 AND EquipmentID = 'original-device'
              AND CONVERT(varchar(max), Comment) = @Note AND DATALENGTH(Comment) = 1000
              AND NotifiedDateTime BETWEEN @Before AND dbo.fDtUnix(GETUTCDATE())
        )
        THROW 51000, 'Note must retain original alarm context, full text and a new posting time.', 1;
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.History WHERE SystemID = @HistoryID
          AND EventID = 'ALARM' AND EventDescription = 'Original alarm'
          AND EventDateTime = 1700000000 AND NotifiedDateTime = 1700000001
          AND CONVERT(varchar(max), Comment) = 'Original comment'
    )
        THROW 51000, 'Original history must remain unchanged.', 1;

    DECLARE @Invalid TABLE (HistoryID int, OID varchar(32));
    INSERT @Invalid VALUES (0, 'note-participant'), (-1, 'note-participant'),
        (2147483647, 'note-participant'), (@HistoryID, 'different-participant');
    DECLARE @InvalidID int, @InvalidOID varchar(32);
    WHILE EXISTS (SELECT 1 FROM @Invalid)
    BEGIN
        SELECT TOP (1) @InvalidID = HistoryID, @InvalidOID = OID FROM @Invalid;
        BEGIN TRY
            EXEC dbo.activealarms_AddNote @oid = @InvalidOID, @note = 'Must not be written', @HistoryID = @InvalidID;
            THROW 51001, 'Invalid or mismatched history must fail.', 1;
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() <> 51000 THROW;
        END CATCH;
        DELETE @Invalid WHERE HistoryID = @InvalidID AND OID = @InvalidOID;
    END;
    IF (SELECT COUNT(*) FROM dbo.History) <> 2
        THROW 51000, 'Invalid history must not create a standalone note.', 1;

    EXEC dbo.activealarms_AddNote @oid = 'note-participant', @note = 'Legacy note';
    IF (SELECT COUNT(*) FROM dbo.History) <> 3
        OR NOT EXISTS
        (
            SELECT 1 FROM dbo.History
            WHERE CONVERT(varchar(max), Comment) = 'Legacy note'
              AND POGroupNum = 'current-group' AND OID = 'note-participant'
              AND EventID = '888' AND EventDescription = 'System Entered Note'
              AND EventDateTime BETWEEN @Before AND dbo.fDtUnix(GETUTCDATE())
              AND NotifiedDateTime BETWEEN @Before AND dbo.fDtUnix(GETUTCDATE())
              AND EquipmentID IS NULL
        )
        THROW 51000, 'Legacy callers must retain standalone-note behavior.', 1;

    ROLLBACK TRANSACTION;
    PRINT 'Add Note SQL regression checks passed; test data rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
