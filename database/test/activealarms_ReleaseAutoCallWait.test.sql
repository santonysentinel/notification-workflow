SET NOCOUNT ON;
SET XACT_ABORT OFF;
IF DB_NAME() <> N'NotificationWorkflowTenantWaitTests'
    THROW 51000, 'Run tenant Robo Call wait tests only in the owned scratch database.', 1;
BEGIN TRY
    BEGIN TRANSACTION;
    INSERT dbo.TimeZones (Abbr, TimeZoneName) VALUES ('WT', 'Wait Test Time');
    INSERT dbo.Client (OID, OTZ, POGroupNum) VALUES ('wait-oid', 'WT', 'wait-group');
    DECLARE @ClientID int = SCOPE_IDENTITY();
    INSERT dbo.Alarms (AlarmID) VALUES ('WAIT');
    DECLARE @AlarmID int = SCOPE_IDENTITY();
    INSERT dbo.History (POGroupNum, OID, EventID, EventDescription, EventDateTime)
    VALUES ('wait-group', 'wait-oid', 'ALARM', 'Wait alarm', 1700000000);
    DECLARE @HistoryID int = SCOPE_IDENTITY();
    INSERT dbo.ActiveAlarms (HistoryID, ClientID, AlarmID, StateNo) VALUES (@HistoryID, @ClientID, @AlarmID, 1);
    DECLARE @Waiting int = SCOPE_IDENTITY();
    INSERT dbo.ActiveAlarms (HistoryID, ClientID, AlarmID, StateNo) VALUES (@HistoryID, @ClientID, @AlarmID, 1);
    DECLARE @TimedOut int = SCOPE_IDENTITY();
    INSERT dbo.ActiveAlarms (HistoryID, ClientID, AlarmID, StateNo) VALUES (@HistoryID, @ClientID, @AlarmID, 1);
    DECLARE @Plain int = SCOPE_IDENTITY();

    DECLARE @Later datetime = DATEADD(minute, 10, GETUTCDATE()), @Past datetime = DATEADD(minute, -1, GETUTCDATE());
    EXEC dbo.ActiveAlarms_InsertIntoCNotificationState @Waiting, 2, 1, @Later, 1, 0, @AutoCallQueueId = 909;
    EXEC dbo.ActiveAlarms_InsertIntoCNotificationState @TimedOut, 2, 1, @Past, 1, 0, @AutoCallQueueId = 910;
    EXEC dbo.ActiveAlarms_InsertIntoCNotificationState @Plain, 2, 1, @Past, 1, 0;
    IF NOT EXISTS (SELECT 1 FROM dbo.CurrentNotificationState WHERE ActiveAlarmSysID = @Waiting AND AutoCallQueueId = 909)
        OR NOT EXISTS (SELECT 1 FROM dbo.CurrentNotificationState WHERE ActiveAlarmSysID = @Plain AND AutoCallQueueId IS NULL)
        THROW 51000, 'Insert must store the wait queue only when supplied.', 1;

    EXEC dbo.ActiveAlarms_ReleaseAutoCallWait @Waiting, 909, 'other-oid';
    EXEC dbo.ActiveAlarms_ReleaseAutoCallWait @Waiting, 911, 'wait-oid';
    IF NOT EXISTS (SELECT 1 FROM dbo.CurrentNotificationState WHERE ActiveAlarmSysID = @Waiting
        AND AutoCallQueueId = 909 AND ExpiryTime = @Later)
        THROW 51000, 'Releases for another participant or queue job must be no-ops.', 1;
    EXEC dbo.ActiveAlarms_ReleaseAutoCallWait @TimedOut, 910, 'wait-oid';
    IF NOT EXISTS (SELECT 1 FROM dbo.CurrentNotificationState WHERE ActiveAlarmSysID = @TimedOut
        AND AutoCallQueueId = 910 AND ExpiryTime = @Past)
        THROW 51000, 'Late releases must not hide an already-timed-out wait.', 1;

    EXEC dbo.ActiveAlarms_ReleaseAutoCallWait @Waiting, 909, 'wait-oid';
    DECLARE @Released datetime = (SELECT ExpiryTime FROM dbo.CurrentNotificationState
        WHERE ActiveAlarmSysID = @Waiting AND AutoCallQueueId IS NULL);
    IF @Released IS NULL OR @Released > GETUTCDATE()
        THROW 51000, 'Release must clear the wait and make the row due now.', 1;
    EXEC dbo.ActiveAlarms_ReleaseAutoCallWait @Waiting, 909, 'wait-oid';
    IF NOT EXISTS (SELECT 1 FROM dbo.CurrentNotificationState WHERE ActiveAlarmSysID = @Waiting AND ExpiryTime = @Released)
        THROW 51000, 'Repeated releases must be no-ops.', 1;

    BEGIN TRY
        EXEC dbo.ActiveAlarms_ReleaseAutoCallWait @Waiting, 0, 'wait-oid';
        THROW 51001, 'Incomplete release identity must fail.', 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 51000 THROW;
    END CATCH;

    DECLARE @Expired TABLE
    (
        HistoryID int, AgencyID varchar(50), ClientID int, OID varchar(20), OTZ varchar(100), AlarmSystemID int,
        AlarmID varchar(50), DeviceID varchar(50), StateID int, ReceivedDateTime datetime, EventDateTime int,
        EventDescription varchar(250), SystemID int, POGroupNum varchar(20), POGroup1 varchar(20), POGroup2 varchar(20),
        POGroup3 varchar(20), ParserStateNo int, CurrentStateNo int, CurrentLoopNo int, ExpiryTime datetime,
        AutoCallQueueId int
    );
    DECLARE @Now datetime = DATEADD(second, 1, GETUTCDATE());
    INSERT @Expired EXEC dbo.ActiveAlarms_RemoveExpiredNotificationAlarms @Now;
    IF (SELECT COUNT(*) FROM @Expired) <> 3
        OR NOT EXISTS (SELECT 1 FROM @Expired WHERE SystemID = @TimedOut AND AutoCallQueueId = 910)
        OR EXISTS (SELECT 1 FROM @Expired WHERE SystemID IN (@Waiting, @Plain) AND AutoCallQueueId IS NOT NULL)
        THROW 51000, 'Only rows whose wait timed out may expose their queue job to the parser.', 1;
    IF EXISTS (SELECT 1 FROM dbo.CurrentNotificationState WHERE ActiveAlarmSysID IN (@Waiting, @TimedOut, @Plain))
        THROW 51000, 'Expired rows must be consumed.', 1;
    ROLLBACK;
    PRINT 'Tenant Robo Call wait SQL regression checks passed.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK;
    THROW;
END CATCH;
