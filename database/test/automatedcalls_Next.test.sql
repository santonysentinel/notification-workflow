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
    DECLARE @FlowId int, @CallId int, @OtherCallId int,
            @Status varchar(20) = 'in-progress',
            @Sid varchar(200) = 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            @Phone varchar(16) = '+15551234567',
            @SourceId uniqueidentifier = NEWID(),
            @NextId uniqueidentifier = NEWID(),
            @LastId uniqueidentifier = NEWID(),
            @Bundle nvarchar(max) = N'<CallFlow initialStep="opening"><Step id="opening"><Response><Say>Hello.</Say></Response></Step><Step id="question"><Response><Gather/></Response></Step><Step id="end"><Response><Hangup/></Response></Step></CallFlow>',
            @Graph nvarchar(max) = N'{"schemaVersion":1,"initialStep":"opening","steps":[{"id":"opening","type":"redirect","transitions":{"next":"question"}},{"id":"question","type":"gather","transitions":{"digits":{"1":"end"},"noInput":"end","fallback":"end"}},{"id":"end","type":"terminal"}]}',
            @Response nvarchar(max) = N'<Response><Gather/></Response>',
            @Input nvarchar(max) = N'{"digits":"","speechResult":"","confidence":null}';

    INSERT INTO dbo.CallFlowTemplates (Name, Version, TemplateJSON, TwiML, CreatedBy)
    VALUES ('Next regression', 1, @Graph, @Bundle, 'isolated-test');
    SET @FlowId = SCOPE_IDENTITY();
    INSERT INTO dbo.AutomatedCalls (Provider, PhoneE164, FlowId, TwiML)
    VALUES ('Twilio', @Phone, @FlowId, @Bundle);
    SET @CallId = SCOPE_IDENTITY();
    DECLARE @Result TABLE (Outcome varchar(30), ResponseTwiML nvarchar(max), StepId nvarchar(200), ExecutionId uniqueidentifier, Replayed bit);

    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone,
        @ExpectedTwiML = @Bundle, @StepId = N'opening', @ExecutionId = @SourceId, @ResponseTwiML = N'<Response><Say>Hello.</Say></Response>';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'started') THROW 51000, 'Start fixture failed.', 1;
    DELETE FROM @Result;

    DECLARE @Context TABLE (TwiML nvarchar(max), TemplateJSON nvarchar(max), SourceEventJSON nvarchar(max));
    INSERT INTO @Context EXEC dbo.automatedcalls_GetNextContext @CallId, @SourceId;
    IF NOT EXISTS (SELECT 1 FROM @Context WHERE TwiML = @Bundle AND TemplateJSON = @Graph AND JSON_VALUE(SourceEventJSON, '$.stepId') = 'opening')
        THROW 51000, 'Next context did not return the complete bundle, graph, and source execution.', 1;

    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Input;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-flow')
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 1
        THROW 51000, 'Missing preparation mutated the call.', 1;
    DELETE FROM @Result;

    DECLARE @StaleGraph nvarchar(max) = @Graph + N' ';
    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Input,
        'redirect', @Bundle, @StaleGraph, N'opening', N'question', @NextId, @Response;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'context-changed')
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 1
        THROW 51000, 'A stale graph caused a transition.', 1;
    DELETE FROM @Result;

    DECLARE @StaleBundle nvarchar(max) = REPLACE(@Bundle, N'Hello', N'HELLO');
    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Input,
        'redirect', @StaleBundle, @Graph, N'opening', N'question', @NextId, @Response;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'context-changed') THROW 51000, 'A stale bundle caused a transition.', 1;
    DELETE FROM @Result;

    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Input,
        'redirect', @Bundle, @Graph, N'opening', N'question', @NextId, @Response;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'advanced' AND ExecutionId = @NextId AND StepId = 'question' AND ResponseTwiML = @Response AND Replayed = 0)
        THROW 51000, 'First next did not return the prepared response.', 1;
    IF (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 4
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND EventType = 'InputReceived') <> 1
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND EventType = 'StepTransitioned') <> 1
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND EventType = 'StepIssued') <> 2
        THROW 51000, 'The transition did not write exactly one input, transition, and issued-step event.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID = @CallId AND CallStatus = @Status AND providerCallId = @Sid AND TwiML = @Bundle)
        THROW 51000, 'Next altered the status, SID, or stored bundle.', 1;
    DELETE FROM @Result;

    DECLARE @UpdatedAt datetime2(3) = (SELECT UpdatedDateTime FROM dbo.AutomatedCalls WHERE SystemID = @CallId);
    UPDATE dbo.AutomatedCalls SET TwiML = N'invalid' WHERE SystemID = @CallId;
    UPDATE dbo.CallFlowTemplates SET TemplateJSON = N'invalid' WHERE SystemID = @FlowId;
    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, N'{"digits":"2"}';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'advanced' AND ExecutionId = @NextId AND ResponseTwiML = @Response AND Replayed = 1)
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 4
       OR (SELECT UpdatedDateTime FROM dbo.AutomatedCalls WHERE SystemID = @CallId) <> @UpdatedAt
        THROW 51000, 'Replay did not preserve the first response, execution, events, and timestamp.', 1;
    UPDATE dbo.AutomatedCalls SET TwiML = @Bundle WHERE SystemID = @CallId;
    UPDATE dbo.CallFlowTemplates SET TemplateJSON = @Graph WHERE SystemID = @FlowId;
    DELETE FROM @Result;

    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, 'CAbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', @Phone, @Input;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'call-sid-mismatch' AND ResponseTwiML IS NULL) THROW 51000, 'Conflicting SID replayed.', 1;
    DELETE FROM @Result;
    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, '+15557654321', @Input;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'destination-mismatch' AND ResponseTwiML IS NULL) THROW 51000, 'Conflicting destination replayed.', 1;
    DELETE FROM @Result;

    INSERT INTO dbo.AutomatedCalls (Provider, providerCallId, PhoneE164, FlowId, TwiML)
    VALUES ('Twilio', @Sid, @Phone, @FlowId, @Bundle);
    SET @OtherCallId = SCOPE_IDENTITY();
    INSERT INTO @Result EXEC dbo.automatedcalls_Next @OtherCallId, @SourceId, @Sid, @Phone, @Input;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'execution-not-found')
        THROW 51000, 'An execution was accepted for another call.', 1;
    DELETE FROM @Result;

    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @NextId, @Sid, @Phone, N'{"digits":"1","speechResult":"","confidence":null}',
        'dtmf', @Bundle, @Graph, N'question', N'end', @LastId, N'<Response><Hangup/></Response>';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'advanced' AND ExecutionId = @LastId)
       OR (SELECT COUNT(*) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId) <> 7
        THROW 51000, 'Second transition failed.', 1;
    DELETE FROM @Result;

    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Input;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'advanced' AND ExecutionId = @NextId AND Replayed = 1)
        THROW 51000, 'A delayed retry did not replay after later progression.', 1;
    DELETE FROM @Result;

    DECLARE @TransitionKey varchar(255) = 'voice:next:' + LOWER(CONVERT(varchar(36), @SourceId));
    DECLARE @SavedEvent nvarchar(max) = (SELECT EventJSON FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND IdempotencyKey = @TransitionKey);
    UPDATE dbo.AutomatedCallEvents SET EventJSON = N'[]' WHERE CallId = @CallId AND IdempotencyKey = @TransitionKey;
    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Input,
        'redirect', @Bundle, @Graph, N'opening', N'question', @NextId, @Response;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-flow' AND ResponseTwiML IS NULL)
        THROW 51000, 'Corrupt replay data returned a fresh candidate.', 1;
    UPDATE dbo.AutomatedCallEvents SET EventJSON = @SavedEvent WHERE CallId = @CallId AND IdempotencyKey = @TransitionKey;
    DELETE FROM @Result;

    UPDATE dbo.AutomatedCallEvents SET EventType = NULL WHERE CallId = @CallId AND IdempotencyKey = @TransitionKey;
    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Input;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-flow' AND ResponseTwiML IS NULL)
        THROW 51000, 'A replay event with a missing type was accepted.', 1;
    UPDATE dbo.AutomatedCallEvents SET EventType = 'StepTransitioned' WHERE CallId = @CallId AND IdempotencyKey = @TransitionKey;
    DELETE FROM @Result;

    DELETE FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND IdempotencyKey = @TransitionKey;
    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Input;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'stale-execution' AND ResponseTwiML IS NULL)
        THROW 51000, 'An unconsumed stale execution advanced the call.', 1;

    ROLLBACK TRANSACTION;
    PRINT 'automatedcalls_Next SQL regression checks passed; test data rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;