SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'NotificationWorkflowHistoryNoteTests'
    THROW 51000, 'Run history-note tests only in the owned scratch database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.History
        (POGroupNum, OID, EventID, EventDescription, EventDateTime,
         Handled, Operator, Comment, NotifiedDateTime, EquipmentID)
    VALUES
        ('note-group', 'note-participant', 'ALARM', 'Original alarm', 1700000000,
         0, NULL, 'Original comment', 1700000001, 'device-1');
    DECLARE @HistoryID int = SCOPE_IDENTITY();
    DECLARE @Before int = dbo.fDtUnix(GETUTCDATE());

    EXEC dbo.activealarms_AddHistoryNote @HistoryID, 'Robo Call queued for participant.';

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.History
        WHERE SystemID <> @HistoryID AND POGroupNum = 'note-group' AND OID = 'note-participant'
          AND EventID = 'NOTE' AND EventDescription = 'NOTE' AND EventDateTime = 1700000000
          AND Handled = 1 AND Operator = 'SY' AND Comment = 'Robo Call queued for participant.'
          AND NotifiedDateTime BETWEEN @Before AND dbo.fDtUnix(GETUTCDATE())
          AND EquipmentID = 'device-1'
    )
        THROW 51000, 'Note must preserve original event context with exact text and a new posting time.', 1;
    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.History WHERE SystemID = @HistoryID
          AND EventID = 'ALARM' AND EventDescription = 'Original alarm'
          AND Comment = 'Original comment' AND NotifiedDateTime = 1700000001 AND Handled = 0
    )
        THROW 51000, 'Original alarm history must not be modified.', 1;

    BEGIN TRY
        EXEC dbo.activealarms_AddHistoryNote 2147483647, 'Robo Call queued for participant.';
        THROW 51001, 'A missing source history row must fail.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;

    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    PRINT 'History-note SQL regression checks passed; test data rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
