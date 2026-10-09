SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.AutomatedCalls') AND name='UX_AutomatedCalls_QueueClaim')
    CREATE UNIQUE INDEX UX_AutomatedCalls_QueueClaim ON dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken)
    WHERE AutomatedCallQueueId IS NOT NULL AND QueueLeaseToken IS NOT NULL;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_BeginAttempt
    @QueueId int, @LeaseToken uniqueidentifier, @WorkerId varchar(100), @PhoneE164 varchar(16)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @LeaseToken IS NULL OR @WorkerId IS NULL OR LEN(LTRIM(RTRIM(@WorkerId)))=0
        OR @PhoneE164 IS NULL OR LEN(@PhoneE164) NOT BETWEEN 3 AND 16 OR LEFT(@PhoneE164,1)<>'+'
        OR SUBSTRING(@PhoneE164,2,1) NOT LIKE '[1-9]' OR SUBSTRING(@PhoneE164,2,15) LIKE '%[^0-9]%'
        THROW 51000,'Valid claim identity and E.164 recipient are required.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Oid varchar(20),@FlowId int,@Status varchar(20),@Token uniqueidentifier,@Owner varchar(100),@Until datetime2(3),@CallId int;
        SELECT @Oid=OID,@FlowId=FlowId,@Status=Status,@Token=LeaseToken,@Owner=LockedBy,@Until=LockedUntil
        FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@QueueId;
        IF @Status IS NULL OR @Status<>'PROCESSING' OR @Token IS NULL OR @Token<>@LeaseToken
            OR @Owner IS NULL OR @Owner COLLATE Latin1_General_100_BIN2<>@WorkerId COLLATE Latin1_General_100_BIN2
            OR @Until IS NULL OR @Until<=SYSUTCDATETIME()
            THROW 51002,'Queue claim is not currently owned by this worker.',1;
        SELECT @CallId=SystemID FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK)
        WHERE AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken;
        IF @CallId IS NULL
        BEGIN
            IF NOT EXISTS(SELECT 1 FROM dbo.CallFlowTemplates WHERE SystemID=@FlowId AND DeletedDateTime IS NULL AND TemplateJSON IS NOT NULL AND TwiML IS NOT NULL)
                THROW 51000,'Published flow data is required for preparation.',1;
            INSERT dbo.AutomatedCalls(AutomatedCallQueueId,QueueLeaseToken,Provider,OID,PhoneE164,FlowId,CallStatus)
            VALUES(@QueueId,@LeaseToken,'Twilio',@Oid,@PhoneE164,@FlowId,'queued');
            SET @CallId=SCOPE_IDENTITY();
        END;
        IF EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@CallId AND
            (Provider<>'Twilio' OR OID<>@Oid OR FlowId<>@FlowId OR PhoneE164 IS NULL))
            THROW 51001,'Existing attempt identity conflicts with the claim.',1;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue claim expired during preparation.',1;
        SELECT a.SystemID AS CallId,a.AutomatedCallQueueId AS QueueId,a.QueueLeaseToken,a.OID,a.PhoneE164,a.FlowId,
            a.TwiML,a.Parameters,t.TemplateJSON AS TemplateJson,t.TwiML AS TemplateTwiML,q.VariablesJSON AS VariablesJson
        FROM dbo.AutomatedCalls a JOIN dbo.AutomatedCallQueue q ON q.SystemID=a.AutomatedCallQueueId
        JOIN dbo.CallFlowTemplates t ON t.SystemID=a.FlowId WHERE a.SystemID=@CallId;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_SavePreparation
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100),@CallId int,@TwiML nvarchar(max),@Parameters nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @TwiML IS NULL OR LEN(@TwiML)=0 OR @Parameters IS NULL OR ISJSON(@Parameters)<>1 OR LEFT(LTRIM(@Parameters),1)<>'{'
        THROW 51000,'Personalized bundle and parameter object are required.',1;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Status varchar(20),@Token uniqueidentifier,@Owner varchar(100),@Until datetime2(3),@ExistingXml nvarchar(max),@ExistingParameters nvarchar(max);
        SELECT @Status=Status,@Token=LeaseToken,@Owner=LockedBy,@Until=LockedUntil
        FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@QueueId;
        IF @Status IS NULL OR @Status<>'PROCESSING' OR @Token IS NULL OR @LeaseToken IS NULL OR @Token<>@LeaseToken
            OR @Owner IS NULL OR @WorkerId IS NULL OR @Owner COLLATE Latin1_General_100_BIN2<>@WorkerId COLLATE Latin1_General_100_BIN2
            OR @Until IS NULL OR @Until<=SYSUTCDATETIME()
            THROW 51002,'Queue claim is not currently owned by this worker.',1;
        IF NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WITH(UPDLOCK,HOLDLOCK) WHERE SystemID=@CallId AND AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken AND Provider='Twilio')
            THROW 51002,'Attempt does not belong to the queue claim.',1;
        SELECT @ExistingXml=TwiML,@ExistingParameters=Parameters FROM dbo.AutomatedCalls WHERE SystemID=@CallId;
        IF @ExistingXml IS NOT NULL OR @ExistingParameters IS NOT NULL
        BEGIN
            IF @ExistingXml IS NULL OR @ExistingParameters IS NULL
                OR DATALENGTH(@ExistingXml)<>DATALENGTH(@TwiML) OR @ExistingXml COLLATE Latin1_General_100_BIN2<>@TwiML COLLATE Latin1_General_100_BIN2
                OR DATALENGTH(@ExistingParameters)<>DATALENGTH(@Parameters) OR @ExistingParameters COLLATE Latin1_General_100_BIN2<>@Parameters COLLATE Latin1_General_100_BIN2
                THROW 51001,'Personalized attempt snapshot is immutable.',1;
        END
        ELSE
        BEGIN
            IF EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE SystemID=@CallId AND (providerCallId IS NOT NULL OR CallStatus<>'queued'))
                THROW 51001,'An already submitted attempt cannot be prepared.',1;
            UPDATE dbo.AutomatedCalls SET TwiML=@TwiML,Parameters=@Parameters,UpdatedDateTime=SYSUTCDATETIME() WHERE SystemID=@CallId;
        END;
        IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue claim expired during preparation.',1;
        COMMIT;
        SELECT @CallId AS CallId;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_FailPreparation
    @QueueId int,@LeaseToken uniqueidentifier,@WorkerId varchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Owned int,@Until datetime2(3);
        SELECT @Owned=SystemID,@Until=LockedUntil FROM dbo.AutomatedCallQueue WITH(UPDLOCK,HOLDLOCK)
        WHERE SystemID=@QueueId AND Status='PROCESSING' AND LeaseToken=@LeaseToken
            AND LockedBy COLLATE Latin1_General_100_BIN2=@WorkerId COLLATE Latin1_General_100_BIN2 AND LockedUntil>SYSUTCDATETIME();
        IF @Owned IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.AutomatedCalls WHERE AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken AND
            (providerCallId IS NOT NULL OR (TwiML IS NOT NULL AND Parameters IS NOT NULL)))
        BEGIN
            UPDATE dbo.AutomatedCalls SET CallStatus='failed',UpdatedDateTime=SYSUTCDATETIME()
            WHERE AutomatedCallQueueId=@QueueId AND QueueLeaseToken=@LeaseToken AND providerCallId IS NULL;
            UPDATE dbo.AutomatedCallQueue SET Status='FAILED',FailedAt=SYSUTCDATETIME(),UpdatedAt=SYSUTCDATETIME(),
                LastErrorCode='PREPARATION_FAILED',LastErrorMessage=N'Attempt preparation failed.',LockedBy=NULL,LockedAt=NULL,LockedUntil=NULL,LeaseToken=NULL
            WHERE SystemID=@QueueId;
            IF @Until<=SYSUTCDATETIME() THROW 51002,'Queue claim expired during failure disposition.',1;
        END;
        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK;
        THROW;
    END CATCH;
END;
GO