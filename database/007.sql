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
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.AutomatedCalls') AND name = 'CallStatus' AND TYPE_NAME(user_type_id) = 'int')
    BEGIN
        IF OBJECT_ID('tempdb..#TwilioCallStatusMapping') IS NULL
            CREATE TABLE #TwilioCallStatusMapping (LegacyCallStatus int PRIMARY KEY, CallStatus varchar(20) NOT NULL);
        IF EXISTS (
            SELECT 1 FROM dbo.AutomatedCalls AS calls
            LEFT JOIN #TwilioCallStatusMapping AS mapping ON mapping.LegacyCallStatus = calls.CallStatus
            WHERE calls.CallStatus IS NOT NULL AND (mapping.LegacyCallStatus IS NULL OR mapping.CallStatus COLLATE Latin1_General_100_BIN2 NOT IN
                ('queued', 'initiated', 'ringing', 'in-progress', 'completed', 'busy', 'failed', 'no-answer', 'canceled'))
        )
            THROW 51000, 'Supply an explicit #TwilioCallStatusMapping for every existing numeric call status before migrating.', 1;
        IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCalls') AND name = 'FK_AutomatedCalls_CallStatus')
            ALTER TABLE dbo.AutomatedCalls DROP CONSTRAINT FK_AutomatedCalls_CallStatus;
        EXEC(N'ALTER TABLE dbo.AutomatedCalls ALTER COLUMN CallStatus varchar(20) NULL;');
        EXEC(N'UPDATE calls SET CallStatus = mapping.CallStatus FROM dbo.AutomatedCalls AS calls JOIN #TwilioCallStatusMapping AS mapping ON mapping.LegacyCallStatus = TRY_CONVERT(int, calls.CallStatus);');
    END;

    IF COL_LENGTH('dbo.AutomatedCalls', 'QueueLeaseToken') IS NULL
        ALTER TABLE dbo.AutomatedCalls ADD QueueLeaseToken uniqueidentifier NULL;
    IF COL_LENGTH('dbo.AutomatedCalls', 'EndedDateTime') IS NULL
        ALTER TABLE dbo.AutomatedCalls ADD EndedDateTime datetime2(3) NULL;
    IF COL_LENGTH('dbo.AutomatedCalls', 'CallDurationSeconds') IS NULL
        ALTER TABLE dbo.AutomatedCalls ADD CallDurationSeconds int NULL;
    IF COL_LENGTH('dbo.AutomatedCalls', 'LastStatusSequenceNumber') IS NULL
        ALTER TABLE dbo.AutomatedCalls ADD LastStatusSequenceNumber int NULL;
    IF COL_LENGTH('dbo.AutomatedCalls', 'LastStatusDateTime') IS NULL
        ALTER TABLE dbo.AutomatedCalls ADD LastStatusDateTime datetime2(3) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCalls') AND name = 'CK_AutomatedCalls_TwilioStatus')
        EXEC(N'ALTER TABLE dbo.AutomatedCalls ADD CONSTRAINT CK_AutomatedCalls_TwilioStatus CHECK (CallStatus IS NULL OR CallStatus COLLATE Latin1_General_100_BIN2 IN (''queued'', ''initiated'', ''ringing'', ''in-progress'', ''completed'', ''busy'', ''failed'', ''no-answer'', ''canceled''));');
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.AutomatedCalls') AND name = 'CK_AutomatedCalls_Reporting')
        EXEC(N'ALTER TABLE dbo.AutomatedCalls ADD CONSTRAINT CK_AutomatedCalls_Reporting CHECK ((CallDurationSeconds IS NULL OR CallDurationSeconds >= 0) AND (LastStatusSequenceNumber IS NULL OR LastStatusSequenceNumber >= 0));');
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_Status
    @CallId int,
    @ProviderCallId varchar(200),
    @PhoneE164 varchar(16),
    @CallStatus varchar(20),
    @SequenceNumber int,
    @Timestamp datetime2(3),
    @CallDurationSeconds int = NULL,
    @SipResponseCode int = NULL,
    @ParametersJSON nvarchar(max) = N'{}'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET ANSI_PADDING ON;
    SET ANSI_WARNINGS ON;
    SET ARITHABORT ON;
    SET CONCAT_NULL_YIELDS_NULL ON;
    SET NUMERIC_ROUNDABORT OFF;

    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Found bit = 0, @Provider varchar(100), @ExistingSid varchar(200), @ExistingPhone varchar(16),
                @CurrentStatus varchar(20), @LastSequence int, @EndedDateTime datetime2(3),
                @QueueId int, @QueueLeaseToken uniqueidentifier,
                @Outcome varchar(30) = 'recorded', @Applied bit = 0, @QueueFinalized bit = 0,
                @IgnoreReason varchar(40), @Terminal bit = 0, @Now datetime2(3) = SYSUTCDATETIME(),
                @Key varchar(255) = 'voice:status:' + @ProviderCallId + ':' + CONVERT(varchar(11), @SequenceNumber);

        SELECT @Found = 1, @Provider = Provider, @ExistingSid = providerCallId, @ExistingPhone = PhoneE164,
               @CurrentStatus = CallStatus, @LastSequence = LastStatusSequenceNumber, @EndedDateTime = EndedDateTime,
               @QueueId = AutomatedCallQueueId, @QueueLeaseToken = QueueLeaseToken
        FROM dbo.AutomatedCalls WITH (UPDLOCK, HOLDLOCK) WHERE SystemID = @CallId;

        IF @Found = 0 SET @Outcome = 'not-found';
        ELSE IF @Provider IS NULL OR LOWER(@Provider) <> 'twilio' SET @Outcome = 'provider-mismatch';
        ELSE IF @ExistingPhone IS NULL OR @PhoneE164 IS NULL OR @ExistingPhone <> @PhoneE164 SET @Outcome = 'destination-mismatch';
        ELSE IF NULLIF(@ProviderCallId, '') IS NULL OR (@ExistingSid IS NOT NULL AND @ExistingSid COLLATE Latin1_General_100_BIN2 <> @ProviderCallId COLLATE Latin1_General_100_BIN2) SET @Outcome = 'call-sid-mismatch';
        ELSE IF @CallStatus IS NULL OR @CallStatus COLLATE Latin1_General_100_BIN2 NOT IN ('queued', 'initiated', 'ringing', 'in-progress', 'completed', 'busy', 'failed', 'no-answer', 'canceled')
             OR @SequenceNumber IS NULL OR @SequenceNumber < 0 OR @Timestamp IS NULL
             OR @CallDurationSeconds < 0 OR @SipResponseCode NOT BETWEEN 100 AND 699
             OR @ParametersJSON IS NULL OR ISJSON(@ParametersJSON) <> 1 SET @Outcome = 'invalid-status';
        ELSE IF EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND IdempotencyKey = @Key) SET @Outcome = 'duplicate';

        IF @Outcome = 'recorded'
        BEGIN
            IF @EndedDateTime IS NOT NULL OR @CurrentStatus IN ('completed', 'busy', 'failed', 'no-answer', 'canceled')
                SET @IgnoreReason = 'already-terminal';
            ELSE IF @LastSequence IS NOT NULL AND @SequenceNumber <= @LastSequence
                SET @IgnoreReason = 'older-sequence';
            ELSE IF CASE @CallStatus WHEN 'queued' THEN 0 WHEN 'initiated' THEN 1 WHEN 'ringing' THEN 2 WHEN 'in-progress' THEN 3 ELSE 4 END
                  < CASE @CurrentStatus WHEN 'queued' THEN 0 WHEN 'initiated' THEN 1 WHEN 'ringing' THEN 2 WHEN 'in-progress' THEN 3 ELSE -1 END
                SET @IgnoreReason = 'status-regression';
            ELSE SET @Applied = 1;

            SET @Terminal = CASE WHEN @CallStatus IN ('completed', 'busy', 'failed', 'no-answer', 'canceled') THEN 1 ELSE 0 END;
            IF @Applied = 1
            BEGIN
                UPDATE dbo.AutomatedCalls
                SET providerCallId = COALESCE(providerCallId, @ProviderCallId), CallStatus = @CallStatus,
                    LastStatusSequenceNumber = @SequenceNumber, LastStatusDateTime = @Timestamp, UpdatedDateTime = @Now,
                    EndedDateTime = CASE WHEN @Terminal = 1 THEN @Timestamp ELSE EndedDateTime END,
                    CallDurationSeconds = CASE WHEN @Terminal = 1 THEN @CallDurationSeconds ELSE CallDurationSeconds END
                WHERE SystemID = @CallId;

                IF @Terminal = 1 AND @QueueId IS NOT NULL AND @QueueLeaseToken IS NOT NULL
                BEGIN
                    UPDATE dbo.AutomatedCallQueue
                    SET Status = CASE WHEN @CallStatus = 'completed' THEN 'COMPLETED' ELSE 'FAILED' END,
                        CompletedAt = CASE WHEN @CallStatus = 'completed' THEN @Timestamp END,
                        FailedAt = CASE WHEN @CallStatus <> 'completed' THEN @Timestamp END,
                        LastErrorCode = CASE WHEN @CallStatus <> 'completed' THEN 'twilio:' + @CallStatus END,
                        LastErrorMessage = CASE WHEN @CallStatus <> 'completed' THEN 'Twilio call ended with status ' + @CallStatus END,
                        LockedBy = NULL, LockedAt = NULL, LockedUntil = NULL, LeaseToken = NULL,
                        UpdatedAt = @Now, UpdatedDateTime = @Now
                    WHERE SystemID = @QueueId AND Status = 'PROCESSING' AND LeaseToken = @QueueLeaseToken;
                    IF @@ROWCOUNT = 1 SET @QueueFinalized = 1;
                END;
            END
            ELSE IF @ExistingSid IS NULL
                UPDATE dbo.AutomatedCalls SET providerCallId = @ProviderCallId, UpdatedDateTime = @Now WHERE SystemID = @CallId;

            DECLARE @EventJSON nvarchar(max) = (
                SELECT @ProviderCallId AS providerCallId, @CallStatus AS callStatus, @SequenceNumber AS sequenceNumber,
                       CONVERT(varchar(33), @Timestamp, 126) + 'Z' AS timestamp,
                       CONVERT(varchar(33), @Now, 126) + 'Z' AS receivedDateTime,
                       @CallDurationSeconds AS callDurationSeconds, @SipResponseCode AS sipResponseCode,
                       @Applied AS applied, @IgnoreReason AS ignoreReason, @QueueFinalized AS queueFinalized,
                       JSON_QUERY(@ParametersJSON) AS parameters
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
            );
            INSERT INTO dbo.AutomatedCallEvents (CallId, CallEventDateTime, EventType, EventJSON, IdempotencyKey)
            VALUES (@CallId, @Timestamp, 'CallStatusReceived', @EventJSON, @Key);
        END;
        COMMIT TRANSACTION;
        SELECT @Outcome AS Outcome, @Applied AS Applied, @QueueFinalized AS QueueFinalized, @IgnoreReason AS IgnoreReason;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO