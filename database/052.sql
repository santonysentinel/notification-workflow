SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.ProfileItems', N'U') IS NULL
    THROW 51000, 'dbo.ProfileItems is required.', 1;

IF COL_LENGTH(N'dbo.ProfileItems', N'FlowId') IS NULL
    RETURN;

ALTER TABLE dbo.ProfileItems DROP COLUMN FlowId;
