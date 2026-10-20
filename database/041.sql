SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'dbo.AutomatedCallTranscriptionJobs', N'CallbackWaitMinutes') IS NULL
    THROW 51000, 'Deploy transcription migration 018.sql before 041.sql.', 1;
GO

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @DefaultName sysname;
    SELECT @DefaultName = d.name
    FROM sys.default_constraints AS d
    JOIN sys.columns AS c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
    WHERE d.parent_object_id = OBJECT_ID(N'dbo.AutomatedCallTranscriptionJobs')
        AND c.name = N'CallbackWaitMinutes';
    IF @DefaultName IS NOT NULL
    BEGIN
        DECLARE @DropDefault nvarchar(max) = N'ALTER TABLE dbo.AutomatedCallTranscriptionJobs DROP CONSTRAINT ' + QUOTENAME(@DefaultName) + N';';
        EXEC sys.sp_executesql @DropDefault;
    END;
    -- Only future inserts use this budget; existing jobs and callback deadlines are unchanged.
    ALTER TABLE dbo.AutomatedCallTranscriptionJobs ADD
        CONSTRAINT DF_TranscriptionJobs_CallbackWait DEFAULT (45) FOR CallbackWaitMinutes;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
