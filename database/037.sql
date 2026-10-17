SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-- Deploy in AutoCallDB before 037.sql. Existing rows deliberately remain unassigned.
IF OBJECT_ID(N'dbo.AutomatedCallQueue', N'U') IS NULL
    THROW 51000, 'AutomatedCallQueue must exist before adding POGroupNum.', 1;

IF COL_LENGTH(N'dbo.AutomatedCallQueue', N'POGroupNum') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD POGroupNum varchar(20) NULL;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallQueue')
      AND name = N'IX_AutomatedCallQueue_POGroupNum'
)
    CREATE INDEX IX_AutomatedCallQueue_POGroupNum
        ON dbo.AutomatedCallQueue (POGroupNum)
        WHERE POGroupNum IS NOT NULL;
GO



