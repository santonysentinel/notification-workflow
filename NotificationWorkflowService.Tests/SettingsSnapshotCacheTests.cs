using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ActiveAlarmsParser.Service.NotificationService;

namespace NotificationWorkflowService.Tests;

public class SettingsSnapshotCacheTests
{
    [Fact]
    public void InitialLoadDoesNotRepeatUntilFiveMinuteExpiry()
    {
        var clock = new ManualClock();
        int loads = 0;
        var cache = Create(() => new Dictionary<string, string> { ["value"] = (++loads).ToString() }, clock);
        var first = cache.GetSnapshot();
        Assert.Same(first, cache.GetSnapshot());
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.Same(first, cache.GetSnapshot());
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal("2", cache.GetSnapshot()["value"]);
        Assert.Equal(2, loads);
    }

    [Fact]
    public void EmptySuccessfulLoadReplacesOldSnapshot()
    {
        var clock = new ManualClock();
        int loads = 0;
        var cache = Create(() => ++loads == 1 ? new() { ["obsolete"] = "setting" } : new(), clock);
        var original = cache.GetSnapshot();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(cache.GetSnapshot());
        Assert.Single(original); // Previously published snapshots aren't cleared in place.
        Assert.Empty(cache.GetSnapshot());
        Assert.Equal(2, loads);
    }

    [Fact]
    public void FailedRefreshRetainsOldSnapshotAndRetriesAfterThirtySeconds()
    {
        var clock = new ManualClock();
        int loads = 0;
        var cache = Create(() => ++loads switch
        {
            1 => new() { ["old"] = "value" },
            2 => throw new DataException("refresh failed"),
            _ => new() { ["new"] = "value" }
        }, clock);
        var original = cache.GetSnapshot();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Same(original, cache.GetSnapshot());
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Same(original, cache.GetSnapshot());
        Assert.Equal(2, loads);
        clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = cache.GetSnapshot();
        Assert.True(recovered.ContainsKey("new"));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Same(recovered, cache.GetSnapshot());
        Assert.Equal(3, loads);
    }

    [Fact]
    public void FailedInitialLoadUsesFailureIntervalRatherThanSuccessLifetime()
    {
        var clock = new ManualClock();
        int loads = 0;
        var cache = Create(() =>
        {
            if (++loads <= 2) throw new DataException("unavailable");
            return new() { ["recovered"] = "yes" };
        }, clock);
        Assert.Empty(cache.GetSnapshot());
        Assert.Empty(cache.GetSnapshot());
        Assert.Equal(1, loads);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(cache.GetSnapshot());
        Assert.Equal(2, loads);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(cache.GetSnapshot().ContainsKey("recovered"));
    }

    [Fact]
    public void SuccessLifetimeStartsAfterLoadCompletes()
    {
        var clock = new ManualClock();
        int loads = 0;
        var cache = Create(() =>
        {
            loads++;
            clock.Advance(TimeSpan.FromMinutes(2));
            return new();
        }, clock);
        var snapshot = cache.GetSnapshot();
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Same(snapshot, cache.GetSnapshot());
        Assert.Equal(1, loads);
        clock.Advance(TimeSpan.FromMinutes(1));
        cache.GetSnapshot();
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task ConcurrentReadersShareOneCompleteSnapshotPerRefresh()
    {
        var clock = new ManualClock();
        int loads = 0;
        var cache = Create(() =>
        {
            Interlocked.Increment(ref loads);
            return Enumerable.Range(0, 100).ToDictionary(i => i.ToString(), i => i.ToString());
        }, clock);
        var initial = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(cache.GetSnapshot)));
        Assert.Equal(1, loads);
        Assert.All(initial, snapshot => { Assert.Same(initial[0], snapshot); Assert.Equal(100, snapshot.Count); });
        clock.Advance(TimeSpan.FromMinutes(5));
        var refreshed = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(cache.GetSnapshot)));
        Assert.Equal(2, loads);
        Assert.All(refreshed, snapshot => { Assert.Same(refreshed[0], snapshot); Assert.Equal(100, snapshot.Count); });
        Assert.NotSame(initial[0], refreshed[0]);
        Assert.Equal(100, initial[0].Count);
    }

    [Fact]
    public void AllSettingsMappersAcceptEmptyResults()
    {
        using var table = new DataTable();
        Assert.Empty(NotificationServiceSetting.BuildAccountSettings(table));
        Assert.Empty(NotificationServiceSetting.BuildReminderSettings(table));
        Assert.Empty(NotificationServiceSetting.BuildVictimSettings(table));
    }

    [Fact]
    public void MappersPreserveFieldsAndFirstDuplicateRowWins()
    {
        using var accounts = Table("UserName", "EventCode", "EventType", "FieldName", "PushNotificationToken", "UserFullname", "NotificationSubType", "ReminderType", "ReminderEnable", "AlternativeText");
        accounts.Rows.Add("v", "e", "event", "field", "token", "name", "sub", "reminder", "False", "first");
        accounts.Rows.Add("v", "e", "event", "field", "token", "name", "sub", "reminder", "True", "second");
        var account = NotificationServiceSetting.BuildAccountSettings(accounts)["v"]["e"];
        Assert.Equal("first", account.AlternativeText);
        Assert.Equal("token", account.PushNotificationToken);
        Assert.Equal("reminder", account.ReminderType);
        Assert.Equal("v", account.UserName);
        using var reminders = Table("EventCode", "EventName", "ReminderType", "NotificationSubType", "AlternativeText", "EventNotificationType");
        reminders.Rows.Add("e", "first", "reminder", "sub", "text", "offtamper");
        reminders.Rows.Add("e", "second", "reminder", "sub", "text", "offbattery");
        var reminder = NotificationServiceSetting.BuildReminderSettings(reminders)["e"];
        Assert.Equal("first", reminder.EventName);
        Assert.Equal("offtamper", reminder.EventNotificationType);
        using var victims = Table("OID", "victimproximity", "offbattery", "offtamper", "offcellgpstatus");
        victims.Rows.Add("o", "True", "False", "True", "False");
        victims.Rows.Add("o", "False", "True", "False", "True");
        var victim = NotificationServiceSetting.BuildVictimSettings(victims)["o"];
        Assert.Equal("True", victim.victimproximity);
        Assert.Equal("False", victim.offbattery);
        Assert.Equal("True", victim.offtamper);
        Assert.Equal("False", victim.offcellgpstatus);
    }

    [Fact]
    public void MappingFailureAfterFirstRowDoesNotPublishPartialSnapshot()
    {
        var clock = new ManualClock();
        using var table = Table("EventCode", "EventName", "ReminderType", "NotificationSubType", "AlternativeText", "EventNotificationType");
        table.Rows.Add("old", "old name", "reminder", "sub", "text", "offtamper");
        var cache = new SettingsSnapshotCache<Dictionary<string, NotificationServiceData.ReminderSetting>>(
            () => NotificationServiceSetting.BuildReminderSettings(table), new(), NullLogger.Instance, "reminders", clock);
        var original = cache.GetSnapshot();
        table.Rows.Clear();
        table.Rows.Add("new", "new name", "reminder", "sub", "text", "offtamper");
        table.Rows.Add("bad", new BrokenValue(), "reminder", "sub", "text", "offtamper");
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Same(original, cache.GetSnapshot());
        Assert.True(original.ContainsKey("old"));
        Assert.False(original.ContainsKey("new"));
        table.Rows.RemoveAt(1);
        clock.Advance(TimeSpan.FromSeconds(30));
        var recovered = cache.GetSnapshot();
        Assert.True(recovered.ContainsKey("new"));
        Assert.False(recovered.ContainsKey("old"));
    }

    private static SettingsSnapshotCache<Dictionary<string, string>> Create(Func<Dictionary<string, string>> loader, TimeProvider clock)
        => new(loader, new(), NullLogger.Instance, "test", clock);

    private static DataTable Table(params string[] columns)
    {
        var table = new DataTable();
        foreach (string column in columns) table.Columns.Add(column, typeof(object));
        return table;
    }

    private sealed class BrokenValue
    {
        public override string ToString() => throw new DataException("invalid row mapping");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
}