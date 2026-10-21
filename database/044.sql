SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-- AutoCallDB: deploy after transcription migrations through 020.sql.
IF COL_LENGTH(N'dbo.AutomatedCallQueue', N'OptionsJSON') IS NULL
    ALTER TABLE dbo.AutomatedCallQueue ADD OptionsJSON nvarchar(max) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_AutoCallQueue_Options')
    ALTER TABLE dbo.AutomatedCallQueue ADD CONSTRAINT CK_AutoCallQueue_Options
        CHECK (OptionsJSON IS NULL OR (ISJSON(OptionsJSON) = 1 AND LEFT(LTRIM(OptionsJSON), 1) = '{'));

IF OBJECT_ID(N'dbo.AutomatedCallNoteOutbox', N'U') IS NULL
CREATE TABLE dbo.AutomatedCallNoteOutbox
(
    SystemID bigint IDENTITY PRIMARY KEY,
    DeliveryId uniqueidentifier NOT NULL DEFAULT NEWID() UNIQUE,
    CallId int NOT NULL REFERENCES dbo.AutomatedCalls(SystemID),
    Kind varchar(20) NOT NULL CHECK (Kind IN ('COMPLETED', 'TRANSCRIPT')),
    SourceId int NOT NULL,
    OID varchar(20) NOT NULL,
    HistoryID int NOT NULL CHECK (HistoryID > 0),
    OccurredAt datetime2(3) NOT NULL,
    RecordingId int NULL,
    MessagesJSON nvarchar(max) NULL CHECK (MessagesJSON IS NULL OR ISJSON(MessagesJSON) = 1),
    Status varchar(20) NOT NULL DEFAULT 'READY'
        CHECK (Status IN ('READY', 'PROCESSING', 'DELIVERED', 'DEAD_LETTER', 'SKIPPED')),
    AttemptCount int NOT NULL DEFAULT 0,
    AvailableAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    LeaseToken uniqueidentifier NULL,
    LockedUntil datetime2(3) NULL,
    LastErrorCode varchar(100) NULL,
    CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    DeliveredAt datetime2(3) NULL,
    CONSTRAINT UQ_AutoCallNote_Source UNIQUE (CallId, Kind, SourceId)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.AutomatedCallNoteOutbox') AND name = 'IX_AutoCallNote_Pending')
    CREATE INDEX IX_AutoCallNote_Pending ON dbo.AutomatedCallNoteOutbox(Status, AvailableAt, SystemID);
GO

-- Called inside the callback transactions, never after the HTTP response.
CREATE OR ALTER PROCEDURE dbo.automatedcalls_QueueCompletionNote
    @CallId int
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.AutomatedCallNoteOutbox (CallId, Kind, SourceId, OID, HistoryID, OccurredAt)
    SELECT i.SystemID, 'COMPLETED', 0, q.OID, CONVERT(int, JSON_VALUE(q.OptionsJSON, '$.historyId')), i.EndedDateTime
    FROM dbo.AutomatedCalls i
    JOIN dbo.AutomatedCallQueue q ON q.SystemID = i.AutomatedCallQueueId
    WHERE i.SystemID = @CallId AND i.CallStatus = 'completed'
      AND i.EndedDateTime IS NOT NULL
      AND TRY_CONVERT(int, JSON_VALUE(q.OptionsJSON, '$.historyId')) > 0
      AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox o WHERE o.CallId = i.SystemID AND o.Kind = 'COMPLETED' AND o.SourceId = 0);
END;
GO
CREATE OR ALTER PROCEDURE dbo.transcription_QueueTranscriptNote
    @JobId int
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.AutomatedCallNoteOutbox
        (CallId, Kind, SourceId, OID, HistoryID, OccurredAt, RecordingId, MessagesJSON)
    SELECT r.CallId, 'TRANSCRIPT', i.SystemID, q.OID,
        CONVERT(int, JSON_VALUE(q.OptionsJSON, '$.historyId')), i.CompletedDateTime, i.RecordingId,
        COALESCE((SELECT t.Sentence, t.AudioChannel, t.StartTimeMilliseconds, t.EndTimeMilliseconds, t.TranscriptText
                  FROM dbo.AutomatedCallTranscripts t WHERE t.SubmissionId = i.CompletedSubmissionId
                  ORDER BY t.Sentence, t.AudioChannel FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')
    FROM dbo.AutomatedCallTranscriptionJobs i
    JOIN dbo.AutomatedCallRecordings r ON r.SystemID = i.RecordingId
    JOIN dbo.AutomatedCalls c ON c.SystemID = r.CallId
    JOIN dbo.AutomatedCallQueue q ON q.SystemID = c.AutomatedCallQueueId
    WHERE i.SystemID = @JobId AND i.Status = 'COMPLETED' AND i.CompletedSubmissionId IS NOT NULL
      AND JSON_VALUE(q.OptionsJSON, '$.postRecordingTranscript') = 'true'
      AND TRY_CONVERT(int, JSON_VALUE(q.OptionsJSON, '$.historyId')) > 0
      AND NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox o WHERE o.CallId = r.CallId AND o.Kind = 'TRANSCRIPT' AND o.SourceId = i.SystemID);
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_ClaimNote
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Now datetime2(3) = SYSUTCDATETIME();
    UPDATE o SET Status = 'SKIPPED', LastErrorCode = 'CALL_NOT_COMPLETED'
    FROM dbo.AutomatedCallNoteOutbox o JOIN dbo.AutomatedCalls c ON c.SystemID = o.CallId
    WHERE o.Status = 'READY' AND o.Kind = 'TRANSCRIPT' AND c.CallStatus IN ('failed','busy','no-answer','canceled');
    UPDATE dbo.AutomatedCallNoteOutbox SET Status = 'DEAD_LETTER', LastErrorCode = 'DELIVERY_ATTEMPTS_EXHAUSTED'
    WHERE AttemptCount >= 10 AND (Status = 'READY' OR (Status = 'PROCESSING' AND LockedUntil <= @Now));
    ;WITH NextNote AS
    (
        SELECT TOP (1) o.* FROM dbo.AutomatedCallNoteOutbox o WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE o.AttemptCount < 10 AND
            ((o.Status = 'READY' AND o.AvailableAt <= @Now) OR (o.Status = 'PROCESSING' AND o.LockedUntil <= @Now))
          AND (o.Kind = 'COMPLETED' OR
               (EXISTS (SELECT 1 FROM dbo.AutomatedCalls c WHERE c.SystemID = o.CallId AND c.CallStatus = 'completed')
                AND EXISTS (SELECT 1 FROM dbo.AutomatedCallNoteOutbox completion
                    WHERE completion.CallId = o.CallId AND completion.Kind = 'COMPLETED' AND completion.Status = 'DELIVERED')))
        ORDER BY o.SystemID
    )
    UPDATE NextNote SET Status = 'PROCESSING', LeaseToken = NEWID(), LockedUntil = DATEADD(second, 120, @Now),
        AttemptCount = AttemptCount + 1
    OUTPUT inserted.SystemID, inserted.DeliveryId, inserted.LeaseToken, inserted.CallId, inserted.Kind,
        inserted.OID, inserted.HistoryID, inserted.OccurredAt, inserted.RecordingId, inserted.MessagesJSON;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_FinishNote
    @Id bigint, @LeaseToken uniqueidentifier, @Success bit
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.AutomatedCallNoteOutbox
    SET Status = CASE WHEN @Success = 1 THEN 'DELIVERED' WHEN AttemptCount >= 10 THEN 'DEAD_LETTER' ELSE 'READY' END,
        DeliveredAt = CASE WHEN @Success = 1 THEN SYSUTCDATETIME() END,
        LastErrorCode = CASE WHEN @Success = 0 THEN 'TENANT_NOTE_DELIVERY_FAILED' END,
        AvailableAt = DATEADD(second, 60 * AttemptCount, SYSUTCDATETIME()), LeaseToken = NULL, LockedUntil = NULL
    WHERE SystemID = @Id AND Status = 'PROCESSING' AND LeaseToken = @LeaseToken AND LockedUntil > SYSUTCDATETIME();
    IF @@ROWCOUNT <> 1 THROW 51002, 'Tenant note delivery lease was lost.', 1;
END;
GO
