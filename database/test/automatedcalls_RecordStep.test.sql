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
    DECLARE @FlowId int, @CallId int,
            @Sid varchar(200) = 'CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            @Phone varchar(16) = '+15551234567',
            @SourceId uniqueidentifier = NEWID(),
            @NextId uniqueidentifier = NEWID(),
            @Bundle nvarchar(max) = N'<CallFlow initialStep="message"><Step id="message"><Response><Say>Leave a message.</Say><Record/><Redirect/></Response></Step><Step id="thanks"><Response><Hangup/></Response></Step></CallFlow>',
            @Graph nvarchar(max) = N'{"schemaVersion":1,"initialStep":"message","steps":[{"id":"message","type":"record","transitions":{"recorded":"thanks","noRecording":"thanks"}},{"id":"thanks","type":"terminal"}]}',
            @Response nvarchar(max) = N'<Response><Hangup/></Response>',
            @Recording nvarchar(max) = N'{"digits":"hangup","speechResult":"","confidence":null,"recordingSid":"REdddddddddddddddddddddddddddddddd","recordingDurationSeconds":7}';

    INSERT dbo.CallFlowTemplates(Name, CreatedBy) VALUES('Record step regression', 'isolated-test');
    SET @FlowId = SCOPE_IDENTITY();
    INSERT dbo.CallFlowTemplateVersions(CallFlowTemplateID, Version, TemplateJSON, TwiML, CreatedBy) VALUES(@FlowId, 1, @Graph, @Bundle, 'isolated-test');
    DECLARE @FlowIdVersion int = SCOPE_IDENTITY();
    INSERT INTO dbo.AutomatedCalls (Provider, PhoneE164, FlowId, FlowVersionId, TwiML)
    VALUES ('Twilio', @Phone, @FlowId, @FlowIdVersion, @Bundle);
    SET @CallId = SCOPE_IDENTITY();
    DECLARE @Result TABLE (Outcome varchar(30), ResponseTwiML nvarchar(max), StepId nvarchar(200), ExecutionId uniqueidentifier, Replayed bit);
    INSERT INTO @Result EXEC dbo.automatedcalls_Start @CallId, @Sid, @Phone,
        @ExpectedTwiML = @Bundle, @StepId = N'message', @ExecutionId = @SourceId, @ResponseTwiML = N'<Response><Say>Leave a message.</Say></Response>';
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'started') THROW 51000, 'Start fixture failed.', 1;
    DELETE FROM @Result;

    -- The caller hung up while recording, so the final status arrived before the Record action.
    UPDATE dbo.AutomatedCalls SET CallStatus = 'completed', EndedDateTime = SYSUTCDATETIME() WHERE SystemID = @CallId;

    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, N'{"digits":""}',
        'no-recording', @Bundle, @Graph, N'message', N'thanks', @NextId, @Response;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'call-ended')
        THROW 51000, 'Input other than a recording must still stop at an ended call.', 1;
    DELETE FROM @Result;

    DECLARE @BadSid nvarchar(max) = REPLACE(@Recording, N'"REddd', N'"CAddd');
    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @BadSid,
        'recording', @Bundle, @Graph, N'message', N'thanks', @NextId, @Response;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-flow')
        THROW 51000, 'A recording without a valid RecordingSid was accepted.', 1;
    DELETE FROM @Result;

    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, N'{"digits":""}',
        'recording', @Bundle, @Graph, N'message', N'thanks', @NextId, @Response;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'invalid-flow')
        THROW 51000, 'A recording without a RecordingSid was accepted.', 1;
    DELETE FROM @Result;
    IF EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND EventType = 'InputReceived')
        THROW 51000, 'Rejected input wrote an event.', 1;

    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Recording,
        'recording', @Bundle, @Graph, N'message', N'thanks', @NextId, @Response;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'advanced' AND ExecutionId = @NextId AND Replayed = 0)
        THROW 51000, 'A recording posted after the call ended must still be recorded against its step.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND EventType = 'InputReceived'
                   AND JSON_VALUE(EventJSON, '$.stepId') = 'message' AND JSON_VALUE(EventJSON, '$.inputType') = 'recording'
                   AND JSON_VALUE(EventJSON, '$.input.recordingSid') = 'REdddddddddddddddddddddddddddddddd')
        THROW 51000, 'The input event must link the recording to its step.', 1;
    DELETE FROM @Result;

    INSERT INTO @Result EXEC dbo.automatedcalls_Next @CallId, @SourceId, @Sid, @Phone, @Recording;
    IF NOT EXISTS (SELECT 1 FROM @Result WHERE Outcome = 'advanced' AND ExecutionId = @NextId AND Replayed = 1)
        THROW 51000, 'A retried Record action must replay.', 1;

    ROLLBACK TRANSACTION;
    PRINT 'automatedcalls_RecordStep SQL regression checks passed; test data rolled back.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
