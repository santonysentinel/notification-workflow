namespace NotificationWorkflowService.Repository;

public interface INotificationRepository
{
    Task<IReadOnlyList<AccountNotificationSettingRow>> ReadAccountNotificationSettingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReminderSettingRow>> ReadReminderSettingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VictimNotificationSettingRow>> ReadVictimSettingsAsync(CancellationToken cancellationToken = default);
    Task InsertNotificationHistoryAsync(string historyId, string victimId, string note, int type, CancellationToken cancellationToken = default);
}

public sealed class AccountNotificationSettingRow
{
    public string? UserName { get; set; }
    public string? EventCode { get; set; }
    public string? EventType { get; set; }
    public string? FieldName { get; set; }
    public string? PushNotificationToken { get; set; }
    public string? UserFullname { get; set; }
    public string? NotificationSubType { get; set; }
    public string? ReminderType { get; set; }
    public string? ReminderEnable { get; set; }
    public string? AlternativeText { get; set; }
}

public sealed class ReminderSettingRow
{
    public string? EventCode { get; set; }
    public string? EventName { get; set; }
    public string? ReminderType { get; set; }
    public string? NotificationSubType { get; set; }
    public string? AlternativeText { get; set; }
    public string? EventNotificationType { get; set; }
}

public sealed class VictimNotificationSettingRow
{
    public string? OID { get; set; }
    public string? victimproximity { get; set; }
    public string? offbattery { get; set; }
    public string? offtamper { get; set; }
    public string? offcellgpstatus { get; set; }
}