SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallRecordings') AND name = N'UX_AutomatedCallRecordings_SystemID_CallId')
        CREATE UNIQUE INDEX UX_AutomatedCallRecordings_SystemID_CallId ON dbo.AutomatedCallRecordings (SystemID, CallId);

    IF OBJECT_ID(N'dbo.AutomatedCallTranscriptionJobs', N'U') IS NULL
    CREATE TABLE dbo.AutomatedCallTranscriptionJobs
    (
        SystemID int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
        RecordingId int NOT NULL,
        Provider varchar(30) COLLATE Latin1_General_100_BIN2 NOT NULL DEFAULT ('Deepgram'),
        ConfigurationVersion int NOT NULL,
        ConfigurationJSON nvarchar(max) NOT NULL,
        Status varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL DEFAULT ('READY'),
        AvailableAt datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        AttemptCount int NOT NULL DEFAULT (0),
        MaxAttempts int NOT NULL DEFAULT (5),
        LockedBy varchar(100) NULL,
        LockedUntil datetime2(3) NULL,
        LeaseToken uniqueidentifier NULL,
        CompletedSubmissionId int NULL,
        CompletedDateTime datetime2(3) NULL,
        LastErrorCode varchar(100) NULL,
        LastErrorMessage nvarchar(2000) NULL,
        CreatedDateTime datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        UpdatedDateTime datetime2(3) NULL,
        Version rowversion,
        CONSTRAINT FK_TranscriptionJobs_Recording FOREIGN KEY (RecordingId) REFERENCES dbo.AutomatedCallRecordings (SystemID),
        CONSTRAINT UQ_TranscriptionJobs_Configuration UNIQUE (RecordingId, Provider, ConfigurationVersion),
        CONSTRAINT UQ_TranscriptionJobs_Recording UNIQUE (SystemID, RecordingId),
        CONSTRAINT CK_TranscriptionJobs_Configuration CHECK (ConfigurationVersion > 0 AND ISJSON(ConfigurationJSON) = 1),
        CONSTRAINT CK_TranscriptionJobs_Provider CHECK (Provider = 'Deepgram' AND DATALENGTH(Provider) = 8),
        CONSTRAINT CK_TranscriptionJobs_Status CHECK (Status IN ('READY', 'PROCESSING', 'WAITING_CALLBACK', 'COMPLETED', 'FAILED', 'DEAD_LETTER')),
        CONSTRAINT CK_TranscriptionJobs_Attempts CHECK (AttemptCount >= 0 AND MaxAttempts > 0 AND AttemptCount <= MaxAttempts),
        CONSTRAINT CK_TranscriptionJobs_Completion CHECK (
            (Status = 'COMPLETED' AND CompletedSubmissionId IS NOT NULL AND CompletedDateTime IS NOT NULL)
            OR (Status <> 'COMPLETED' AND CompletedSubmissionId IS NULL AND CompletedDateTime IS NULL))
    );

    IF OBJECT_ID(N'dbo.AutomatedCallTranscriptionSubmissions', N'U') IS NULL
    CREATE TABLE dbo.AutomatedCallTranscriptionSubmissions
    (
        SystemID int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
        TranscriptionJobId int NOT NULL,
        RecordingId int NOT NULL,
        AttemptNumber int NOT NULL,
        CorrelationId uniqueidentifier NOT NULL DEFAULT (NEWID()),
        CallbackTokenHash binary(32) NOT NULL,
        ProviderRequestId varchar(100) COLLATE Latin1_General_100_BIN2 NULL,
        Status varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL DEFAULT ('SUBMITTING'),
        CallbackDeadlineDateTime datetime2(3) NOT NULL,
        SubmittedDateTime datetime2(3) NULL,
        CallbackReceivedDateTime datetime2(3) NULL,
        ResponseJSON nvarchar(max) NULL,
        LastErrorCode varchar(100) NULL,
        LastErrorMessage nvarchar(2000) NULL,
        CreatedDateTime datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
        UpdatedDateTime datetime2(3) NULL,
        Version rowversion,
        CONSTRAINT FK_TranscriptionSubmissions_JobRecording FOREIGN KEY (TranscriptionJobId, RecordingId)
            REFERENCES dbo.AutomatedCallTranscriptionJobs (SystemID, RecordingId),
        CONSTRAINT UQ_TranscriptionSubmissions_Attempt UNIQUE (TranscriptionJobId, AttemptNumber),
        CONSTRAINT UQ_TranscriptionSubmissions_Correlation UNIQUE (CorrelationId),
        CONSTRAINT UQ_TranscriptionSubmissions_Recording UNIQUE (SystemID, RecordingId),
        CONSTRAINT UQ_TranscriptionSubmissions_Job UNIQUE (TranscriptionJobId, SystemID),
        CONSTRAINT CK_TranscriptionSubmissions_Attempt CHECK (AttemptNumber > 0),
        CONSTRAINT CK_TranscriptionSubmissions_Status CHECK (Status IN ('SUBMITTING', 'ACCEPTED', 'COMPLETED', 'FAILED', 'TIMED_OUT')),
        CONSTRAINT CK_TranscriptionSubmissions_Deadline CHECK (CallbackDeadlineDateTime > CreatedDateTime),
        CONSTRAINT CK_TranscriptionSubmissions_Request CHECK (ProviderRequestId IS NULL OR DATALENGTH(ProviderRequestId) > 0),
        CONSTRAINT CK_TranscriptionSubmissions_Response CHECK (ResponseJSON IS NULL OR ISJSON(ResponseJSON) = 1)
    );

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCallTranscriptionJobs') AND name = N'FK_TranscriptionJobs_CompletedSubmission')
        ALTER TABLE dbo.AutomatedCallTranscriptionJobs ADD CONSTRAINT FK_TranscriptionJobs_CompletedSubmission
            FOREIGN KEY (SystemID, CompletedSubmissionId) REFERENCES dbo.AutomatedCallTranscriptionSubmissions (TranscriptionJobId, SystemID);

    IF COL_LENGTH('dbo.AutomatedCallTranscripts', 'RecordingId') IS NULL
        ALTER TABLE dbo.AutomatedCallTranscripts ADD RecordingId int NULL;
    IF COL_LENGTH('dbo.AutomatedCallTranscripts', 'SubmissionId') IS NULL
        ALTER TABLE dbo.AutomatedCallTranscripts ADD SubmissionId int NULL;
    IF COL_LENGTH('dbo.AutomatedCallTranscripts', 'StartTimeMilliseconds') IS NULL
        ALTER TABLE dbo.AutomatedCallTranscripts ADD StartTimeMilliseconds int NULL;
    IF COL_LENGTH('dbo.AutomatedCallTranscripts', 'EndTimeMilliseconds') IS NULL
        ALTER TABLE dbo.AutomatedCallTranscripts ADD EndTimeMilliseconds int NULL;

    EXEC sys.sp_executesql N'
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N''dbo.AutomatedCallTranscripts'') AND name = N''FK_Transcripts_SubmissionRecording'')
        ALTER TABLE dbo.AutomatedCallTranscripts ADD CONSTRAINT FK_Transcripts_SubmissionRecording
            FOREIGN KEY (SubmissionId, RecordingId) REFERENCES dbo.AutomatedCallTranscriptionSubmissions (SystemID, RecordingId);
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N''dbo.AutomatedCallTranscripts'') AND name = N''FK_Transcripts_RecordingCall'')
        ALTER TABLE dbo.AutomatedCallTranscripts ADD CONSTRAINT FK_Transcripts_RecordingCall
            FOREIGN KEY (RecordingId, CallId) REFERENCES dbo.AutomatedCallRecordings (SystemID, CallId);
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N''dbo.AutomatedCallTranscripts'') AND name = N''CK_Transcripts_SubmissionMetadata'')
        ALTER TABLE dbo.AutomatedCallTranscripts ADD CONSTRAINT CK_Transcripts_SubmissionMetadata CHECK (
            (SubmissionId IS NULL AND RecordingId IS NULL)
            OR (SubmissionId IS NOT NULL AND RecordingId IS NOT NULL AND CallId IS NOT NULL
                AND Sentence IS NOT NULL AND Sentence >= 0 AND AudioChannel IS NOT NULL AND AudioChannel >= 0
                AND StartTimeMilliseconds IS NOT NULL AND StartTimeMilliseconds >= 0
                AND EndTimeMilliseconds IS NOT NULL AND EndTimeMilliseconds >= StartTimeMilliseconds
                AND (Confidence IS NULL OR Confidence BETWEEN 0 AND 1)
                AND (WordsJSON IS NULL OR ISJSON(WordsJSON) = 1)));
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N''dbo.AutomatedCallTranscripts'') AND name = N''UX_Transcripts_Submission_Sentence_Channel'')
        CREATE UNIQUE INDEX UX_Transcripts_Submission_Sentence_Channel
            ON dbo.AutomatedCallTranscripts (SubmissionId, Sentence, AudioChannel) WHERE SubmissionId IS NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N''dbo.AutomatedCallTranscripts'') AND name = N''IX_Transcripts_Recording'')
        CREATE INDEX IX_Transcripts_Recording ON dbo.AutomatedCallTranscripts (RecordingId, SubmissionId);
    ';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallTranscriptionJobs') AND name = N'IX_TranscriptionJobs_Ready')
        CREATE INDEX IX_TranscriptionJobs_Ready ON dbo.AutomatedCallTranscriptionJobs (AvailableAt, SystemID)
            INCLUDE (AttemptCount, MaxAttempts) WHERE Status = 'READY';
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallTranscriptionJobs') AND name = N'IX_TranscriptionJobs_ExpiredLease')
        CREATE INDEX IX_TranscriptionJobs_ExpiredLease ON dbo.AutomatedCallTranscriptionJobs (LockedUntil, SystemID)
            INCLUDE (LeaseToken, AttemptCount, MaxAttempts) WHERE Status = 'PROCESSING';
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallTranscriptionSubmissions') AND name = N'IX_TranscriptionSubmissions_Deadline')
        CREATE INDEX IX_TranscriptionSubmissions_Deadline ON dbo.AutomatedCallTranscriptionSubmissions (Status, CallbackDeadlineDateTime)
            INCLUDE (TranscriptionJobId, RecordingId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallTranscriptionSubmissions') AND name = N'UX_TranscriptionSubmissions_RequestId')
        CREATE UNIQUE INDEX UX_TranscriptionSubmissions_RequestId ON dbo.AutomatedCallTranscriptionSubmissions (ProviderRequestId)
            WHERE ProviderRequestId IS NOT NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO