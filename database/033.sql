USE [AutoCallDev]
GO
/****** Object:  StoredProcedure [dbo].[dash_ProfileManager_UpdatePOGroupCallFlows]    Script Date: 10/15/2026 2:31:51 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

ALTER   PROCEDURE [dbo].[dash_ProfileManager_UpdatePOGroupCallFlows]
    @POGroupNum varchar(20),
    @FlowIDs nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET ANSI_NULLS ON;
    SET ANSI_PADDING ON;
    SET ANSI_WARNINGS ON;
    SET ARITHABORT ON;
    SET CONCAT_NULL_YIELDS_NULL ON;
    SET QUOTED_IDENTIFIER ON;
    SET NUMERIC_ROUNDABORT OFF;

    SET @POGroupNum = LTRIM(RTRIM(@POGroupNum));
    IF NULLIF(@POGroupNum, '') IS NULL
        THROW 51003, 'POGroupNum is required.', 1;
    IF @FlowIDs IS NULL
        THROW 51003, 'FlowIDs is required; pass an empty string to remove all associations.', 1;

    DECLARE @RequestedFlowIDs TABLE (FlowID int NOT NULL PRIMARY KEY);
    SET @FlowIDs = LTRIM(RTRIM(@FlowIDs));

    IF @FlowIDs <> N''
    BEGIN
        DECLARE @EncodedFlowIDs nvarchar(max) = (
            SELECT @FlowIDs AS [text()] FOR XML PATH('')
        );
        DECLARE @FlowIDsXML xml = CONVERT(xml,
            N'<IDs><ID>' + REPLACE(@EncodedFlowIDs, N',', N'</ID><ID>') + N'</ID></IDs>');
        DECLARE @Tokens TABLE (Token nvarchar(max) NOT NULL);

        INSERT INTO @Tokens (Token)
        SELECT LTRIM(RTRIM(Item.Node.value('.', 'nvarchar(max)')))
        FROM @FlowIDsXML.nodes('/IDs/ID') AS Item(Node);

        IF EXISTS (
            SELECT 1 FROM @Tokens
            WHERE Token = N'' OR TRY_CONVERT(int, Token) IS NULL OR TRY_CONVERT(int, Token) <= 0
        )
            THROW 51003, 'FlowIDs must be a comma-separated list of positive integers.', 1;

        INSERT INTO @RequestedFlowIDs (FlowID)
        SELECT DISTINCT CONVERT(int, Token) FROM @Tokens;
    END;

    DECLARE @OwnTransaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;
    DECLARE @ExistingCount int;
    DECLARE @ChangedDate datetime;

    BEGIN TRY
        IF @OwnTransaction = 1
            BEGIN TRANSACTION;

        SELECT @ExistingCount = COUNT(*)
        FROM dbo.CallFlowTemplatePOGroups WITH (UPDLOCK, HOLDLOCK)
        WHERE POGroupNum = @POGroupNum AND DeletedDate IS NULL;

        SET @ChangedDate = GETUTCDATE();

        UPDATE Association
        SET DeletedDate = @ChangedDate
        FROM dbo.CallFlowTemplatePOGroups AS Association
        WHERE Association.POGroupNum = @POGroupNum
          AND Association.DeletedDate IS NULL
          AND NOT EXISTS (
              SELECT 1 FROM @RequestedFlowIDs AS Requested
              WHERE Requested.FlowID = Association.FlowID
          );

        INSERT INTO dbo.CallFlowTemplatePOGroups (POGroupNum, FlowID, CreatedDate, DeletedDate)
        SELECT @POGroupNum, Requested.FlowID, @ChangedDate, NULL
        FROM @RequestedFlowIDs AS Requested
        WHERE NOT EXISTS (
            SELECT 1 FROM dbo.CallFlowTemplatePOGroups AS Association
            WHERE Association.POGroupNum = @POGroupNum
              AND Association.FlowID = Requested.FlowID
              AND Association.DeletedDate IS NULL
        );

        IF @OwnTransaction = 1
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @OwnTransaction = 1 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
