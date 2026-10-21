SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
-- Tenant database: deploy after 052.sql (ProfileItems.FlowId dropped), before restarting either parser.
-- Supersedes 050.sql. Action-19 FlowId now comes from OptionsJSON.participantRoboCall.FlowId.
CREATE OR ALTER PROCEDURE dbo.ActiveAlarms_ReadProfileItems
AS
BEGIN
    SET NOCOUNT ON;
    SELECT pfi.ProfileID, p.ProfileName, pfi.EventCode, pfi.StartTime,
        FORMAT(CAST(pfi.EndTime AS datetime), 'HH:mm:59') AS EndTime,
        pfi.[Day], pfi.[Action], pfi.HoldDuration, pfi.GracePeriod,
        pfi.Instruction, pfi.EmailAddress, pfi.EmailJoin, pfi.StateNo, pfi.StateTime,
        pfi.FeedbackRequired, pfi.ProfileType,
        ISNULL(pfi.TimeIntervalsID, -1) AS TimeIntervalsID,
        ISNULL(pfi.NextState, -1) AS NextState,
        ISNULL(pfi.LoopStartState, -1) AS LoopStartState,
        ISNULL(pfi.NumberOfLoops, 1) AS NumberOfLoops,
        ISNULL(pfi.RoleID, -1) AS RoleID,
        ISNULL(pfi.RoleAction, -1) AS RoleAction,
        ISNULL(pfi.IsAlarmClearingEnabled, 0) AS IsAlarmClearingEnabled,
        pfi.OptionsJSON
    FROM dbo.ProfileItems pfi
    INNER JOIN dbo.Profiles p ON p.ProfileID = pfi.ProfileID
    WHERE p.DeletedTime IS NULL AND pfi.DeletedDate IS NULL
    ORDER BY pfi.EventCode, pfi.[Day], pfi.ProfileType;
END;
GO
