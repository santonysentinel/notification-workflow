-- Run in SQLCMD mode from the repository root.
SET NOCOUNT ON;
IF DB_NAME() <> N'NotificationWorkflowVoiceTests'
    THROW 51000, 'Run officer profile tests only in the owned scratch database; fixtures are not removed.', 1;
GO
CREATE TABLE dbo.Profiles(ProfileID int IDENTITY PRIMARY KEY, ProfileName varchar(100), CreatedBy varchar(100),
    ModifiedBy varchar(100), CreatedTime datetime, ModifiedTime datetime, DeletedTime datetime);
CREATE TABLE dbo.POGroupNumProfiles(ProfileID int, POGroupNum varchar(20), CreatedBy varchar(100),
    ModifiedBy varchar(100), CreatedTime datetime, DeletedTime datetime);
CREATE TABLE dbo.ProfileItems(ProfileID int, EventCode varchar(10), StartTime time, EndTime time, [Day] int, [Action] int,
    HoldDuration int, GracePeriod int, Instruction varchar(1500), CreatedBy varchar(100), ModifiedBy varchar(100),
    CreatedDate datetime, ModifiedDate datetime, DeletedDate datetime, EmailAddress varchar(640), EmailJoin tinyint,
    StateNo tinyint, StateTime int, IsLastState bit, Recursive bit, FeedbackRequired bit, ProfileType int,
    TimeIntervalsID int, NextState int, StateName varchar(150), NextStateName varchar(150),
    LoopStartState int, NumberOfLoops int, RoleID int, RoleAction varchar(32), OptionsJSON nvarchar(max), VersionHistory int);
CREATE TABLE dbo.POGroupProfileIntervals(ProfileTimeIntervalID int, IntervalID int, StartTime time, EndTime time);
CREATE TABLE dbo.Roles(SystemID int PRIMARY KEY, RoleName varchar(100));
CREATE TABLE dbo.Alarms(AlarmID varchar(10) PRIMARY KEY, BoldDescription varchar(100));
GO
CREATE FUNCTION dbo.dash_validateUser(@session varchar(32))
RETURNS TABLE AS RETURN
    SELECT CAST('UTC' AS varchar(5)) AS UserTZ, 'test' AS UserID, 'test' AS UserName, 'GROUP' AS UserPOGroup, 1 AS WebAccessRight
    WHERE @session = 'test';
GO
CREATE PROCEDURE dbo.dash_UpdateProfileItemSteps AS RETURN;
GO
CREATE PROCEDURE dbo.dash_profile_GetProfileInfo AS RETURN;
GO
CREATE PROCEDURE dbo.dash_UpdateProfileSteps AS RETURN;
GO
CREATE PROCEDURE dbo.dash_profile_GetProfileSteps AS RETURN;
GO
:r database\053.sql
:r database\055.sql
:r database\066.sql
GO
INSERT dbo.POGroupProfileIntervals VALUES(1,1,'00:00','23:59');
INSERT dbo.Profiles(ProfileName) VALUES('Officer profile');
DECLARE @Profile int=SCOPE_IDENTITY();
DECLARE @Doc xml = N'<ROOT>
  <Item EventCode="E" DOW="2" ACT="20" ProfileType="1" TimeInterval="1" StateNo="1" StateTime="2" FeedBackRequired="0"
    OptionsJSON="{&quot;officerRoboCall&quot;:{&quot;FlowId&quot;:12,&quot;Officer&quot;:{&quot;Type&quot;:&quot;Primary&quot;},&quot;PrimaryCallDevice&quot;:{&quot;Type&quot;:&quot;CellPhone&quot;,&quot;PhoneNumber&quot;:null}}}"/>
  <Item EventCode="E" DOW="2" ACT="19" ProfileType="1" TimeInterval="1" StateNo="2" StateTime="2" FeedBackRequired="0"
    OptionsJSON="{&quot;participantRoboCall&quot;:{&quot;FlowId&quot;:12}}"/>
  <Item EventCode="E" DOW="2" ACT="18" ProfileType="1" TimeInterval="1" StateNo="3" StateTime="2" FeedBackRequired="0"
    OptionsJSON="{&quot;unused&quot;:true}"/>
</ROOT>';
EXEC dbo.dash_UpdateProfileSteps 'test', @Profile, 'Officer profile', @Doc;
IF NOT EXISTS (SELECT 1 FROM dbo.ProfileItems WHERE ProfileID=@Profile AND [Action]=20
    AND JSON_VALUE(OptionsJSON,'$.officerRoboCall.FlowId')='12' AND StateTime=2)
    THROW 51000, 'The XML profile save must preserve Action 20 options and step time.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.ProfileItems WHERE ProfileID=@Profile AND [Action]=19
    AND JSON_VALUE(OptionsJSON,'$.participantRoboCall.FlowId')='12')
    THROW 51000, 'Action 19 options must remain unchanged.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.ProfileItems WHERE ProfileID=@Profile AND [Action]=18 AND OptionsJSON IS NULL)
    THROW 51000, 'Other actions must not acquire call options.', 1;
DECLARE @Steps TABLE(ProfileName varchar(100), ProfileID int, EventCode varchar(10), [Day] int, [Action] int,
    Instruction varchar(1500), EmailAddress varchar(640), StateNo int, StateTime int, FeedbackRequired bit,
    ProfileType int, TimeIntervalsID int, NextState int, StateName varchar(150), NextStateName varchar(150),
    LoopStartState int, NumberOfLoops int, RoleID int, RoleAction varchar(32), OptionsJSON nvarchar(max));
INSERT @Steps EXEC dbo.dash_profile_GetProfileSteps 'test', @Profile;
IF NOT EXISTS (SELECT 1 FROM @Steps WHERE [Action]=20 AND JSON_VALUE(OptionsJSON,'$.officerRoboCall.Officer.Type')='Primary')
    THROW 51000, 'The profile editor must read back officer options.', 1;
DECLARE @Info TABLE(ProfileName varchar(100), VersionHistory int, Createddate datetime, EventCode varchar(10),
    BoldDescription varchar(100), StartTime time, EndTime time, DayofWeek varchar(20), DayNumber int,
    [Action] varchar(200), Instruction varchar(1500), Notification varchar(640), StateName varchar(150),
    StateNo int, StateTime int, LoopStartState int, NumberOfLoops int, NextState int, FeedbackRequired bit,
    NextStateName varchar(150), OptionsJSON nvarchar(max));
INSERT @Info EXEC dbo.dash_profile_GetProfileInfo 'test', @Profile;
IF NOT EXISTS (SELECT 1 FROM @Info WHERE [Action]='Officer Robo Call' AND OptionsJSON IS NOT NULL)
    THROW 51000, 'Profile display must label Action 20.', 1;
PRINT 'Officer profile checks passed: XML save, step time, editor read-back, action display and Action 19 regression.';
