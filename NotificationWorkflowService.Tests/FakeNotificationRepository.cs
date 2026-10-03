using NotificationWorkflowService.Repository;

namespace NotificationWorkflowService.Tests;

internal sealed class FakeNotificationRepository : INotificationRepository
{
    internal List<(string HistoryId, string VictimId, string Note, int Type)> History { get; } = new();
    internal Exception? HistoryFailure { get; set; }
    internal Func<CancellationToken, Task>? InsertHistory { get; set; }
    internal CancellationToken HistoryToken { get; private set; }
    internal int AccountReads { get; private set; }
    internal int ReminderReads { get; private set; }
    internal int VictimReads { get; private set; }
    internal CancellationToken AccountReadToken { get; private set; }
    internal CancellationToken ReminderReadToken { get; private set; }
    internal CancellationToken VictimReadToken { get; private set; }

    public Task<IReadOnlyList<AccountNotificationSettingRow>> ReadAccountNotificationSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AccountReadToken = cancellationToken;
        AccountReads++;
        return Task.FromResult<IReadOnlyList<AccountNotificationSettingRow>>([new() { UserName = "v", EventCode = "e", PushNotificationToken = "token" }]);
    }
    public Task<IReadOnlyList<ReminderSettingRow>> ReadReminderSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReminderReadToken = cancellationToken;
        ReminderReads++;
        return Task.FromResult<IReadOnlyList<ReminderSettingRow>>([new() { EventCode = "e", EventNotificationType = "offtamper" }]);
    }
    public Task<IReadOnlyList<VictimNotificationSettingRow>> ReadVictimSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VictimReadToken = cancellationToken;
        VictimReads++;
        return Task.FromResult<IReadOnlyList<VictimNotificationSettingRow>>([new() { OID = "o", offtamper = "True" }]);
    }
    public Task InsertNotificationHistoryAsync(string historyId, string victimId, string note, int type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HistoryToken = cancellationToken;
        History.Add((historyId, victimId, note, type));
        if (InsertHistory != null) return InsertHistory(cancellationToken);
        return HistoryFailure == null ? Task.CompletedTask : Task.FromException(HistoryFailure);
    }
}