-- DNA DB


SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.ProfileItems', N'U') IS NULL
    THROW 51000, 'Run this script manually in the Sentrak database containing dbo.ProfileItems.', 1;

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.ProfileItems', N'U')
      AND name = N'FlowId'
      AND (user_type_id <> TYPE_ID(N'int') OR is_nullable = 0 OR is_computed = 1))
    THROW 51000, 'dbo.ProfileItems.FlowId already exists with an incompatible definition; nullable int is required.', 1;

IF COL_LENGTH(N'dbo.ProfileItems', N'FlowId') IS NULL
    ALTER TABLE dbo.ProfileItems ADD FlowId int NULL;
    