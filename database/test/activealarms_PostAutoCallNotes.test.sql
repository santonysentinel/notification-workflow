SET NOCOUNT ON;
SET XACT_ABORT OFF;
IF DB_NAME() <> N'NotificationWorkflowTenantReceiptTests'
    THROW 51000, 'Run tenant receipt tests only in the owned scratch database.', 1;
BEGIN TRY
    INSERT dbo.History (POGroupNum, OID, EventID, EventDescription, EventDateTime, Comment, EquipmentID)
    VALUES ('original-group', 'original-oid', 'ALARM', 'Original alarm', 1700000000, 'Original comment', 'original-device');
    DECLARE @HistoryID int = SCOPE_IDENTITY(), @Delivery uniqueidentifier = NEWID(), @Before int = dbo.fDtUnix(GETUTCDATE());
    DECLARE @Notes nvarchar(max) = N'["Completion note","Transcript part 1","Transcript part 2"]';
    EXEC dbo.activealarms_PostAutoCallNotes @Delivery, @HistoryID, 'original-oid', @Notes;
    EXEC dbo.activealarms_PostAutoCallNotes @Delivery, @HistoryID, 'original-oid', @Notes;
    IF (SELECT COUNT(*) FROM dbo.History) <> 4 OR (SELECT COUNT(*) FROM dbo.AutomatedCallNoteReceipts) <> 1
        THROW 51000, 'Replayed delivery must not duplicate any note parts.', 1;
    IF (SELECT COUNT(*) FROM dbo.History WHERE SystemID <> @HistoryID AND OID = 'original-oid'
        AND POGroupNum = 'original-group' AND EventDateTime = 1700000000 AND EquipmentID = 'original-device'
        AND EventID = 'NOTE' AND Operator = 'SY' AND Handled = 1
        AND NotifiedDateTime BETWEEN @Before AND dbo.fDtUnix(GETUTCDATE())) <> 3
        THROW 51000, 'All notes must preserve the original alarm context.', 1;
    BEGIN TRY
        EXEC dbo.activealarms_PostAutoCallNotes @Delivery, @HistoryID, 'original-oid', N'["Changed note"]';
        THROW 51001, 'Payload conflict must fail.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51001 OR ERROR_MESSAGE() = 'Payload conflict must fail.' THROW;
    END CATCH;
    DECLARE @Other uniqueidentifier = NEWID();
    BEGIN TRY
        EXEC dbo.activealarms_PostAutoCallNotes @Other, @HistoryID, 'wrong-oid', @Notes;
        THROW 51001, 'Mismatched participant must fail.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    DECLARE @Long nvarchar(max) = N'["' + REPLICATE(N'x', 251) + N'"]';
    BEGIN TRY
        EXEC dbo.activealarms_PostAutoCallNotes @Other, @HistoryID, 'original-oid', @Long;
        THROW 51001, 'Oversized note must fail.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    IF (SELECT COUNT(*) FROM dbo.History) <> 4 OR (SELECT COUNT(*) FROM dbo.AutomatedCallNoteReceipts) <> 1
        THROW 51000, 'Rejected deliveries must not create notes or receipts.', 1;
    SET @Notes = N'["' + REPLICATE(N'x', 250) + N'"]';
    EXEC dbo.activealarms_PostAutoCallNotes @Other, @HistoryID, 'original-oid', @Notes;
    IF NOT EXISTS (SELECT 1 FROM dbo.History WHERE DATALENGTH(Comment) = 500)
        THROW 51000, 'Exactly 250 UTF-16 characters must fit.', 1;

    ALTER TABLE dbo.History ADD CONSTRAINT CK_TestNoteStorage CHECK (Comment <> N'fail-store');
    SET @Other = NEWID();
    BEGIN TRY
        EXEC dbo.activealarms_PostAutoCallNotes @Other, @HistoryID, 'original-oid', N'["first part","fail-store"]';
        THROW 51001, 'Storage failure must roll back every part.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 547 THROW;
    END CATCH;
    ALTER TABLE dbo.History DROP CONSTRAINT CK_TestNoteStorage;
    IF (SELECT COUNT(*) FROM dbo.History) <> 5 OR (SELECT COUNT(*) FROM dbo.AutomatedCallNoteReceipts) <> 2
        THROW 51000, 'Storage failure must not leave partial notes or a receipt.', 1;

    ALTER TABLE dbo.History ALTER COLUMN Comment varchar(250) COLLATE Latin1_General_100_CI_AS NULL;
    SET @Notes = N'["ASCII part","' + NCHAR(0x4E2D) + N'"]';
    BEGIN TRY
        EXEC dbo.activealarms_PostAutoCallNotes @Other, @HistoryID, 'original-oid', @Notes;
        THROW 51001, 'Unrepresentable text must fail, not silently become a question mark.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    IF (SELECT COUNT(*) FROM dbo.History) <> 5 OR (SELECT COUNT(*) FROM dbo.AutomatedCallNoteReceipts) <> 2
        THROW 51000, 'Encoding failure must roll back every part and the receipt.', 1;
    PRINT 'Tenant receipt SQL regression checks passed.';
END TRY
BEGIN CATCH
    THROW;
END CATCH;
