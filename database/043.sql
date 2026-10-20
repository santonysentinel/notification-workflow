SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Deploy in each tenant workflow database before updating the parsers.
CREATE OR ALTER PROCEDURE dbo.activealarms_AddHistoryNote
    @HistoryID int,
    @Note varchar(250)
AS
BEGIN
    SET NOCOUNT ON;

    IF @HistoryID IS NULL OR @HistoryID <= 0
        THROW 51000, 'A valid original HistoryID is required.', 1;
    IF NULLIF(LTRIM(RTRIM(@Note)), '') IS NULL
        THROW 51000, 'Note text is required.', 1;

    INSERT INTO dbo.History
        (POGroupNum, OID, EventID, EventDescription, EventDateTime,
         Handled, Operator, Comment, NotifiedDateTime, EquipmentID)
    SELECT POGroupNum, OID, 'NOTE', 'NOTE', EventDateTime,
        1, 'SY', @Note, dbo.fDtUnix(GETUTCDATE()), EquipmentID
    FROM dbo.History
    WHERE SystemID = @HistoryID;

    IF @@ROWCOUNT <> 1
        THROW 51000, 'The original history row was not found.', 1;
END;
GO
