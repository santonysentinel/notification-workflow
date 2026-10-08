SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'NotificationWorkflowVoiceTests'
    THROW 51000, 'Run this regression script only in the isolated NotificationWorkflowVoiceTests database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @StartedStatus int = (SELECT MIN(LookUpId) FROM dbo.LookUpFields),
            @ProtectedStatus int = (SELECT MAX(LookUpId) FROM dbo.LookUpFields),
            @CallId int,
            @Sid varchar(200) = 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            @Phone varchar(16) = '+15551234567',
            @Bundle nvarchar(max) = N'<CallFlow initialStep="reminder"><Step id="reminder"><Response><Say>Hello Alex.</Say><Redirect method="POST">https://example.com/webhooks/twilio/voice/next?callId=42&amp;executionId={{executionId}}</Redirect></Response></Step><Step id="later"><Response><Say>Later step.</Say></Response></Step></CallFlow>',
            @ResponseTwiML nvarchar(max),
            @ExecutionId uniqueidentifier = NEWID(),
            @PreparedResponse nvarchar(max);

    SET @PreparedResponse = N'<Response><Say>Hello Alex.</Say><Redirect method="POST">https://example.com/webhooks/twilio/voice/next?callId=42&amp;executionId='
        + CONVERT(nvarchar(36), @ExecutionId) + N'</Redirect></Response>';

    IF @StartedStatus IS NULL OR @ProtectedStatus = @StartedStatus
        THROW 51000, 'The isolated test database needs two lookup status rows.', 1;

    DECLARE @Result TABLE
    (
        Outcome varchar(30),
        ResponseTwiML nvarchar(max),
        StepId nvarchar(200),
        ExecutionId uniqueidentifier,
        Replayed bit
    );

    INSERT INTO dbo.AutomatedCalls (Provider, PhoneE164, TwiML, Parameters)
    VALUES ('Twilio', @Phone, @Bundle, N'{}');
    SET @CallId = SCOPE_IDENTITY();

    DECLARE @ReadBundle TABLE (TwiML nvarchar(max));
    INSERT INTO @ReadBundle EXEC dbo.automatedcalls_GetTwiML @CallId;
    IF NOT EXISTS (SELECT 1 FROM @ReadBundle WHERE TwiML = @Bundle)
        THROW 51000, 'The read procedure did not return the full stored bundle.', 1;

    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone, @StartedStatus,
        @ExpectedTwiML = @Bundle, @StepId = N'reminder', @ExecutionId = @ExecutionId, @ResponseTwiML = @PreparedResponse;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'started' AND StepId = 'reminder' AND ExecutionId IS NOT NULL AND Replayed = 0)
        THROW 51000, 'Initial start did not issue the opening step.', 1;

    SELECT @ResponseTwiML = ResponseTwiML FROM @Result;
    IF @ResponseTwiML <> @PreparedResponse OR NOT EXISTS (SELECT 1 FROM @Result WHERE ExecutionId = @ExecutionId)
        THROW 51000, 'SQL changed the prepared response or execution ID.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND providerCallId = @Sid AND CallStatus = @StartedStatus AND StartedDateTime IS NOT NULL)
        THROW 51000, 'The call was not bound and marked started.', 1;

    IF (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 1
       OR NOT EXISTS
       (
           SELECT 1 FROM dbo.AutomatedCallEvents
           WHERE CallId = @CallId AND EventType = 'StepIssued' AND IdempotencyKey = 'voice:start'
             AND JSON_VALUE(EventJSON, '$.stepId') = 'reminder'
             AND JSON_VALUE(EventJSON, '$.executionId') = CONVERT(nvarchar(36), @ExecutionId)
             AND JSON_VALUE(EventJSON, '$.responseTwiML') = @ResponseTwiML
       )
        THROW 51000, 'The issued step was not persisted correctly.', 1;

    DECLARE @StartedAt datetime2(3), @UpdatedAt datetime2(3);
    SELECT @StartedAt = StartedDateTime, @UpdatedAt = UpdatedDateTime FROM dbo.AutomatedCalls WHERE SystemID = @CallId;
    UPDATE dbo.AutomatedCalls SET TwiML = N'invalid bundle', CallStatus = @ProtectedStatus WHERE SystemID = @CallId;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone, @StartedStatus;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'started' AND Replayed = 1 AND ExecutionId = @ExecutionId AND ResponseTwiML = @ResponseTwiML)
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 1
       OR NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND StartedDateTime = @StartedAt AND UpdatedDateTime = @UpdatedAt AND CallStatus = @ProtectedStatus)
        THROW 51000, 'Replay changed state, issued another execution, or did not return the saved response.', 1;

    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, 'CAbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', @Phone, @StartedStatus;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'call-sid-mismatch' AND ResponseTwiML IS NULL)
        THROW 51000, 'A conflicting SID was allowed to replay.', 1;

    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, '+15557654321', @StartedStatus;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'destination-mismatch' AND ResponseTwiML IS NULL)
        THROW 51000, 'A conflicting destination was allowed to replay.', 1;

    DECLARE @SavedEventJSON nvarchar(max) = (SELECT EventJSON FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND IdempotencyKey = 'voice:start');
    UPDATE dbo.AutomatedCallEvents SET EventJSON = N'[]' WHERE CallId = @CallId AND IdempotencyKey = 'voice:start';
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone, @StartedStatus,
        @ExpectedTwiML = @Bundle, @StepId = N'reminder', @ExecutionId = @ExecutionId, @ResponseTwiML = @PreparedResponse;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-twiml' AND ResponseTwiML IS NULL)
        THROW 51000, 'A corrupt replay event returned the newly prepared candidate.', 1;
    UPDATE dbo.AutomatedCallEvents SET EventJSON = @SavedEventJSON WHERE CallId = @CallId AND IdempotencyKey = 'voice:start';

    INSERT INTO dbo.AutomatedCalls (Provider, PhoneE164, TwiML) VALUES ('Twilio', @Phone, @Bundle);
    SET @CallId = SCOPE_IDENTITY();
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone, @StartedStatus;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-twiml' AND ResponseTwiML IS NULL)
       OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId)
       OR EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND (providerCallId IS NOT NULL OR StartedDateTime IS NOT NULL))
        THROW 51000, 'A missing prepared response caused a call mutation or step event.', 1;

    DECLARE @StaleBundle nvarchar(max) = REPLACE(@Bundle, N'Hello', N'HELLO');
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone, @StartedStatus,
        @ExpectedTwiML = @StaleBundle, @StepId = N'reminder', @ExecutionId = @ExecutionId, @ResponseTwiML = @PreparedResponse;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'bundle-changed' AND ResponseTwiML IS NULL)
       OR EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId)
       OR EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND (providerCallId IS NOT NULL OR StartedDateTime IS NOT NULL))
        THROW 51000, 'A stale bundle snapshot caused a call mutation or step event.', 1;

    SET @StaleBundle = @Bundle + N' ';
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone, @StartedStatus,
        @ExpectedTwiML = @StaleBundle, @StepId = N'reminder', @ExecutionId = @ExecutionId, @ResponseTwiML = @PreparedResponse;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'bundle-changed' AND ResponseTwiML IS NULL)
        THROW 51000, 'The snapshot check ignored a trailing space.', 1;

    INSERT INTO dbo.AutomatedCalls (Provider, PhoneE164, TwiML, CallStatus)
    VALUES ('Twilio', @Phone, @Bundle, @ProtectedStatus);
    SET @CallId = SCOPE_IDENTITY();
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone, @StartedStatus,
        @ExpectedTwiML = @Bundle, @StepId = N'reminder', @ExecutionId = @ExecutionId, @ResponseTwiML = @PreparedResponse;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND CallStatus = @ProtectedStatus)
        THROW 51000, 'Start overwrote a non-pending status.', 1;

    INSERT INTO dbo.AutomatedCalls (Provider, PhoneE164, TwiML, CallStatus)
    VALUES ('Twilio', @Phone, @Bundle, @ProtectedStatus);
    SET @CallId = SCOPE_IDENTITY();
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone, @StartedStatus, @ProtectedStatus,
        @ExpectedTwiML = @Bundle, @StepId = N'reminder', @ExecutionId = @ExecutionId, @ResponseTwiML = @PreparedResponse;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND CallStatus = @StartedStatus)
        THROW 51000, 'Start did not transition the configured pending status.', 1;

    ROLLBACK TRANSACTION;
    PRINT 'automatedcalls_Start SQL regression checks passed; test data rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;