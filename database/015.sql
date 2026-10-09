SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
IF COL_LENGTH('dbo.AutomatedCalls','SubmissionState') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD SubmissionState varchar(20) NULL CONSTRAINT DF_AutomatedCalls_SubmissionState DEFAULT('READY');
IF COL_LENGTH('dbo.AutomatedCalls','SubmissionToken') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD SubmissionToken uniqueidentifier NULL;
IF COL_LENGTH('dbo.AutomatedCalls','SubmissionStartedAt') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD SubmissionStartedAt datetime2(3) NULL;
IF COL_LENGTH('dbo.AutomatedCalls','SubmissionCompletedAt') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD SubmissionCompletedAt datetime2(3) NULL;
IF COL_LENGTH('dbo.AutomatedCalls','SubmissionErrorCode') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD SubmissionErrorCode varchar(100) NULL;
IF COL_LENGTH('dbo.AutomatedCalls','SubmissionRequestJSON') IS NULL
    ALTER TABLE dbo.AutomatedCalls ADD SubmissionRequestJSON nvarchar(max) NULL;
GO
IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.AutomatedCalls') AND name='CK_AutomatedCalls_SubmissionState')
    ALTER TABLE dbo.AutomatedCalls ADD CONSTRAINT CK_AutomatedCalls_SubmissionState CHECK
        (SubmissionState IS NULL OR SubmissionState COLLATE Latin1_General_100_BIN2 IN('READY','SUBMITTING','ACCEPTED','REJECTED','UNKNOWN'));
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_BeginSubmission
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100),@CallId int,
    @SubmissionToken uniqueidentifier,@RequestJSON nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @SubmissionToken IS NULL OR @SubmissionToken='00000000-0000-0000-0000-000000000000'
        OR @RequestJSON IS NULL OR ISJSON(@RequestJSON)<>1 OR LEFT(LTRIM(@RequestJSON),1)<>'{'
        THROW 51000,'A submission token and request snapshot are required.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Status varchar(20),@QueueToken uniqueidentifier,@Owner varchar(100),@Until datetime2(3),
            @Found bit=0,@State varchar(20),@Sid varchar(200),@Phone varchar(16),@Xml nvarchar(max),@Parameters nvarchar(max),
            @CallStatus varchar(20),@Token uniqueidentifier,@ShouldSubmit bit=0;
        SELECT @Found=1,@State=SubmissionState,@Token=SubmissionToken,@Sid=providerCallId,@Phone=PhoneE164,
            @Xml=TwiML,@Parameters=Parameters,@CallStatus=CallStatus
        FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@CallId AND AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken AND Provider='Twilio';
        IF @Found=0 THROW 51002,'Attempt does not belong to the claim.',1;
        SELECT @Status=Status,@QueueToken=LeaseToken,@Owner=LockedBy,@Until=LockedUntil
        FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@QueueId;
        IF @Sid IS NOT NULL AND (@State IS NULL OR @State IN('READY','SUBMITTING'))
        BEGIN
            UPDATE dbo.AutomatedCalls SET SubmissionState='ACCEPTED',SubmissionCompletedAt=COALESCE(SubmissionCompletedAt,SYSUTCDATETIME()) WHERE SystemID=@CallId;
            SET @State='ACCEPTED';
        END;
        IF @State='READY' AND @Sid IS NULL
        BEGIN
            IF @Status IS NULL OR @Status<>'PROCESSING' OR @QueueToken IS NULL OR @QueueToken<>@LeaseToken
                OR @Owner IS NULL OR @WorkerId IS NULL OR @Owner COLLATE Latin1_General_100_BIN2<>@WorkerId COLLATE Latin1_General_100_BIN2
                OR @Until IS NULL OR @Until<=SYSUTCDATETIME()
                THROW 51002,'Submission requires current queue lease ownership.',1;
            IF @Xml IS NULL OR @Parameters IS NULL OR @CallStatus IS NULL OR @CallStatus<>'queued'
                THROW 51000,'Only a persisted prepared attempt may be submitted.',1;
            IF TRY_CONVERT(int,JSON_VALUE(@RequestJSON,'$.CallId')) IS NULL OR TRY_CONVERT(int,JSON_VALUE(@RequestJSON,'$.CallId'))<>@CallId
                OR JSON_VALUE(@RequestJSON,'$.To') IS NULL OR JSON_VALUE(@RequestJSON,'$.To')<>@Phone
                THROW 51000,'Submission request identity does not match the attempt.',1;
            UPDATE dbo.AutomatedCalls SET SubmissionState='SUBMITTING',SubmissionToken=@SubmissionToken,
                SubmissionStartedAt=SYSUTCDATETIME(),SubmissionRequestJSON=@RequestJSON,SubmissionErrorCode=NULL WHERE SystemID=@CallId;
            SET @State='SUBMITTING'; SET @Token=@SubmissionToken; SET @ShouldSubmit=1;
            IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue lease expired before submission.',1;
        END;
        COMMIT;
        SELECT @ShouldSubmit AS ShouldSubmit,COALESCE(@State,'LEGACY') AS SubmissionState,@Token AS SubmissionToken,@Sid AS ProviderCallId;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_SaveSubmission
    @QueueId int,@LeaseToken uniqueidentifier,@CallId int,@SubmissionToken uniqueidentifier,
    @SubmissionState varchar(20),@ProviderCallId varchar(200)=NULL,@ErrorCode varchar(100)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @SubmissionState IS NULL OR @SubmissionState COLLATE Latin1_General_100_BIN2 NOT IN('ACCEPTED','REJECTED','UNKNOWN')
        OR (@SubmissionState='ACCEPTED' AND (@ProviderCallId IS NULL OR LEN(@ProviderCallId)<>34 OR LEFT(@ProviderCallId,2) COLLATE Latin1_General_100_BIN2<>'CA'
            OR SUBSTRING(@ProviderCallId,3,32) COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-fA-F]%'))
        THROW 51000,'A valid submission outcome and accepted SID are required.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Found bit=0,@State varchar(20),@Token uniqueidentifier,@Sid varchar(200);
        SELECT @Found=1,@State=SubmissionState,@Token=SubmissionToken,@Sid=providerCallId
        FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@CallId AND AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken AND Provider='Twilio';
        IF @Found=0 OR @Token IS NULL OR @SubmissionToken IS NULL OR @Token<>@SubmissionToken
            THROW 51002,'Submission response does not belong to the attempt.',1;
        IF @SubmissionState='ACCEPTED' AND @Sid IS NOT NULL AND @Sid COLLATE Latin1_General_100_BIN2<>@ProviderCallId COLLATE Latin1_General_100_BIN2
        BEGIN
            UPDATE dbo.AutomatedCalls SET SubmissionState='UNKNOWN',SubmissionErrorCode='SID_CONFLICT',SubmissionCompletedAt=COALESCE(SubmissionCompletedAt,SYSUTCDATETIME()) WHERE SystemID=@CallId;
        END
        ELSE IF @State<>'ACCEPTED'
        BEGIN
            IF @Sid IS NOT NULL SET @SubmissionState='ACCEPTED';
            UPDATE dbo.AutomatedCalls SET providerCallId=CASE WHEN @SubmissionState='ACCEPTED' THEN COALESCE(providerCallId,@ProviderCallId) ELSE providerCallId END,
                SubmissionState=@SubmissionState,SubmissionCompletedAt=COALESCE(SubmissionCompletedAt,SYSUTCDATETIME()),
                SubmissionErrorCode=CASE WHEN @SubmissionState='ACCEPTED' THEN NULL ELSE @ErrorCode END WHERE SystemID=@CallId;
        END;
        COMMIT;
        SELECT CAST(0 AS bit) AS ShouldSubmit,SubmissionState,SubmissionToken,providerCallId AS ProviderCallId FROM dbo.AutomatedCalls WHERE SystemID=@CallId;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO