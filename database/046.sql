SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.ProfileItems', N'U') IS NULL
    THROW 51000, 'Run this script manually in the Sentrak database containing dbo.ProfileItems.', 1;

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.ProfileItems', N'U')
      AND name = N'OptionsJSON'
      AND (user_type_id <> TYPE_ID(N'nvarchar') OR max_length <> -1 OR is_nullable = 0 OR is_computed = 1))
    THROW 51000, 'dbo.ProfileItems.OptionsJSON already exists with an incompatible definition; nullable nvarchar(max) is required.', 1;

IF COL_LENGTH(N'dbo.ProfileItems', N'OptionsJSON') IS NULL
    ALTER TABLE dbo.ProfileItems ADD OptionsJSON nvarchar(max) NULL;