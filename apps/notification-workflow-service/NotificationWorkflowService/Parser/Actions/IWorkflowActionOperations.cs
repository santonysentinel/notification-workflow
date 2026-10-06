using NotificationWorkflowService.Entity;

namespace NotificationWorkflowService.Parser.Actions;

// Adapters delegate to the original helpers, including their exception contracts.
internal interface IWorkflowActionOperations
{
    Task AddNoteAsync(string template, string oid, CancellationToken cancellationToken = default);
    Task SendNotificationsToOfficersInSameGroupAsync(ActiveAlarm a, CancellationToken cancellationToken = default);
    Task<bool> PushAlertToMcAppAsync(ActiveAlarm a, CancellationToken cancellationToken = default);
    Task<bool> AddToNotificationQueueAsync(ActiveAlarm a, int insertType, CancellationToken cancellationToken = default);
    Task CreateAlarmAuditAsync(int type, string action, int historyID, int StepNo, CancellationToken cancellationToken = default);
    Task AddActiveAlarmActionToActivityAsync(int historyID, string email, int type, CancellationToken cancellationToken = default);
    Task<bool> insertNotificationQueueVictimAsync(ActiveAlarm a, int insertType, string victimsEmails, bool isVictimNotification = false, CancellationToken cancellationToken = default);
    Task<string> getInsertEmailsAsync(ActiveAlarm a, CancellationToken cancellationToken = default);
    Task<string> getClientEmailAsync(ActiveAlarm a, CancellationToken cancellationToken = default);
    Task<string> getClientTextAsync(ActiveAlarm a, CancellationToken cancellationToken = default);
}