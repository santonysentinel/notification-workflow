using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ActiveAlarmsParser.Service.NotificationService;
using NotificationWorkflowService.Repository;

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
        Assert.Empty(NotificationServiceSetting.BuildAccountSettings(new List<AccountNotificationSettingRow>()));
        Assert.Empty(NotificationServiceSetting.BuildReminderSettings(new List<ReminderSettingRow>()));
        Assert.Empty(NotificationServiceSetting.BuildVictimSettings(new List<VictimNotificationSettingRow>()));
    }

    [Fact]
    public void MappersPreserveFieldsAndFirstDuplicateRowWins()
    {
        var accounts = new List<AccountNotificationSettingRow>
        {
            new() { UserName = "v", EventCode = "e", EventType = "event", FieldName = "field", PushNotificationToken = "token", UserFullname = "name", NotificationSubType = "sub", ReminderType = "reminder", ReminderEnable = "False", AlternativeText = "first" },
            new() { UserName = "v", EventCode = "e", EventType = "second event", FieldName = "second field", PushNotificationToken = "second token", UserFullname = "second name", NotificationSubType = "second sub", ReminderType = "second reminder", ReminderEnable = "True", AlternativeText = "second" }
        };
        var account = NotificationServiceSetting.BuildAccountSettings(accounts)["v"]["e"];
        Assert.Equal("first", account.AlternativeText);
        Assert.Equal("token", account.PushNotificationToken);
        Assert.Equal("reminder", account.ReminderType);
        Assert.Equal("v", account.UserName);
        Assert.Equal("e", account.EventCode);
        Assert.Equal("event", account.EventType);
        Assert.Equal("field", account.FieldName);
        Assert.Equal("name", account.UserFullname);
        Assert.Equal("sub", account.NotificationSubType);
        var reminders = new List<ReminderSettingRow>
        {
            new() { EventCode = "e", EventName = "first", ReminderType = "reminder", NotificationSubType = "sub", AlternativeText = "text", EventNotificationType = "offtamper" },
            new() { EventCode = "e", EventName = "second", ReminderType = "second reminder", NotificationSubType = "second sub", AlternativeText = "second text", EventNotificationType = "offbattery" }
        };
        var reminder = NotificationServiceSetting.BuildReminderSettings(reminders)["e"];
        Assert.Equal("first", reminder.EventName);
        Assert.Equal("offtamper", reminder.EventNotificationType);
        Assert.Equal("reminder", reminder.ReminderType);
        Assert.Equal("sub", reminder.NotificationSubType);
        Assert.Equal("text", reminder.AlternativeText);
        var victims = new List<VictimNotificationSettingRow>
        {
            new() { OID = "o", victimproximity = "True", offbattery = "False", offtamper = "True", offcellgpstatus = "False" },
            new() { OID = "o", victimproximity = "False", offbattery = "True", offtamper = "False", offcellgpstatus = "True" }
        };
        var victim = NotificationServiceSetting.BuildVictimSettings(victims)["o"];
        Assert.Equal("True", victim.victimproximity);
        Assert.Equal("False", victim.offbattery);
        Assert.Equal("True", victim.offtamper);
        Assert.Equal("False", victim.offcellgpstatus);
    }

    [Fact]
    public void MappersNormalizeNullFieldsToEmptyStrings()
    {
        var account = NotificationServiceSetting.BuildAccountSettings(new List<AccountNotificationSettingRow> { new() })[string.Empty][string.Empty];
        Assert.Equal(string.Empty, account.UserName);
        Assert.Equal(string.Empty, account.EventCode);
        Assert.Equal(string.Empty, account.EventType);
        Assert.Equal(string.Empty, account.FieldName);
        Assert.Equal(string.Empty, account.PushNotificationToken);
        Assert.Equal(string.Empty, account.UserFullname);
        Assert.Equal(string.Empty, account.NotificationSubType);
        Assert.Equal(string.Empty, account.ReminderType);
        Assert.Equal(string.Empty, account.AlternativeText);

        var reminder = NotificationServiceSetting.BuildReminderSettings(new List<ReminderSettingRow> { new() })[string.Empty];
        Assert.Equal(string.Empty, reminder.EventName);
        Assert.Equal(string.Empty, reminder.ReminderType);
        Assert.Equal(string.Empty, reminder.NotificationSubType);
        Assert.Equal(string.Empty, reminder.AlternativeText);
        Assert.Equal(string.Empty, reminder.EventNotificationType);

        var victim = NotificationServiceSetting.BuildVictimSettings(new List<VictimNotificationSettingRow> { new() })[string.Empty];
        Assert.Equal(string.Empty, victim.victimproximity);
        Assert.Equal(string.Empty, victim.offbattery);
        Assert.Equal(string.Empty, victim.offtamper);
        Assert.Equal(string.Empty, victim.offcellgpstatus);
        Assert.Null(victim.OID); // Preserve the existing mapping: OID is only the dictionary key.
    }

    [Fact]
    public void MappingFailureAfterFirstRowDoesNotPublishPartialSnapshot()
    {
        var clock = new ManualClock();
        var rows = new List<ReminderSettingRow>
        {
            new() { EventCode = "old", EventName = "old name", ReminderType = "reminder", NotificationSubType = "sub", AlternativeText = "text", EventNotificationType = "offtamper" }
        };
        bool failAfterFirstRow = false;
        var cache = new SettingsSnapshotCache<Dictionary<string, NotificationServiceData.ReminderSetting>>(
            () => NotificationServiceSetting.BuildReminderSettings(failAfterFirstRow ? RowsThenThrow(rows[0]) : rows), new(), NullLogger.Instance, "reminders", clock);
        var original = cache.GetSnapshot();
        rows[0] = new() { EventCode = "new", EventName = "new name", ReminderType = "reminder", NotificationSubType = "sub", AlternativeText = "text", EventNotificationType = "offtamper" };
        failAfterFirstRow = true;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Same(original, cache.GetSnapshot());
        Assert.Single(original);
        Assert.True(original.ContainsKey("old"));
        Assert.False(original.ContainsKey("new"));
        Assert.Equal("old name", original["old"].EventName);
        failAfterFirstRow = false;
        clock.Advance(TimeSpan.FromSeconds(30));
        var recovered = cache.GetSnapshot();
        Assert.NotSame(original, recovered);
        Assert.Single(recovered);
        Assert.True(recovered.ContainsKey("new"));
        Assert.False(recovered.ContainsKey("old"));
        Assert.Equal("new name", recovered["new"].EventName);
        Assert.Single(original);
        Assert.Equal("old name", original["old"].EventName);
    }

    private static SettingsSnapshotCache<Dictionary<string, string>> Create(Func<Dictionary<string, string>> loader, TimeProvider clock)
        => new(loader, new(), NullLogger.Instance, "test", clock);

    private static IEnumerable<ReminderSettingRow> RowsThenThrow(ReminderSettingRow first)
    {
        yield return first;
        throw new DataException("row enumeration failed");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
}