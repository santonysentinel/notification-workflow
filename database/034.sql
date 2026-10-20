SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE OR ALTER  PROC [dbo].[activealarms_AddNote] (
	
	@oid VARCHAR(32)
	,@note TEXT
	,@HistoryID INT = NULL
	)
AS
BEGIN
		SET NOCOUNT ON;

		-- Legacy callers without HistoryID retain standalone participant-note behavior.
		IF @HistoryID IS NOT NULL
		BEGIN
			IF @HistoryID <= 0
				THROW 51000, 'A valid original HistoryID is required.', 1;

			INSERT INTO dbo.History (
				POGroupNum, OID, EventID, EventDescription, EventDateTime,
				Comment, NotifiedDateTime, EquipmentID
			)
			SELECT POGroupNum, OID, '888', 'System Entered Note', EventDateTime,
				@note, dbo.fDTUnix(GETUTCDATE()), EquipmentID
			FROM dbo.History
			WHERE SystemID = @HistoryID AND OID = @oid;

			IF @@ROWCOUNT <> 1
				THROW 51000, 'Original history row not found for the participant.', 1;

			SELECT 1 AS success, '' AS msg;
			RETURN;
		END;

		declare @pogroupnum varchar(32)

		SELECT @pogroupnum = POGroupNum
		FROM Client
		WHERE OID = @oid

		INSERT INTO dbo.History (
			[POGroupNum]
			,[OID]
			,[EventID]
			,[EventDescription]
			,[EventDateTime]
			,[Comment]
			,[NotifiedDateTime]
			)
		VALUES (
			@pogroupnum
			,@oid
			,'888'
			,'System Entered Note'
			,dbo.fDTUnix(getutcdate())
			,@note
			,dbo.fDTUnix(getutcdate())
			)

		SELECT 1 AS success
			,'' AS msg
	
END