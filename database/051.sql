ALTER PROC [dbo].[dash_profile_GetProfileInfo] (
    @session VARCHAR(32),
    @ProfileID INT,
    @Version INT = NULL
)
AS
DECLARE @potz VARCHAR(5), @poname VARCHAR(100), @pogroupnum VARCHAR(10)

SELECT @potz = UserTZ, @poname = UserName, @pogroupnum = UserPOGroup + '%'
FROM dbo.dash_validateUser(@session)

IF (@potz IS NOT NULL)
BEGIN
    DECLARE @CreatedDate DATETIME

    SELECT @CreatedDate = Max(CreatedDate)
    FROM ProfileItems
    WHERE ((@Version IS NOT NULL AND VersionHistory = @Version)
        OR (DeletedDate IS NULL AND @Version IS NULL))
        AND ProfileID = @ProfileID

    SELECT p.ProfileName
        ,pri.VersionHistory
        ,@CreatedDate AS 'Createddate'
        ,pri.EventCode
        ,a.BoldDescription
        ,pri.StartTime
        ,pri.EndTime
        ,CASE
            WHEN (pri.[Day] = 2) THEN 'Monday'
            WHEN (pri.[Day] = 3) THEN 'Tuesday'
            WHEN (pri.[Day] = 4) THEN 'Wednesday'
            WHEN (pri.[Day] = 5) THEN 'Thursday'
            WHEN (pri.[Day] = 6) THEN 'Friday'
            WHEN (pri.[Day] = 7) THEN 'Saturday'
            WHEN (pri.[Day] = 1) THEN 'Sunday'
            END AS [DayofWeek]
        ,pri.[Day] AS DayNumber
        ,CASE
            WHEN (pri.[Action] = 2) THEN 'Auto Email'
            WHEN (pri.[Action] = 3) THEN 'McApp'
            WHEN (pri.[Action] = 7) THEN 'Auto Email and McApp'
            WHEN (pri.[Action] = 1) THEN 'Do Nothing'
            WHEN (pri.[Action] = 5) THEN 'Auto Page'
            WHEN (pri.[Action] = 9) THEN 'Auto Page and McApp'
            WHEN (pri.[Action] = 11) THEN 'Delay'
            WHEN (pri.[Action] = 12) THEN (
                SELECT 'Role Based - ' + CASE
                    WHEN pri.RoleAction = 1 THEN 'Call'
                    WHEN pri.RoleAction = 2 THEN 'E-Mail'
                    WHEN pri.RoleAction = 3 THEN 'Text'
                    END + ' ' + r.RoleName
                )
            WHEN (pri.[Action] = 13) THEN 'Email All Victims'
            WHEN (pri.[Action] = 14) THEN 'Contact Victims'
            WHEN (pri.[Action] = 15) THEN 'Alert Client - Email'
            WHEN (pri.[Action] = 16) THEN 'Alert Client - Text'
            WHEN (pri.[Action] = 17) THEN 'Text All Victims'
            WHEN (pri.[Action] = 18) THEN 'Add Note to History'
            WHEN (pri.[Action] = 19) THEN 'Robo Call Participant'
            ELSE 'Unknown'
            END AS [Action]
        ,Instruction
        ,CASE
            WHEN (pri.[Action] = 5 OR pri.[Action] = 9) THEN 'Notifies the assigned officer'
            WHEN (pri.[Action] = 13) THEN 'Notifies the assigned victims'
            ELSE EmailAddress
            END AS Notification
        ,pri.StateName
        ,pri.StateNo
        ,pri.StateTime
        ,[LoopStartState]
        ,[NumberOfLoops]
        ,[NextState]
        ,FeedbackRequired
        ,NextStateName
        ,pri.OptionsJSON
    FROM Profiles p
    INNER JOIN ProfileItems pri ON pri.ProfileID = p.ProfileID
    LEFT JOIN Roles r ON r.SystemID = pri.RoleID
    LEFT JOIN Alarms a ON a.AlarmID = pri.EventCode
    WHERE ((@Version IS NOT NULL AND pri.VersionHistory = @Version)
        OR (pri.DeletedDate IS NULL AND @Version IS NULL))
        AND p.ProfileID = @ProfileID
    ORDER BY pri.EventCode, [Day], pri.StartTime, pri.EndTime, pri.StateNo
END