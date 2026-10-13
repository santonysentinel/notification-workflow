SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CallFlowTemplates', N'U') IS NULL
    THROW 51000, 'CallFlowTemplates must exist before applying migration 022.', 1;

IF COL_LENGTH('dbo.CallFlowTemplates', 'Platform') IS NULL
    ALTER TABLE dbo.CallFlowTemplates ADD Platform varchar(20) NULL;