SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetNextContext
    @CallId int,
    @ExecutionId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT calls.TwiML, templates.TemplateJSON, source.EventJSON AS SourceEventJSON
    FROM dbo.AutomatedCalls AS calls
    LEFT JOIN dbo.CallFlowTemplates AS templates ON templates.SystemID = calls.FlowId
    OUTER APPLY
    (
        SELECT TOP (1) events.EventJSON
        FROM dbo.AutomatedCallEvents AS events
        WHERE events.CallId = calls.SystemID AND events.EventType = 'StepIssued'
          AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(
              CASE WHEN ISJSON(events.EventJSON) = 1 THEN events.EventJSON ELSE N'{}' END,
              '$.executionId')) = @ExecutionId
        ORDER BY events.SystemID DESC
    ) AS source
    WHERE calls.SystemID = @CallId;
END;
GO

CREATE OR ALTER PROCEDURE dbo.automatedcalls_Next
    @CallId int,
    @ExecutionId uniqueidentifier,
    @ProviderCallId varchar(200),
    @PhoneE164 varchar(16),
    @InputJSON nvarchar(max),
    @InputType varchar(20) = NULL,
    @ExpectedTwiML nvarchar(max) = NULL,
    @ExpectedTemplateJSON nvarchar(max) = NULL,
    @SourceStepId nvarchar(200) = NULL,
    @StepId nvarchar(200) = NULL,
    @NextExecutionId uniqueidentifier = NULL,
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
                @TemplateJSON nvarchar(max),
                @ActualSourceStepId nvarchar(200),
                @SourceEventId int,
                @SourceCount int,
                @EventJSON nvarchar(max),
                @EventType varchar(100),
                @EventFound bit = 0,
                @Replayed bit = 0,
                @Outcome varchar(30) = 'advanced',
                @ExecutionKey varchar(36) = LOWER(CONVERT(varchar(36), @ExecutionId));

        SELECT @Found = 1,
               @Provider = calls.Provider,
               @ExistingCallId = calls.providerCallId,
               @ExistingPhone = calls.PhoneE164,
               @TwiML = calls.TwiML,
               @TemplateJSON = templates.TemplateJSON
        FROM dbo.AutomatedCalls AS calls WITH (UPDLOCK, HOLDLOCK)
        LEFT JOIN dbo.CallFlowTemplates AS templates WITH (HOLDLOCK) ON templates.SystemID = calls.FlowId
        WHERE calls.SystemID = @CallId;

        IF @Found = 0
            SET @Outcome = 'not-found';
        ELSE IF @Provider IS NULL OR LOWER(@Provider) <> 'twilio'
            SET @Outcome = 'provider-mismatch';
        ELSE IF @ExistingPhone IS NULL OR @ExistingPhone <> @PhoneE164
            SET @Outcome = 'destination-mismatch';
        ELSE IF @ExistingCallId IS NULL
             OR @ExistingCallId COLLATE Latin1_General_100_BIN2 <> @ProviderCallId COLLATE Latin1_General_100_BIN2
            SET @Outcome = 'call-sid-mismatch';

        IF @Outcome = 'advanced'
        BEGIN
            SELECT @SourceCount = COUNT(*),
                   @SourceEventId = MAX(SystemID),
                   @ActualSourceStepId = MAX(JSON_VALUE(
                       CASE WHEN ISJSON(EventJSON) = 1 THEN EventJSON ELSE N'{}' END, '$.stepId'))
            FROM dbo.AutomatedCallEvents
            WHERE CallId = @CallId AND EventType = 'StepIssued'
              AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(
                  CASE WHEN ISJSON(EventJSON) = 1 THEN EventJSON ELSE N'{}' END,
                  '$.executionId')) = @ExecutionId;

            IF @SourceCount = 0
                SET @Outcome = 'execution-not-found';
            ELSE IF @SourceCount <> 1 OR NULLIF(LTRIM(RTRIM(@ActualSourceStepId)), '') IS NULL
                SET @Outcome = 'invalid-flow';
        END;

        IF @Outcome = 'advanced'
        BEGIN
            SELECT @EventFound = 1, @EventJSON = EventJSON, @EventType = EventType
            FROM dbo.AutomatedCallEvents
            WHERE CallId = @CallId AND IdempotencyKey = 'voice:next:' + @ExecutionKey;

            IF @EventFound = 1
            BEGIN
                IF @EventJSON IS NULL OR ISJSON(@EventJSON) <> 1 OR @EventType IS NULL OR @EventType <> 'StepTransitioned'
                    SET @Outcome = 'invalid-flow';
                ELSE
                BEGIN
                    SET @StepId = NULL;
                    SET @NextExecutionId = NULL;
                    SET @ResponseTwiML = NULL;
                    SELECT @StepId = stepId,
                           @NextExecutionId = TRY_CONVERT(uniqueidentifier, executionId),
                           @ResponseTwiML = responseTwiML
                    FROM OPENJSON(@EventJSON)
                    WITH (stepId nvarchar(200), executionId nvarchar(36), responseTwiML nvarchar(max));
                    IF @NextExecutionId IS NULL OR NULLIF(LTRIM(RTRIM(@StepId)), '') IS NULL
                       OR NULLIF(LTRIM(RTRIM(@ResponseTwiML)), '') IS NULL
                        SET @Outcome = 'invalid-flow';
                    ELSE
                        SET @Replayed = 1;
                END;
            END
            ELSE IF @SourceEventId <> (SELECT MAX(SystemID) FROM dbo.AutomatedCallEvents WHERE CallId = @CallId AND EventType = 'StepIssued')
                SET @Outcome = 'stale-execution';
            ELSE IF @NextExecutionId IS NULL OR @NextExecutionId = @ExecutionId
                 OR NULLIF(LTRIM(RTRIM(@StepId)), '') IS NULL
                 OR NULLIF(LTRIM(RTRIM(@ResponseTwiML)), '') IS NULL
                 OR @ExpectedTwiML IS NULL OR @ExpectedTemplateJSON IS NULL
                 OR @TwiML IS NULL OR @TemplateJSON IS NULL
                 OR @SourceStepId IS NULL OR @InputJSON IS NULL OR ISJSON(@InputJSON) <> 1
                 OR @InputType IS NULL OR @InputType NOT IN ('redirect', 'dtmf', 'speech', 'no-input')
                SET @Outcome = 'invalid-flow';
            ELSE IF DATALENGTH(@TwiML) <> DATALENGTH(@ExpectedTwiML)
                 OR @TwiML COLLATE Latin1_General_100_BIN2 <> @ExpectedTwiML COLLATE Latin1_General_100_BIN2
                 OR DATALENGTH(@TemplateJSON) <> DATALENGTH(@ExpectedTemplateJSON)
                 OR @TemplateJSON COLLATE Latin1_General_100_BIN2 <> @ExpectedTemplateJSON COLLATE Latin1_General_100_BIN2
                 OR DATALENGTH(@ActualSourceStepId) <> DATALENGTH(@SourceStepId)
                 OR @ActualSourceStepId COLLATE Latin1_General_100_BIN2 <> @SourceStepId COLLATE Latin1_General_100_BIN2
                SET @Outcome = 'context-changed';
            ELSE IF EXISTS
            (
                SELECT 1 FROM dbo.AutomatedCallEvents
                WHERE CallId = @CallId AND EventType = 'StepIssued'
                  AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(
                      CASE WHEN ISJSON(EventJSON) = 1 THEN EventJSON ELSE N'{}' END,
                      '$.executionId')) = @NextExecutionId
            )
                SET @Outcome = 'context-changed';
        END;

        IF @Outcome = 'advanced' AND @Replayed = 0
        BEGIN
            SET @EventJSON = (
                SELECT @ActualSourceStepId AS stepId,
                       CONVERT(varchar(36), @ExecutionId) AS executionId,
                       @ProviderCallId AS providerCallId,
                       @InputType AS inputType,
                       JSON_QUERY(@InputJSON) AS input
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            );
            INSERT INTO dbo.AutomatedCallEvents (CallId, CallEventDateTime, EventType, EventJSON, IdempotencyKey)
            VALUES (@CallId, SYSUTCDATETIME(), 'InputReceived', @EventJSON, 'voice:input:' + @ExecutionKey);

            SET @EventJSON = (
                SELECT @ActualSourceStepId AS fromStepId,
                       CONVERT(varchar(36), @ExecutionId) AS fromExecutionId,
                       @StepId AS stepId,
                       CONVERT(varchar(36), @NextExecutionId) AS executionId,
                       @ProviderCallId AS providerCallId,
                       @InputType AS inputType,
                       @ResponseTwiML AS responseTwiML
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            );
            INSERT INTO dbo.AutomatedCallEvents (CallId, CallEventDateTime, EventType, EventJSON, IdempotencyKey)
            VALUES (@CallId, SYSUTCDATETIME(), 'StepTransitioned', @EventJSON, 'voice:next:' + @ExecutionKey);

            SET @EventJSON = (
                SELECT @StepId AS stepId,
                       CONVERT(varchar(36), @NextExecutionId) AS executionId,
                       CONVERT(varchar(36), @ExecutionId) AS sourceExecutionId,
                       @ProviderCallId AS providerCallId,
                       @ResponseTwiML AS responseTwiML
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            );
            INSERT INTO dbo.AutomatedCallEvents (CallId, CallEventDateTime, EventType, EventJSON, IdempotencyKey)
            VALUES (@CallId, SYSUTCDATETIME(), 'StepIssued', @EventJSON, 'voice:step:' + LOWER(CONVERT(varchar(36), @NextExecutionId)));

            UPDATE dbo.AutomatedCalls SET UpdatedDateTime = SYSUTCDATETIME() WHERE SystemID = @CallId;
        END;

        COMMIT TRANSACTION;
        SELECT @Outcome AS Outcome,
               CASE WHEN @Outcome = 'advanced' THEN @ResponseTwiML END AS ResponseTwiML,
               CASE WHEN @Outcome = 'advanced' THEN @StepId END AS StepId,
               CASE WHEN @Outcome = 'advanced' THEN @NextExecutionId END AS ExecutionId,
               @Replayed AS Replayed;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO