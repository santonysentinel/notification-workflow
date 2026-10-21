SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

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
        CASE WHEN @Action = 19 THEN @OptionsJSON ELSE NULL END
    );
END;
GO