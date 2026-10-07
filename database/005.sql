CREATE OR ALTER PROCEDURE dbo.automatedcalls_Start
    @CallId int,
    @ProviderCallId varchar(200),
    @PhoneE164 varchar(16),
    @StartedCallStatus int,
    @PendingCallStatus int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        DECLARE @Found bit = 0,
                @Provider varchar(100),
                @ExistingCallId varchar(200),
                @ExistingPhone varchar(16),
                @TwiML nvarchar(max),
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
        ELSE IF @TwiML IS NULL OR LEN(LTRIM(RTRIM(@TwiML))) = 0
             OR TRY_CONVERT(xml, @TwiML) IS NULL
            SET @Outcome = 'invalid-twiml';

        IF @Outcome = 'started'
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
        END;

        COMMIT TRANSACTION;

        SELECT @Outcome AS Outcome,
               CASE WHEN @Outcome = 'started' THEN @TwiML END AS TwiML;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;