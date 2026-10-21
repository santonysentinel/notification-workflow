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
:r .\046.sql
:r .\047.sql
:r .\048.sql
:r .\049.sql
:r .\050.sql
:r .\050.sql

INSERT dbo.POGroupProfileIntervals VALUES (1, 1, '00:00:00', '23:59:00');
DECLARE @Doc xml = N'<ROOT><Item EventCode="ALARM" ProfileType="1" DOW="1" ACT="19"
    INSTRUCTION="call" TimeInterval="1" StateNo="0" StateTime="5" FlowId="12"
    OptionsJSON="{&quot;postRecordingTranscript&quot;:false}" /></ROOT>';
EXEC dbo.dash_UpdateProfileSteps 'valid', NULL, 'original profile', @Doc;
DECLARE @ProfileID int = (SELECT ProfileID FROM dbo.Profiles);
IF NOT EXISTS (SELECT 1 FROM dbo.ProfileItems WHERE ProfileID = @ProfileID AND DeletedDate IS NULL
    AND FlowId = 12 AND OptionsJSON = N'{"postRecordingTranscript":false}')
    THROW 51000, 'Profile saves must preserve XML-escaped policy and flow identity.', 1;

CREATE TABLE #Read
(
    ProfileID int, ProfileName varchar(100), EventCode varchar(10), StartTime time, EndTime nvarchar(20),
    [Day] smallint, [Action] smallint, HoldDuration int, GracePeriod int, Instruction varchar(1500),
    EmailAddress varchar(640), EmailJoin tinyint, StateNo tinyint, StateTime int, FeedbackRequired bit,
    ProfileType int, TimeIntervalsID int, NextState int, LoopStartState int, NumberOfLoops int,
    RoleID int, RoleAction varchar(32), IsAlarmClearingEnabled bit, FlowID int, OptionsJSON nvarchar(max)
);
INSERT #Read EXEC dbo.ActiveAlarms_ReadProfileItems;
IF (SELECT COUNT(*) FROM #Read) <> 1 OR NOT EXISTS
    (SELECT 1 FROM #Read WHERE OptionsJSON = N'{"postRecordingTranscript":false}'
        AND FlowID = 12 AND EndTime = N'23:59:59' AND NextState = -1)
    THROW 51000, 'Parser read must return policy while preserving existing column semantics.', 1;

CREATE TABLE #Editor
(
    ProfileName varchar(100), ProfileID int, EventCode varchar(10), [Day] smallint,
    [Action] smallint, Instruction varchar(1500), EmailAddress varchar(640), StateNo tinyint,
    StateTime int, FeedbackRequired bit, ProfileType int, TimeIntervalsID int, NextState int,
    StateName varchar(150), NextStateName varchar(150), LoopStartState int, NumberOfLoops int,
    RoleID int, RoleAction varchar(32), FlowId int, OptionsJSON nvarchar(max)
);
INSERT #Editor EXEC dbo.dash_profile_GetProfileSteps 'valid', @ProfileID;
IF (SELECT COUNT(*) FROM #Editor) <> 1 OR NOT EXISTS
    (SELECT 1 FROM #Editor WHERE FlowId = 12 AND OptionsJSON = N'{"postRecordingTranscript":false}')
    THROW 51000, 'Editor reads must round-trip the saved policy.', 1;

DECLARE @Bad TABLE (OptionsJSON nvarchar(max));
INSERT @Bad VALUES (N''), (N'null'), (N'[]'), (N'{broken'), (N'{"historyId":999}'),
    (N'{"postRecordingTranscript":"false"}'), (N'{"postRecordingTranscript":null}'),
    (N'{"PostRecordingTranscript":false}'),
    (N'{"postRecordingTranscript":true,"postRecordingTranscript":false}');
DECLARE @Options nvarchar(max), @BadDoc xml;
DECLARE policies CURSOR LOCAL FAST_FORWARD FOR SELECT OptionsJSON FROM @Bad;
OPEN policies;
FETCH NEXT FROM policies INTO @Options;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @BadDoc = (SELECT 'ALARM' AS [@EventCode], 19 AS [@ACT], @Options AS [@OptionsJSON] FOR XML PATH('Item'), ROOT('ROOT'), TYPE);
    BEGIN TRY
        EXEC dbo.dash_UpdateProfileSteps 'valid', @ProfileID, 'must not replace', @BadDoc;
        THROW 51001, 'Invalid profile policy must fail.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    IF @@TRANCOUNT <> 0 OR (SELECT ProfileName FROM dbo.Profiles WHERE ProfileID = @ProfileID) <> 'original profile'
        OR (SELECT COUNT(*) FROM dbo.ProfileItems) <> 1
        OR EXISTS (SELECT 1 FROM dbo.ProfileItems WHERE DeletedDate IS NOT NULL)
        THROW 51000, 'Rejected policy must preserve the entire existing profile.', 1;
    BEGIN TRY
        EXEC dbo.dash_UpdateProfileItemSteps @ProfileID, 'ALARM', 1, 1, 19, '', 'tester', '', 0,
            0, 5, 0, 0, 0, 1, NULL, '', NULL, NULL, NULL, NULL, NULL, 12, @Options;
        THROW 51001, 'Direct item saves must also reject invalid policy.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;
    FETCH NEXT FROM policies INTO @Options;
END;
CLOSE policies;
DEALLOCATE policies;

-- A later item can fail after the old profile and a first replacement item were written.
ALTER TABLE dbo.ProfileItems ADD CONSTRAINT CK_TestStorage CHECK (Instruction <> 'fail-store');
SET @BadDoc = N'<ROOT>
    <Item EventCode="ALARM" ACT="19" INSTRUCTION="first" OptionsJSON="{}" />
    <Item EventCode="ALARM" ACT="19" INSTRUCTION="fail-store" OptionsJSON="{}" />
    </ROOT>';
BEGIN TRY
    EXEC dbo.dash_UpdateProfileSteps 'valid', @ProfileID, 'must roll back', @BadDoc;
    THROW 51001, 'Later storage failure must roll back replacement.', 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() <> 547 THROW;
END CATCH;
IF @@TRANCOUNT <> 0 OR (SELECT COUNT(*) FROM dbo.ProfileItems) <> 1
    OR EXISTS (SELECT 1 FROM dbo.ProfileItems WHERE DeletedDate IS NOT NULL)
    OR (SELECT ProfileName FROM dbo.Profiles WHERE ProfileID = @ProfileID) <> 'original profile'
    THROW 51000, 'Storage failure must roll back all profile changes.', 1;
ALTER TABLE dbo.ProfileItems DROP CONSTRAINT CK_TestStorage;

-- A retry in the same SQL session must not retain the failed save's cursor/XML state.
EXEC dbo.dash_UpdateProfileSteps 'valid', @ProfileID, 'retry succeeded', @Doc;
IF (SELECT COUNT(*) FROM dbo.ProfileItems WHERE DeletedDate IS NULL) <> 1
    THROW 51000, 'Profile retries must replace exactly the active items.', 1;
EXEC dbo.dash_UpdateProfileItemSteps @ProfileID, 'ALARM', 1, 1, 19, '', 'tester', '', 0,
    1, 5, 0, 0, 0, 1, NULL, '', NULL, NULL, NULL, NULL, NULL, 12, NULL;
EXEC dbo.dash_UpdateProfileItemSteps @ProfileID, 'ALARM', 1, 1, 19, '', 'tester', '', 0,
    2, 5, 0, 0, 0, 1, NULL, '', NULL, NULL, NULL, NULL, NULL, 12, N'{}';
EXEC dbo.dash_UpdateProfileItemSteps @ProfileID, 'ALARM', 1, 1, 2, '', 'tester', '', 0,
    3, 5, 0, 0, 0, 1, NULL, '', NULL, NULL, NULL, NULL, NULL, 12, N'not-json';
IF NOT EXISTS (SELECT 1 FROM dbo.ProfileItems WHERE [Action] = 2 AND OptionsJSON IS NULL AND FlowId IS NULL)
    THROW 51000, 'Non-call actions must not retain call policy.', 1;
IF EXISTS (SELECT 1 FROM sys.dm_exec_xml_handles(@@SPID))
    THROW 51000, 'Profile saves must release XML handles on success and failure.', 1;
PRINT 'Profile option save and parser-read SQL regression checks passed.';
