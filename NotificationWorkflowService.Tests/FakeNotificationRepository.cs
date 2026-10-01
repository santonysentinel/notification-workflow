using NotificationWorkflowService.Repository;

namespace NotificationWorkflowService.Tests;

internal sealed class FakeNotificationRepository : INotificationRepository
{
    internal List<(string HistoryId, string VictimId, string Note, int Type)> History { get; } = new();
    internal Exception? HistoryFailure { get; set; }
    internal int AccountReads { get; private set; }
    internal int ReminderReads { get; private set; }
    internal int VictimReads { get; private set; }

    public Task<IReadOnlyList<AccountNotificationSettingRow>> ReadAccountNotificationSettingsAsync(CancellationToken cancellationToken = default)
    {
        AccountReads++;
        return Task.FromResult<IReadOnlyList<AccountNotificationSettingRow>>([new() { UserName = "v", EventCode = "e", PushNotificationToken = "token" }]);
    }
    public Task<IReadOnlyList<ReminderSettingRow>> ReadReminderSettingsAsync(CancellationToken cancellationToken = default)
    {
        ReminderReads++;
        return Task.FromResult<IReadOnlyList<ReminderSettingRow>>([new() { EventCode = "e", EventNotificationType = "offtamper" }]);
    }
    public Task<IReadOnlyList<VictimNotificationSettingRow>> ReadVictimSettingsAsync(CancellationToken cancellationToken = default)
    {
        VictimReads++;
        return Task.FromResult<IReadOnlyList<VictimNotificationSettingRow>>([new() { OID = "o", offtamper = "True" }]);
    }
    public Task InsertNotificationHistoryAsync(string historyId, string victimId, string note, int type, CancellationToken cancellationToken = default)
    {
        History.Add((historyId, victimId, note, type));
        return HistoryFailure == null ? Task.CompletedTask : Task.FromException(HistoryFailure);
    }
}