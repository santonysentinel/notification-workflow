SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

ALTER PROCEDURE dbo.dash_profile_GetProfileSteps
    @session varchar(32),
    @ProfileID int
AS
BEGIN
    DECLARE @potz varchar(5) = NULL;

    SELECT @potz = UserTZ
    FROM dbo.dash_validateUser(@session);

    IF @potz IS NOT NULL
    BEGIN
        SELECT p.ProfileName,
            p.ProfileID,
            EventCode,
            [Day],
            [Action],
            Instruction,
            EmailAddress,
            StateNo,
            StateTime,
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
            pri.FlowId
        FROM Profiles p
        INNER JOIN ProfileItems pri ON pri.ProfileID = p.ProfileID
        WHERE p.ProfileID = @ProfileID AND DeletedDate IS NULL
        ORDER BY pri.EventCode, [Day], pri.StateNo;
    END;
END;
GO