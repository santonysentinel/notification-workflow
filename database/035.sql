ALTER PROCEDURE [dbo].[ActiveAlarms_ReadProfileItems]
AS
BEGIN
	SET NOCOUNT ON;

	SELECT pfi.[ProfileID]
		,[ProfileName]
		,[EventCode]
		,[StartTime]
		,FORMAT(CAST(EndTime AS DATETIME), 'HH:mm:59') AS 'EndTime'
		,[Day]
		,[Action]
		,[HoldDuration]
		,[GracePeriod]
		,[Instruction]
		,[EmailAddress]
		,[EmailJoin]
		,[StateNo]
		,[StateTime]
		,[FeedbackRequired]
		,[ProfileType]
		,ISNULL([TimeIntervalsID], - 1) AS 'TimeIntervalsID'
		,ISNULL([NextState], - 1) AS 'NextState'
		,ISNULL([LoopStartState], - 1) AS 'LoopStartState'
		,ISNULL([NumberOfLoops], 1) AS 'NumberOfLoops'
		,ISNULL([RoleID], - 1) AS 'RoleID'
		,ISNULL([RoleAction], - 1) AS 'RoleAction'
		,ISNULL([IsAlarmClearingEnabled], 0) AS 'IsAlarmClearingEnabled'
		,ISNULL([FlowID],0) AS FlowID
	FROM ProfileItems pfi
	INNER JOIN Profiles p ON p.ProfileID = pfi.ProfileID
	WHERE p.DeletedTime IS NULL
		AND pfi.DeletedDate IS NULL
	ORDER BY EventCode
		,[Day]
		,pfi.ProfileType
END