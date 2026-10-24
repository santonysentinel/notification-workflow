SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH('dbo.CallFlowTemplateVersions', 'CallFlowTemplateID') IS NULL
    OR COL_LENGTH('dbo.CallFlowTemplates', 'Version') IS NOT NULL
    THROW 51000, 'The call-flow template/version split must be deployed before publication.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes AS ix
    WHERE ix.object_id = OBJECT_ID('dbo.CallFlowTemplateVersions')
      AND ix.name = 'UX_CallFlowTemplateVersions_Active'
      AND ix.is_unique = 1 AND ix.is_disabled = 0 AND ix.has_filter = 1
      AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ix.filter_definition,
          '[', ''), ']', ''), '(', ''), ')', ''), ' ', ''), CHAR(13), ''), CHAR(10), '')) = 'deleteddatetimeisnull'
      AND (SELECT COUNT(*) FROM sys.index_columns WHERE object_id = ix.object_id AND index_id = ix.index_id AND key_ordinal > 0) = 1
      AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
          WHERE ic.object_id = ix.object_id AND ic.index_id = ix.index_id AND ic.key_ordinal = 1 AND col.name = 'CallFlowTemplateID'))
    THROW 51000, 'The call-flow template/version indexes must be deployed before publication.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes AS ix
    WHERE ix.object_id = OBJECT_ID('dbo.CallFlowTemplateVersions')
      AND ix.name = 'UX_CallFlowTemplateVersions_Template_Version'
      AND ix.is_unique = 1 AND ix.is_disabled = 0 AND ix.has_filter = 0
      AND (SELECT COUNT(*) FROM sys.index_columns WHERE object_id = ix.object_id AND index_id = ix.index_id AND key_ordinal > 0) = 2
      AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
          WHERE ic.object_id = ix.object_id AND ic.index_id = ix.index_id AND ic.key_ordinal = 1 AND col.name = 'CallFlowTemplateID')
      AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
          WHERE ic.object_id = ix.object_id AND ic.index_id = ix.index_id AND ic.key_ordinal = 2 AND col.name = 'Version'))
    THROW 51000, 'The call-flow template/version indexes must be deployed before publication.', 1;
GO

IF OBJECT_ID(N'dbo.AutoCall_PublishFlow', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.AutoCall_PublishFlow AS RETURN 0;');
GO

ALTER PROCEDURE dbo.AutoCall_PublishFlow
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
        OR NULLIF(LTRIM(RTRIM(@Platform)), '') IS NULL
        OR NULLIF(LTRIM(RTRIM(@POGroupNum)), '') IS NULL
        THROW 51003, 'A family UUID, expected latest version, both artifacts and audit identity are required.', 1;

    DECLARE @OwnTransaction bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;
    DECLARE @LockResource nvarchar(255) = N'AutoCallFlow:' + LOWER(CONVERT(nvarchar(36), @FlowID));
    DECLARE @LockResult int;
    DECLARE @Latest int;
    DECLARE @NextVersion int;
    DECLARE @TemplateID int;
    DECLARE @VersionID int;
    DECLARE @Changed datetime2(3) = SYSUTCDATETIME();

    BEGIN TRY
        IF @OwnTransaction = 1
            BEGIN TRANSACTION;

        EXEC @LockResult = sys.sp_getapplock
            @Resource = @LockResource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @LockResult < 0
            THROW 51002, 'Publication lock could not be acquired.', 1;

                SELECT @TemplateID = SystemID
        FROM dbo.CallFlowTemplates WITH (UPDLOCK, HOLDLOCK)
        WHERE FlowID = @FlowID;

                IF @TemplateID IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM dbo.CallFlowTemplates
                        WHERE SystemID = @TemplateID AND DeletedDateTime IS NULL
                            AND POGroupNum = @POGroupNum AND Platform = @Platform)
                        THROW 51003, 'The flow is unavailable for this PO group or platform.', 1;

                SELECT @Latest = ISNULL(MAX(Version), 0)
                FROM dbo.CallFlowTemplateVersions WITH (UPDLOCK, HOLDLOCK)
                WHERE CallFlowTemplateID = @TemplateID;

        IF @Latest <> @ExpectedVersion
            THROW 51001, 'This flow has a newer published revision. Open the latest revision before publishing.', 1;
        IF @Latest = 2147483647
            THROW 51002, 'No further versions can be allocated.', 1;

        SET @NextVersion = @Latest + 1;
        IF @TemplateID IS NULL
        BEGIN
            INSERT INTO dbo.CallFlowTemplates (FlowID, Name, POGroupNum, Platform, CreatedBy)
            VALUES (@FlowID, @Name, @POGroupNum, @Platform, @CreatedBy);
            SET @TemplateID = CONVERT(int, SCOPE_IDENTITY());
        END
        ELSE
            UPDATE dbo.CallFlowTemplates
            SET Name = @Name, UpdatedDateTime = @Changed, UpdatedBy = @CreatedBy
            WHERE SystemID = @TemplateID;

        UPDATE dbo.CallFlowTemplateVersions
        SET DeletedDateTime = @Changed, DeletedBy = @CreatedBy
        WHERE CallFlowTemplateID = @TemplateID AND DeletedDateTime IS NULL;

        INSERT INTO dbo.CallFlowTemplateVersions
            (CallFlowTemplateID, Version, TemplateJSON, TwiML, CreatedBy)
        VALUES (@TemplateID, @NextVersion, @TemplateJSON, @TwiML, @CreatedBy);
        SET @VersionID = CONVERT(int, SCOPE_IDENTITY());

        IF @OwnTransaction = 1
            COMMIT TRANSACTION;

        SELECT @FlowID AS FlowID, @TemplateID AS SystemID, @NextVersion AS Version,
            @VersionID AS VersionSystemID;
    END TRY
    BEGIN CATCH
        IF @OwnTransaction = 1 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO