using System.Reflection;
using System.Runtime.CompilerServices;
using ActiveAlarmsParser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Parser;
using Xunit;
using NotificationSender = ActiveAlarmsParser.Service.NotificationService.NotificationService;
using static ActiveAlarmsParser.Service.NotificationService.NotificationServiceData;

namespace NotificationWorkflowService.Tests;

public class WorkflowVictimCharacterizationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingOffenderMappingOrEmptyVictimSetDoesNothingAsync(bool common, bool emptySet)
    {
        var fixture = new Fixture(common);
        if (emptySet)
            fixture.MezVictims["offender"] = [];
        else
            fixture.MezVictims["other-offender"] = ["victim"];

        await fixture.AssertFilteredAsync(Alarm("GPS20"));
    }

    public static IEnumerable<object[]> NonAppVictimCases()
    {
        foreach (bool common in new[] { false, true })
        foreach (string victimType in new[] { "EMAIL", "SMS", "vapp", "Vapp", "VAPP ", " VAPP", "" })
        foreach (string eventCode in new[] { "ordinary-event", "GPS20", "200" })
            yield return [common, victimType, eventCode];
    }

    [Theory]
    [MemberData(nameof(NonAppVictimCases))]
    public async Task ExplicitNonVappTypeIsSkippedUsingExactCaseAndNoTrimmingAsync(
        bool common, string victimType, string eventCode)
    {
        var fixture = new Fixture(common);
        fixture.MezVictims["offender"] = ["victim", "second-victim"];
        fixture.VictimTypes["victim"] = victimType;
        fixture.VictimTypes["second-victim"] = victimType;
        var alarm = Alarm(eventCode);
        alarm.MEZEventVictimID = "victim";
        // Matching MEZ/zone data leaves the type check as the rejecting filter.
        fixture.AttachedZones["offender"] = new()
        {
            ["zone|CircleZone"] = ["victim", "second-victim"]
        };

        await fixture.AssertFilteredAsync(alarm);
    }

    public static IEnumerable<object[]> MezMismatchCases()
    {
        foreach (bool common in new[] { false, true })
        foreach (string eventCode in new[] { "GPS20", "GPS21", "GPS76", "GPS77" })
        foreach (bool knownAppType in new[] { false, true })
        foreach (string? eventVictim in new string?[] { "other-victim", "VICTIM", "victim ", "", null })
            yield return [common, eventCode, knownAppType, eventVictim!];
    }

    [Theory]
    [MemberData(nameof(MezMismatchCases))]
    public async Task MezEventsSkipMismatchedVictimsEvenWhenTypeIsVappOrUnknownAsync(
        bool common, string eventCode, bool knownAppType, string? eventVictim)
    {
        var fixture = new Fixture(common);
        fixture.MezVictims["offender"] = ["victim", "second-victim"];
        if (knownAppType)
        {
            fixture.VictimTypes["victim"] = "VAPP";
            fixture.VictimTypes["second-victim"] = "VAPP";
        }
        var alarm = Alarm(eventCode);
        alarm.MEZEventVictimID = eventVictim!;

        await fixture.AssertFilteredAsync(alarm);
    }

    public static IEnumerable<object[]> AttachedZoneCases()
    {
        foreach (bool common in new[] { false, true })
        foreach (string eventCode in new[] { "200", "206", "GPS14", "GPS17" })
        foreach (string category in new[] { "CircleZone", "PolyZone" })
        foreach (string exclusion in new[]
        {
            "missing-offender", "missing-zone", "different-zone-id", "different-category",
            "category-case", "wrong-delimiter", "empty-victims", "different-victims", "victim-case"
        })
            yield return [common, eventCode, category, exclusion];
    }

    [Theory]
    [MemberData(nameof(AttachedZoneCases))]
    public async Task ZoneEventsRequireExactOffenderZoneIdCategoryAndVictimMembershipAsync(
        bool common, string eventCode, string category, string exclusion)
    {
        var fixture = new Fixture(common);
        fixture.MezVictims["offender"] = ["victim", "second-victim"];
        fixture.VictimTypes["victim"] = "VAPP";
        // Leave the second victim's type unknown: it must still obey zone filtering.
        var alarm = Alarm(eventCode);
        alarm.ZoneCategory = category;
        string zoneKey = "zone|" + category;
        var zones = new Dictionary<string, HashSet<string>>
        {
            [zoneKey] = ["victim", "second-victim"]
        };

        switch (exclusion)
        {
            case "missing-offender":
                fixture.AttachedZones["other-offender"] = zones;
                break;
            case "missing-zone":
                fixture.AttachedZones["offender"] = new();
                break;
            case "different-zone-id":
                fixture.AttachedZones["offender"] = new() { ["other-zone|" + category] = zones[zoneKey] };
                break;
            case "different-category":
                fixture.AttachedZones["offender"] = new()
                {
                    ["zone|" + (category == "CircleZone" ? "PolyZone" : "CircleZone")] = zones[zoneKey]
                };
                break;
            case "category-case":
                fixture.AttachedZones["offender"] = new() { ["zone|" + category.ToLowerInvariant()] = zones[zoneKey] };
                break;
            case "wrong-delimiter":
                fixture.AttachedZones["offender"] = new() { ["zone:" + category] = zones[zoneKey] };
                break;
            case "empty-victims":
                zones[zoneKey] = [];
                fixture.AttachedZones["offender"] = zones;
                break;
            case "different-victims":
                zones[zoneKey] = ["other-victim"];
                fixture.AttachedZones["offender"] = zones;
                break;
            case "victim-case":
                zones[zoneKey] = ["VICTIM", "SECOND-VICTIM"];
                fixture.AttachedZones["offender"] = zones;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(exclusion));
        }

        await fixture.AssertFilteredAsync(alarm);
    }

    private static ActiveAlarm Alarm(string eventCode) => new()
    {
        ClientID = "offender",
        AlarmID = eventCode,
        MEZEventVictimID = "victim",
        ZoneID = "zone",
        ZoneCategory = "CircleZone"
    };

    private sealed class Fixture
    {
        private readonly NotificationArray queue = new();
        private readonly FakeNotificationRepository repository = new();
        private readonly NoHttpFactory httpFactory = new();
        private readonly RecordingLogger<NotificationSender> senderLogger = new();
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> parserLogs = new();
        private readonly Func<ActiveAlarm, CancellationToken, Task> push;

        internal Dictionary<string, HashSet<string>> MezVictims { get; }
        internal Dictionary<string, string> VictimTypes { get; }
        internal Dictionary<string, Dictionary<string, HashSet<string>>> AttachedZones { get; }

        internal Fixture(bool common)
        {
            // The sender constructor initializes shared static settings/authentication.
            // Never call it or replace static fields: other test classes use those globals.
            // Only rejected victims are exercised; accepted paths would read static settings.
            var sender = (NotificationSender)RuntimeHelpers.GetUninitializedObject(typeof(NotificationSender));
            SetSenderField(sender, "notifications", queue);
            SetSenderField(sender, "logger", senderLogger);
            SetSenderField(sender, "repository", repository);
            SetSenderField(sender, "httpClientFactory", httpFactory);
            // Fail before static authentication even if an unexpected notification is queued.
            SetSenderField(sender, "baseURL", "http://[");

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Normal parser constructors only read this string; they do not open SQL.
                ["ConnectionStrings:connstr"] = "not-a-sql-connection-string",
                ["Platform"] = "offline-test"
            }).Build();
            if (common)
            {
                var parser = new WorkFlowCommon(new RecordingLogger<WorkFlowCommon>(parserLogs), configuration, sender);
                MezVictims = parser.MEZVictims;
                VictimTypes = parser.victimTypeDict;
                AttachedZones = parser.AttachedVictimZones;
                push = parser.PushNotificationToVictimAsync;
            }
            else
            {
                var parser = new WorkFlowInitiator(new RecordingLogger<WorkFlowInitiator>(parserLogs), configuration, sender);
                MezVictims = parser.MEZVictims;
                VictimTypes = parser.victimTypeDict;
                AttachedZones = parser.AttachedVictimZones;
                push = parser.PushNotificationToVictimAsync;
            }
        }

        internal async Task AssertFilteredAsync(ActiveAlarm alarm)
        {
            Assert.Null(await Record.ExceptionAsync(() => push(alarm, CancellationToken.None)));
            Assert.Equal(0, queue.Count());
            Assert.Empty(queue.GetSnapshot());
            // Empty batches reach PushNotificationAsync but must never attempt delivery.
            Assert.Empty(senderLogger.Entries);
            Assert.Empty(parserLogs);
            Assert.Equal(0, httpFactory.Calls);
            Assert.Equal(0, repository.AccountReads);
            Assert.Equal(0, repository.ReminderReads);
            Assert.Equal(0, repository.VictimReads);
            Assert.Empty(repository.History);
        }

        private static void SetSenderField(NotificationSender sender, string name, object value) =>
            typeof(NotificationSender).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, value);
    }

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        internal int Calls { get; private set; }
        public HttpClient CreateClient(string name)
        {
            Calls++;
            throw new InvalidOperationException("Victim filtering must not create an HTTP client.");
        }
    }

    private sealed class RecordingLogger<T>(List<(LogLevel Level, string Message, Exception? Exception)>? entries = null) : ILogger<T>
    {
        internal List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = entries ?? new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}