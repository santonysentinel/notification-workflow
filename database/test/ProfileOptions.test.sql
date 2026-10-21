SET NOCOUNT ON;
IF DB_NAME() <> N'NotificationWorkflowProfileOptionsTests'
    THROW 51000, 'Run only in the owned empty profile-options scratch database.', 1;
CREATE TABLE dbo.Profiles
(
    ProfileID int IDENTITY PRIMARY KEY, ProfileName varchar(100), CreatedBy varchar(100),
    ModifiedBy varchar(100), CreatedTime datetime, ModifiedTime datetime, DeletedTime datetime
);
CREATE TABLE dbo.POGroupNumProfiles
(
    ProfileID int, POGroupNum varchar(10), CreatedBy varchar(100), ModifiedBy varchar(100),
    CreatedTime datetime, DeletedTime datetime
);
CREATE TABLE dbo.POGroupProfileIntervals
(
    ProfileTimeIntervalID int, IntervalID int, StartTime time, EndTime time
);
-- Starts with the legacy FlowId column so 052.sql's drop is exercised.
CREATE TABLE dbo.ProfileItems
(
    SystemID int IDENTITY PRIMARY KEY, ProfileID int, EventCode varchar(10), StartTime time,
    EndTime time, [Day] smallint, [Action] smallint, HoldDuration int, GracePeriod int,
    Instruction varchar(1500), CreatedBy varchar(100), ModifiedBy varchar(100),
    CreatedDate datetime, ModifiedDate datetime, DeletedDate datetime,
    EmailAddress varchar(640), EmailJoin tinyint, StateNo tinyint, StateTime int,
    IsLastState bit, Recursive bit, FeedbackRequired bit, ProfileType int,
    TimeIntervalsID int, NextState int, StateName varchar(150), NextStateName varchar(150),
    LoopStartState int, NumberOfLoops int, RoleID int, RoleAction varchar(32),
    IsAlarmClearingEnabled bit, FlowId int, VersionHistory int
);
GO
CREATE FUNCTION dbo.dash_validateUser(@session varchar(32))
RETURNS TABLE AS RETURN
    SELECT CAST('UTC' AS varchar(5)) UserTZ, CAST('tester' AS varchar(100)) UserID,
        CAST('group' AS varchar(10)) UserPOGroup, 1 WebAccessRight WHERE @session = 'valid';
GO
CREATE PROCEDURE dbo.dash_UpdateProfileItemSteps AS RETURN;
GO
CREATE PROCEDURE dbo.dash_UpdateProfileSteps AS RETURN;
GO
CREATE PROCEDURE dbo.dash_profile_GetProfileSteps AS RETURN;
GO
CREATE PROCEDURE dbo.ActiveAlarms_ReadProfileItems AS RETURN;
GO
:r .\046.sql
GO
:r .\052.sql
GO
:r .\052.sql
GO
IF COL_LENGTH(N'dbo.ProfileItems', N'FlowId') IS NOT NULL
    THROW 51000, '052.sql must drop ProfileItems.FlowId.', 1;
GO
:r .\053.sql
:r .\054.sql
:r .\055.sql
:r .\056.sql

INSERT dbo.POGroupProfileIntervals VALUES (1, 1, '00:00:00', '23:59:00');
DECLARE @Options nvarchar(max) = N'{"participantRoboCall":{"FlowId":4,"FlowName":"DEV Flow 1","ShowParticipantResponseTranscript":false,"WaitForCallCompletion":false,"CallTimeoutSeconds":null,"PrimaryCallDevice":{"Type":"WorkPhone","PhoneNumber":null},"SecondaryCallDevice":{"Type":"Custom","PhoneNumber":"+19499299458"}}}';
DECLARE @Doc xml = (
    SELECT 'ALARM' AS [@EventCode], 1 AS [@ProfileType], 1 AS [@DOW], 19 AS [@ACT], 'call' AS [@INSTRUCTION],
        1 AS [@TimeInterval], 0 AS [@StateNo], 5 AS [@StateTime], @Options AS [@OptionsJSON]
    FOR XML PATH('Item'), ROOT('ROOT'), TYPE);
EXEC dbo.dash_UpdateProfileSteps 'valid', NULL, 'original profile', @Doc;
DECLARE @ProfileID int = (SELECT ProfileID FROM dbo.Profiles);
IF NOT EXISTS (SELECT 1 FROM dbo.ProfileItems WHERE ProfileID = @ProfileID AND DeletedDate IS NULL AND OptionsJSON = @Options)
    THROW 51000, 'Profile saves must preserve XML-escaped options unchanged.', 1;

CREATE TABLE #Read
(
    ProfileID int, ProfileName varchar(100), EventCode varchar(10), StartTime time, EndTime nvarchar(20),
    [Day] smallint, [Action] smallint, HoldDuration int, GracePeriod int, Instruction varchar(1500),
    EmailAddress varchar(640), EmailJoin tinyint, StateNo tinyint, StateTime int, FeedbackRequired bit,
    ProfileType int, TimeIntervalsID int, NextState int, LoopStartState int, NumberOfLoops int,
    RoleID int, RoleAction varchar(32), IsAlarmClearingEnabled bit, OptionsJSON nvarchar(max)
);
INSERT #Read EXEC dbo.ActiveAlarms_ReadProfileItems;
IF (SELECT COUNT(*) FROM #Read) <> 1 OR NOT EXISTS
    (SELECT 1 FROM #Read WHERE OptionsJSON = @Options AND EndTime = N'23:59:59' AND NextState = -1
        AND JSON_VALUE(OptionsJSON, '$.participantRoboCall.FlowId') = N'4')
    THROW 51000, 'Parser read must return options (carrying FlowId) without the dropped column.', 1;
IF EXISTS (SELECT 1 FROM sys.dm_exec_describe_first_result_set(N'EXEC dbo.ActiveAlarms_ReadProfileItems', NULL, 0)
    WHERE name = N'FlowID')
    THROW 51000, 'Parser read must not return FlowID.', 1;

CREATE TABLE #Editor
(
    ProfileName varchar(100), ProfileID int, EventCode varchar(10), [Day] smallint,
    [Action] smallint, Instruction varchar(1500), EmailAddress varchar(640), StateNo tinyint,
    StateTime int, FeedbackRequired bit, ProfileType int, TimeIntervalsID int, NextState int,
    StateName varchar(150), NextStateName varchar(150), LoopStartState int, NumberOfLoops int,
    RoleID int, RoleAction varchar(32), OptionsJSON nvarchar(max)
);
INSERT #Editor EXEC dbo.dash_profile_GetProfileSteps 'valid', @ProfileID;
IF (SELECT COUNT(*) FROM #Editor) <> 1 OR NOT EXISTS (SELECT 1 FROM #Editor WHERE OptionsJSON = @Options)
    THROW 51000, 'Editor reads must round-trip the saved options.', 1;

EXEC dbo.dash_UpdateProfileItemSteps @ProfileID, 'ALARM', 1, 1, 2, '', 'tester', '', 0,
    3, 5, 0, 0, 0, 1, NULL, '', NULL, NULL, NULL, NULL, NULL, @Options;
IF NOT EXISTS (SELECT 1 FROM dbo.ProfileItems WHERE [Action] = 2 AND OptionsJSON IS NULL)
    THROW 51000, 'Non-call actions must not retain Robo Call options.', 1;
PRINT 'Profile option save and parser-read SQL regression checks passed.';
