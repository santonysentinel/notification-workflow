SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

IF OBJECT_ID(N'dbo.CallFlowTemplates', N'U') IS NULL
    THROW 51000, 'CallFlowTemplates must exist before applying migration 023.', 1;

IF COL_LENGTH('dbo.CallFlowTemplates', 'FlowID') IS NULL
    ALTER TABLE dbo.CallFlowTemplates ADD FlowID uniqueidentifier NULL;

EXEC sys.sp_executesql N'
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(''dbo.CallFlowTemplates'') AND name=''UX_CallFlowTemplates_FlowID_Version'')
CREATE UNIQUE INDEX UX_CallFlowTemplates_FlowID_Version ON dbo.CallFlowTemplates(FlowID,Version)
WHERE FlowID IS NOT NULL AND Version IS NOT NULL;';