SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
-- Tenant database: deploy in every routed tenant before restarting the parsers or the TenantNotes worker.
-- Pair with AutoCallDB 058.sql. A Robo Call step with WaitForCallCompletion stores its queue job on the
-- next-step row; the TenantNotes worker clears it and expires the row as soon as the call ends.
IF COL_LENGTH(N'dbo.CurrentNotificationState', N'AutoCallQueueId') IS NULL
    ALTER TABLE dbo.CurrentNotificationState ADD AutoCallQueueId int NULL;
GO
CREATE OR ALTER PROCEDURE dbo.ActiveAlarms_InsertIntoCNotificationState
    @AlarmSystemID int,
    @CurrentStateNo int,
    @ParserStateNo int,
    @ExpiryTime datetime,
    @ProcessNextStep bit,
    @CurrentLoopNumber int,
    @AutoCallQueueId int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.CurrentNotificationState
        (ActiveAlarmSysID, CurrentStateNo, ExpiryTime, ParserStateNo, ProcessNextStep, CurrentLoopNo, AutoCallQueueId)
    VALUES
        (@AlarmSystemID, @CurrentStateNo, @ExpiryTime, @ParserStateNo, @ProcessNextStep, @CurrentLoopNumber, @AutoCallQueueId);
END;
GO
CREATE OR ALTER PROCEDURE dbo.ActiveAlarms_RemoveExpiredNotificationAlarms
    @CurrentTime datetime
AS
BEGIN
    SET NOCOUNT ON;

    DELETE CurrentNotificationState
    FROM CurrentNotificationState cns
    INNER JOIN ActiveAlarms a ON a.SystemID = cns.ActiveAlarmSysID
    INNER JOIN Client c ON c.SystemID = a.ClientID
    WHERE c.Deleted IS NOT NULL;

    -- AutoCallQueueId is only still set when a Robo Call wait reached its timeout without a release.
    SELECT [HistoryID]
        ,ISNULL([AgencyID], '') AS 'AgencyID'
        ,[ClientID]
        ,c.OID
        ,tz.TimeZoneName AS 'OTZ'
        ,aa.[AlarmID] AS 'AlarmSystemID'
        ,a.AlarmID
        ,[DeviceID]
        ,[StateNo] AS 'StateID'
        ,[ReceivedDateTime]
        ,aa.[EventDateTime]
        ,h.EventDescription
        ,aa.SystemID
        ,c.POGroupNum
        ,c.POGroup1
        ,c.POGroup2
        ,c.POGroup3
        ,cns.ParserStateNo
        ,cns.CurrentStateNo
        ,cns.CurrentLoopNo
        ,cns.ExpiryTime
        ,cns.AutoCallQueueId
    FROM [dbo].[ActiveAlarms] aa
    INNER JOIN History h ON h.SystemID = aa.HistoryID
    INNER JOIN Client c ON c.SystemID = aa.ClientID
    INNER JOIN [TimeZones] tz ON tz.Abbr = c.OTZ
    INNER JOIN Alarms a ON a.SystemID = aa.AlarmID
    INNER JOIN CurrentNotificationState cns ON cns.ActiveAlarmSysID = aa.SystemID
    WHERE cns.ExpiryTime <= @CurrentTime
        AND c.Deleted IS NULL
        AND cns.ProcessNextStep = 1
    ORDER BY aa.SystemID;

    DELETE
    FROM CurrentNotificationState
    WHERE ExpiryTime <= @CurrentTime
        AND ProcessNextStep = 1;
END;
GO
-- Called by the TenantNotes worker. Repeats, late releases (row already expired or consumed) and
-- releases for another queue job are successful no-ops.
CREATE OR ALTER PROCEDURE dbo.ActiveAlarms_ReleaseAutoCallWait
    @ActiveAlarmID int,
    @QueueId int,
    @OID varchar(20)
AS
BEGIN
    SET NOCOUNT ON;
    IF @ActiveAlarmID IS NULL OR @ActiveAlarmID <= 0 OR @QueueId IS NULL OR @QueueId <= 0
        OR NULLIF(LTRIM(RTRIM(@OID)), '') IS NULL
        THROW 51000, 'Complete Robo Call wait identity is required.', 1;
    UPDATE cns SET ExpiryTime = GETUTCDATE(), AutoCallQueueId = NULL
    FROM dbo.CurrentNotificationState cns
    INNER JOIN dbo.ActiveAlarms aa ON aa.SystemID = cns.ActiveAlarmSysID
    INNER JOIN dbo.Client c ON c.SystemID = aa.ClientID
    WHERE cns.ActiveAlarmSysID = @ActiveAlarmID AND cns.AutoCallQueueId = @QueueId
        AND c.OID = @OID AND cns.ExpiryTime > GETUTCDATE();
END;
GO
