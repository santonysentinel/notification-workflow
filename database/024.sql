SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO



CREATE OR ALTER PROCEDURE dbo.AutoCall_PublishFlow
    @FlowID uniqueidentifier,
    @ExpectedVersion int,
    @TemplateJSON nvarchar(max),
    @TwiML nvarchar(max),
    @CreatedBy nvarchar(200),
    @Name varchar(200) = NULL,
    @Platform varchar(20) = NULL,
    @POGroupNum varchar(20) = NULL
WITH EXECUTE AS OWNER
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

    IF @FlowID IS NULL OR @FlowID = '00000000-0000-0000-0000-000000000000'
        OR @ExpectedVersion IS NULL OR @ExpectedVersion < 0
        OR @TemplateJSON IS NULL OR DATALENGTH(@TemplateJSON) = 0
        OR @TwiML IS NULL OR DATALENGTH(@TwiML) = 0
        OR NULLIF(LTRIM(RTRIM(@CreatedBy)), N'') IS NULL
        THROW 51003, 'A family UUID, expected latest version, both artifacts and audit identity are required.', 1;

    IF COL_LENGTH('dbo.CallFlowTemplates', 'FlowID') IS NULL
        THROW 51000, 'Migration 023 is required before publication.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes AS ix
        WHERE ix.object_id = OBJECT_ID('dbo.CallFlowTemplates')
          AND ix.name = 'UX_CallFlowTemplates_FlowID_Version'
          AND ix.is_unique = 1 AND ix.is_disabled = 0 AND ix.has_filter = 1
          AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ix.filter_definition,
              '[', ''), ']', ''), '(', ''), ')', ''), ' ', ''), CHAR(13), ''), CHAR(10), '')) = 'flowidisnotnullandversionisnotnull'
          AND (SELECT COUNT(*) FROM sys.index_columns WHERE object_id = ix.object_id AND index_id = ix.index_id AND key_ordinal > 0) = 2
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                      WHERE ic.object_id = ix.object_id AND ic.index_id = ix.index_id AND ic.key_ordinal = 1 AND col.name = 'FlowID')
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                      WHERE ic.object_id = ix.object_id AND ic.index_id = ix.index_id AND ic.key_ordinal = 2 AND col.name = 'Version'))
        THROW 51000, 'Migration 023 is required before publication.', 1;

    DECLARE @OwnTransaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;
    DECLARE @LockResource nvarchar(255) = N'AutoCallFlow:' + LOWER(CONVERT(nvarchar(36), @FlowID));
    DECLARE @LockResult int;
    DECLARE @Latest int;
    DECLARE @NextVersion int;
    DECLARE @Published TABLE (FlowID uniqueidentifier, SystemID int, Version int);

    BEGIN TRY
        IF @OwnTransaction = 1
            BEGIN TRANSACTION;

        EXEC @LockResult = sys.sp_getapplock
            @Resource = @LockResource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @LockResult < 0
            THROW 51002, 'Publication lock could not be acquired.', 1;

        SELECT @Latest = ISNULL(MAX(Version), 0)
        FROM dbo.CallFlowTemplates WITH (UPDLOCK, HOLDLOCK)
        WHERE FlowID = @FlowID;

        IF @Latest <> @ExpectedVersion
            THROW 51001, 'This flow has a newer published revision. Open the latest revision before publishing.', 1;
        IF @Latest = 2147483647
            THROW 51002, 'No further versions can be allocated.', 1;

        SET @NextVersion = @Latest + 1;
        INSERT INTO dbo.CallFlowTemplates (FlowID, Name, Version, POGroupNum, Platform, TemplateJSON, TwiML, CreatedBy)
        OUTPUT inserted.FlowID, inserted.SystemID, inserted.Version INTO @Published
        VALUES (@FlowID, @Name, @NextVersion, @POGroupNum, @Platform, @TemplateJSON, @TwiML, @CreatedBy);

        IF @OwnTransaction = 1
            COMMIT TRANSACTION;

        SELECT FlowID, SystemID, Version FROM @Published;
    END TRY
    BEGIN CATCH
        IF @OwnTransaction = 1 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO