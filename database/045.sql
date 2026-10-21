SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
-- Deploy in every routed tenant database before starting the TenantNotes worker.
IF OBJECT_ID(N'dbo.AutomatedCallNoteReceipts', N'U') IS NULL
CREATE TABLE dbo.AutomatedCallNoteReceipts
(
    DeliveryId uniqueidentifier NOT NULL PRIMARY KEY,
    HistoryID int NOT NULL,
    OID varchar(20) NOT NULL,
    PayloadHash binary(32) NOT NULL,
    PostedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE OR ALTER PROCEDURE dbo.activealarms_PostAutoCallNotes
    @DeliveryId uniqueidentifier,
    @HistoryID int,
    @OID varchar(20),
    @NotesJSON nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @DeliveryId IS NULL OR @DeliveryId = '00000000-0000-0000-0000-000000000000'
        OR @HistoryID IS NULL OR @HistoryID <= 0 OR NULLIF(LTRIM(RTRIM(@OID)), '') IS NULL
        THROW 51000, 'Complete tenant note identity is required.', 1;
    IF @NotesJSON IS NULL OR ISJSON(@NotesJSON) <> 1 OR LEFT(LTRIM(@NotesJSON), 1) <> '['
        THROW 51000, 'Notes must be a JSON array.', 1;
    IF NOT EXISTS (SELECT 1 FROM OPENJSON(@NotesJSON))
        OR EXISTS (SELECT 1 FROM OPENJSON(@NotesJSON)
            WHERE type <> 1 OR NULLIF(LTRIM(RTRIM(value)), '') IS NULL OR DATALENGTH(value) > 500)
        THROW 51000, 'Each note must contain 1 to 250 UTF-16 characters.', 1;
    DECLARE @Hash binary(32) = HASHBYTES('SHA2_256', @NotesJSON);
    BEGIN TRY
        BEGIN TRANSACTION;
        IF EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteReceipts WITH (UPDLOCK, HOLDLOCK) WHERE DeliveryId = @DeliveryId)
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteReceipts
                WHERE DeliveryId = @DeliveryId AND HistoryID = @HistoryID AND OID = @OID AND PayloadHash = @Hash)
                THROW 51001, 'Delivery identity conflicts with the previously posted notes.', 1;
            COMMIT;
            RETURN;
        END;
        IF NOT EXISTS (SELECT 1 FROM dbo.History WHERE SystemID = @HistoryID AND OID = @OID)
            THROW 51000, 'Original history row not found for the participant.', 1;
        DECLARE @StoredNotes TABLE (Comment nvarchar(max));
        INSERT dbo.History
            (POGroupNum, OID, EventID, EventDescription, EventDateTime, Handled, Operator, Comment, NotifiedDateTime, EquipmentID)
        OUTPUT inserted.Comment INTO @StoredNotes
        SELECT h.POGroupNum, h.OID, 'NOTE', 'NOTE', h.EventDateTime, 1, 'SY', n.value,
            dbo.fDtUnix(GETUTCDATE()), h.EquipmentID
        FROM dbo.History h CROSS JOIN OPENJSON(@NotesJSON) n
        WHERE h.SystemID = @HistoryID AND h.OID = @OID
        ORDER BY CONVERT(int, n.[key]);
        IF EXISTS
        (
            SELECT CONVERT(varbinary(max), [value]), COUNT_BIG(*) FROM OPENJSON(@NotesJSON)
            GROUP BY CONVERT(varbinary(max), [value])
            EXCEPT
            SELECT CONVERT(varbinary(max), Comment), COUNT_BIG(*) FROM @StoredNotes
            GROUP BY CONVERT(varbinary(max), Comment)
        )
            THROW 51000, 'Tenant History.Comment cannot store the note text without loss.', 1;
        INSERT dbo.AutomatedCallNoteReceipts (DeliveryId, HistoryID, OID, PayloadHash)
        VALUES (@DeliveryId, @HistoryID, @OID, @Hash);
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
