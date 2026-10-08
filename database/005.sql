SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AutomatedCallEvents') AND name = N'UX_AutomatedCallEvents_CallId_IdempotencyKey')
CREATE UNIQUE INDEX UX_AutomatedCallEvents_CallId_IdempotencyKey
ON dbo.AutomatedCallEvents (CallId, IdempotencyKey)
WHERE CallId IS NOT NULL AND IdempotencyKey IS NOT NULL;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetTwiML
    @CallId int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TwiML FROM dbo.AutomatedCalls WHERE SystemID = @CallId;
END;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_Start
    @CallId int,
    @ProviderCallId varchar(200),
    @PhoneE164 varchar(16),
    @StartedCallStatus int,
    @PendingCallStatus int = NULL,
    @ExpectedTwiML nvarchar(max) = NULL,
    @StepId nvarchar(200) = NULL,
    @ExecutionId uniqueidentifier = NULL,
    @ResponseTwiML nvarchar(max) = NULL
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

        DECLARE @Found bit = 0,
                @Provider varchar(100),
                @ExistingCallId varchar(200),
                @ExistingPhone varchar(16),
                @TwiML nvarchar(max),
                @EventJSON nvarchar(max),
                @EventFound bit = 0,
                @Replayed bit = 0,
                @Outcome varchar(30) = 'started';

        SELECT @Found = 1,
               @Provider = Provider,
               @ExistingCallId = providerCallId,
               @ExistingPhone = PhoneE164,
               @TwiML = TwiML
        FROM dbo.AutomatedCalls WITH (UPDLOCK, HOLDLOCK)
        WHERE SystemID = @CallId;

        IF @Found = 0
            SET @Outcome = 'not-found';
        ELSE IF @Provider IS NULL OR LOWER(@Provider) <> 'twilio'
            SET @Outcome = 'provider-mismatch';
        ELSE IF @ExistingPhone IS NULL OR @ExistingPhone <> @PhoneE164
            SET @Outcome = 'destination-mismatch';
        ELSE IF @ExistingCallId IS NOT NULL
             AND @ExistingCallId COLLATE Latin1_General_100_BIN2 <> @ProviderCallId COLLATE Latin1_General_100_BIN2
            SET @Outcome = 'call-sid-mismatch';

        IF @Outcome = 'started'
        BEGIN
            SELECT @EventFound = 1, @EventJSON = EventJSON
            FROM dbo.AutomatedCallEvents
            WHERE CallId = @CallId AND IdempotencyKey = 'voice:start';

            IF @EventFound = 1
            BEGIN
                IF @EventJSON IS NULL OR ISJSON(@EventJSON) <> 1
                    SET @Outcome = 'invalid-twiml';
                ELSE
                BEGIN
                    SET @StepId = NULL;
                    SET @ExecutionId = NULL;
                    SET @ResponseTwiML = NULL;
                    SELECT @StepId = stepId,
                           @ExecutionId = TRY_CONVERT(uniqueidentifier, executionId),
                           @ResponseTwiML = responseTwiML
                    FROM OPENJSON(@EventJSON)
                    WITH
                    (
                        stepId nvarchar(200),
                        executionId nvarchar(36),
                        responseTwiML nvarchar(max)
                    );
                    IF @ExecutionId IS NULL OR NULLIF(@StepId, '') IS NULL
                              OR NULLIF(LTRIM(RTRIM(@ResponseTwiML)), '') IS NULL
                        SET @Outcome = 'invalid-twiml';
                    ELSE
                        SET @Replayed = 1;
                END;
            END
            ELSE
            BEGIN
                IF @ExecutionId IS NULL OR NULLIF(LTRIM(RTRIM(@StepId)), '') IS NULL
                   OR NULLIF(LTRIM(RTRIM(@ResponseTwiML)), '') IS NULL
                   OR @ExpectedTwiML IS NULL OR @TwiML IS NULL
                    SET @Outcome = 'invalid-twiml';
                ELSE IF DATALENGTH(@TwiML) <> DATALENGTH(@ExpectedTwiML)
                     OR @TwiML COLLATE Latin1_General_100_BIN2 <> @ExpectedTwiML COLLATE Latin1_General_100_BIN2
                    SET @Outcome = 'bundle-changed';
            END;
        END;

        IF @Outcome = 'started' AND @Replayed = 0
        BEGIN
            UPDATE dbo.AutomatedCalls
            SET providerCallId = COALESCE(providerCallId, @ProviderCallId),
                CallStatus = CASE
                    WHEN StartedDateTime IS NULL
                     AND (CallStatus = @PendingCallStatus OR (CallStatus IS NULL AND @PendingCallStatus IS NULL))
                    THEN @StartedCallStatus
                    ELSE CallStatus
                END,
                StartedDateTime = COALESCE(StartedDateTime, SYSUTCDATETIME()),
                UpdatedDateTime = SYSUTCDATETIME()
            WHERE SystemID = @CallId;

            SET @EventJSON = (
                SELECT @StepId AS stepId,
                       CONVERT(nvarchar(36), @ExecutionId) AS executionId,
                       @ProviderCallId AS providerCallId,
                       @ResponseTwiML AS responseTwiML
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            );

            INSERT INTO dbo.AutomatedCallEvents
                (CallId, CallEventDateTime, EventType, EventJSON, IdempotencyKey)
            VALUES
                (@CallId, SYSUTCDATETIME(), 'StepIssued', @EventJSON, 'voice:start');
        END;

        COMMIT TRANSACTION;

        SELECT @Outcome AS Outcome,
             CASE WHEN @Outcome = 'started' THEN @ResponseTwiML END AS ResponseTwiML,
             CASE WHEN @Outcome = 'started' THEN @StepId END AS StepId,
             CASE WHEN @Outcome = 'started' THEN @ExecutionId END AS ExecutionId,
             @Replayed AS Replayed;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO