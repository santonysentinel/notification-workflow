using NotificationWorkflowService.Entity;

namespace NotificationWorkflowService.Parser.Actions;

// Adapters delegate to the original helpers, including their exception contracts.
internal interface IWorkflowActionOperations
{
    void SendNotificationsToOfficersInSameGroup(ActiveAlarm a);
    bool PushAlertToMcApp(ActiveAlarm a);
    bool AddToNotificationQueue(ActiveAlarm a, int insertType);
    void CreateAlarmAudit(int type, string action, int historyID, int StepNo);
    void AddActiveAlarmActionToActivity(int historyID, string email, int type);
    bool insertNotificationQueueVictim(ActiveAlarm a, int insertType, string victimsEmails, bool isVictimNotification = false);
    string getInsertEmails(ActiveAlarm a);
    string getClientEmail(ActiveAlarm a);
    string getClientText(ActiveAlarm a);
}