SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
-- Tenant database: Action 20 profile persistence and display.
-- Requires 052-056 and the separately supplied officer resolver/phone procedures.
-- No officer schema is assumed and no placeholder procedures are installed.
ALTER PROCEDURE dbo.dash_UpdateProfileItemSteps
    @ProfileID int,
    @EventCode varchar(10),
    @ProfileType int,
    @Day smallint,
    @Action smallint,
    @Instruction varchar(1500),
    @Author varchar(100),
    @EmailAddress varchar(640),
    @EmailJoin tinyint,
    @StateNo tinyint,
    @StateTime int,
    @IsLastState bit,
    @Recursive bit,
    @FeedbackRequired bit,
    @TimeIntervalID int,
    @NextState int,
    @StateName varchar(150),
    @NextStateName varchar(150),
    @LoopStartState int,
    @NumberOfLoops int,
    @RoleID int,
    @RoleAction varchar(32),
    @OptionsJSON nvarchar(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @StartTime time;
    DECLARE @EndTime time;

    SELECT @StartTime = StartTime,
        @EndTime = EndTime
    FROM POGroupProfileIntervals
    WHERE ProfileTimeIntervalID = @TimeIntervalID
        AND IntervalID = @ProfileType;

    INSERT INTO ProfileItems (
        ProfileID,
        EventCode,
        StartTime,
        EndTime,
        [Day],
        [Action],
        HoldDuration,
        GracePeriod,
        Instruction,
        CreatedBy,
        ModifiedBy,
        Createddate,
        ModifiedDate,
        DeletedDate,
        EmailAddress,
        EmailJoin,
        StateNo,
        StateTime,
        IsLastState,
        Recursive,
        FeedbackRequired,
        ProfileType,
        TimeIntervalsID,
        NextState,
        StateName,
        NextStateName,
        LoopStartState,
        NumberOfLoops,
        RoleID,
        RoleAction,
        OptionsJSON
    )
    VALUES (
        @ProfileID,
        @EventCode,
        @StartTime,
        @EndTime,
        @Day,
        @Action,
        0,
        0,
        REPLACE(REPLACE(@Instruction, CHAR(13), ''), CHAR(10), ''),
        @Author,
        @Author,
        GETUTCDATE(),
        GETUTCDATE(),
        NULL,
        REPLACE(REPLACE(@EmailAddress, CHAR(13), ''), CHAR(10), ''),
        @EmailJoin,
        @StateNo,
        @StateTime,
        @IsLastState,
        @Recursive,
        @FeedbackRequired,
        @ProfileType,
        @TimeIntervalID,
        @NextState,
        @StateName,
        CASE WHEN @NextStateName LIKE '' THEN NULL ELSE @NextStateName END,
        @LoopStartState,
        @NumberOfLoops,
        @RoleID,
        @RoleAction,
        CASE WHEN @Action IN (19, 20) THEN @OptionsJSON ELSE NULL END
    );
END;
GO
GO
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
            WHEN (pri.[Action] = 19) THEN 'Participant RoboCall'
            WHEN (pri.[Action] = 20) THEN 'Officer RoboCall'
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