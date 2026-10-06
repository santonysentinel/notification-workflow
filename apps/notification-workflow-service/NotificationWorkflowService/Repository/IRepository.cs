using NotificationWorkflowService.Entity;

namespace NotificationWorkflowService.Repository
{
    /// <summary>
    /// Constructs SQL operations only. Callers retain streaming mapping, retries and error handling.
    /// Prepare once outside the retry loop and dispose after the whole loop.
    /// </summary>
    public interface IRepository
    {
        // Reference and contact reads.
        IWorkflowOperation PrepareFetchClientProfile(int profileType);
        IWorkflowOperation PrepareReadAllProfiles();
        IWorkflowOperation PrepareReadAllHolidays();
        IWorkflowOperation PrepareReadAllRoles();
        IWorkflowOperation PrepareReadRoles(ActiveAlarm a, int roleID, int roleAction);
        IWorkflowOperation PrepareReadVictims();
        IWorkflowOperation PrepareReadMEZVictims();
        IWorkflowOperation PrepareReadAttachedVictimZones();
        IWorkflowOperation PrepareReadProfileItemsClear();
        IWorkflowOperation PrepareGetInsertEmails(ActiveAlarm a);
        IWorkflowOperation PrepareGetClientEmail(ActiveAlarm a);
        IWorkflowOperation PrepareGetClientText(ActiveAlarm a);

        // Queue, audit and history writes.
        /// <summary>
        /// Prepares activealarms_AddNote without I/O. Duplicates are allowed.
        /// Assumed procedure parameter names: @Note (nvarchar(1000)), @OID (varchar(20)).
        /// OID encoding/representability is governed by the database collation, not ASCII validation.
        /// </summary>
        IWorkflowOperation PrepareAddNote(string noteText, string oid);
        IWorkflowOperation PreparePushAlertToMcApp(ActiveAlarm a);
        IWorkflowOperation PrepareAddToNotificationQueue(ActiveAlarm a, int insertType);
        IWorkflowOperation PrepareInsertPushNotificationQueue(ActiveAlarm a, string? victimID, string? offenderID);
        IWorkflowOperation PrepareInsertNotificationQueueVictim(ActiveAlarm a, int insertType, string? victimsEmails, bool isVictimNotification = false);
        IWorkflowOperation PrepareCreateAlarmAudit(int type, string? action, int historyID, int stepNo);
        IWorkflowOperation PrepareAddActiveAlarmActionToActivity(int historyID, string? email, int type);
        IWorkflowOperation PrepareClearMcAppAlarm(string? alarms, string? oid);

        // Progress and notification state.
        IWorkflowOperation PrepareReadPoints(string? numberToRead, int lastSuccessfulProcess);
        IWorkflowOperation PrepareReadLastSuccessfulProcess(int parserID, bool reloadConnectionString = false);
        IWorkflowOperation PrepareReadCurrentActiveAlarmPoint(bool reloadConnectionString = false);
        IWorkflowOperation PrepareUpdateParserActivty(int? systemID, string? parserId);
        IWorkflowOperation PrepareInsertIntoAlarmNotification(int alarmSystemID, int currentStateNo, DateTime expiryTime, string? action);
        IWorkflowOperation PrepareInsertIntoCurrentAlarmNotification(int alarmSystemID, int currentStateNo, DateTime expiryTime, int displayStateNo, int currentLoopNumber, int processNext);
        IWorkflowOperation PrepareGetExpiredAlarms(DateTime currentTime);
    }
}
