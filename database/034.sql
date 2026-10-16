SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE OR ALTER  PROC [dbo].[activealarms_AddNote] (
	
	@oid VARCHAR(32)
	,@note TEXT
	)
AS
BEGIN

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