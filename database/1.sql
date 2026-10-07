IF OBJECT_ID(N'dbo.CallFlowTemplates', N'U') IS NULL
CREATE TABLE dbo.CallFlowTemplates
(
    SystemID int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    Name varchar(200) NULL,
    Version int NULL,
    POGroupNum int NULL,
    TemplateJSON nvarchar(max) NULL,
    CreatedDateTime datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
    UpdatedDateTime datetime2(3) NULL,
    DeletedDateTime datetime2(3) NULL,
    CreatedBy nvarchar(200) NOT NULL,
    UpdatedBy nvarchar(200) NULL,
    DeletedBy nvarchar(200) NULL
);

IF OBJECT_ID(N'dbo.AutomatedCallQueue', N'U') IS NULL
CREATE TABLE dbo.AutomatedCallQueue
(
    SystemID int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    OID varchar(20) NULL,
    ActiveAlarmID int NOT NULL,
    FlowId int NULL,
    CallStatus int NULL,
    CreatedDateTime datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
    StartedDateTime datetime2(3) NULL,
    UpdatedDateTime datetime2(3) NULL,
    LastMessage varchar(max) NULL,
    CONSTRAINT FK_AutomatedCallQueue_OID FOREIGN KEY (OID) REFERENCES dbo.Client (OID),
    CONSTRAINT FK_AutomatedCallQueue_FlowId FOREIGN KEY (FlowId) REFERENCES dbo.CallFlowTemplates (SystemID),
    CONSTRAINT FK_AutomatedCallQueue_CallStatus FOREIGN KEY (CallStatus) REFERENCES dbo.LookUpFields (LookUpId)
);

IF OBJECT_ID(N'dbo.AutomatedCalls', N'U') IS NULL
CREATE TABLE dbo.AutomatedCalls
(
    SystemID int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    AutomatedCallQueueId int NULL,
    Provider varchar(100) NULL,
    providerCallId varchar(200) NULL,
    OID varchar(20) NULL,
    PhoneE164 varchar(16) NULL,
    FlowId int NULL,
    CallStatus int NULL,
    CreatedDateTime datetime2(3) NOT NULL DEFAULT (SYSUTCDATETIME()),
    StartedDateTime datetime2(3) NULL,
    UpdatedDateTime datetime2(3) NULL,
    CONSTRAINT FK_AutomatedCalls_AutomatedCallQueueId FOREIGN KEY (AutomatedCallQueueId) REFERENCES dbo.AutomatedCallQueue (SystemID),
    CONSTRAINT FK_AutomatedCalls_OID FOREIGN KEY (OID) REFERENCES dbo.Client (OID),
    CONSTRAINT FK_AutomatedCalls_FlowId FOREIGN KEY (FlowId) REFERENCES dbo.CallFlowTemplates (SystemID),
    CONSTRAINT FK_AutomatedCalls_CallStatus FOREIGN KEY (CallStatus) REFERENCES dbo.LookUpFields (LookUpId)
);

IF OBJECT_ID(N'dbo.AutomatedCallEvents', N'U') IS NULL
CREATE TABLE dbo.AutomatedCallEvents
(
    SystemID int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    CallId int NULL,
    CallEventDateTime datetime2(3) NULL,
    EventType varchar(100) NULL,
    EventJSON nvarchar(max) NULL,
    IdempotencyKey varchar(255) NULL,
    CONSTRAINT FK_AutomatedCallEvents_CallId FOREIGN KEY (CallId) REFERENCES dbo.AutomatedCalls (SystemID)
);



IF OBJECT_ID(N'dbo.AutomatedCallTranscripts', N'U') IS NULL
CREATE TABLE dbo.AutomatedCallTranscripts
(
    SystemID int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    Provider nvarchar(100) NULL,
    ProviderModel nvarchar(200) NULL,
    CallId int NULL,
    Sentence int NULL,
    StartTime int NULL,
    EndTime int NULL,
    TranscriptText nvarchar(max) NULL,
    Confidence decimal(6,5) NULL,
    AudioChannel int NULL,
    Language nvarchar(35) NULL,
    WordsJSON nvarchar(max) NULL,
    CONSTRAINT FK_AutomatedCallTranscripts_CallId FOREIGN KEY (CallId) REFERENCES dbo.AutomatedCalls (SystemID)
);