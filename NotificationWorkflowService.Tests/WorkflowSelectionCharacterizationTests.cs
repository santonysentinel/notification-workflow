using System.Reflection;
using System.Runtime.CompilerServices;
using ActiveAlarmsParser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Parser;
using Xunit;

namespace NotificationWorkflowService.Tests;

public class WorkflowSelectionCharacterizationTests
{
    private static readonly DateTime FixedDay = new(2026, 10, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialSelectionIncludesBothExactBoundaries(bool common)
    {
        var parser = NormalParser(common);
        var item = Item(4, "boundary");
        item.StartTime = FixedDay.AddHours(9);
        item.EndTime = FixedDay.AddHours(10);

        Assert.Same(item, SelectInitial(parser, [item], item.StartTime));
        Assert.Same(item, SelectInitial(parser, [item], item.EndTime));
        Assert.Null(SelectInitial(parser, [item], item.StartTime.AddTicks(-1)));
        Assert.Null(SelectInitial(parser, [item], item.EndTime.AddTicks(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialSelectionPrefersZeroEvenOverNegativeAndEarlierStates(bool common)
    {
        var parser = NormalParser(common);
        var zero = Item(0, "zero");

        Assert.Same(zero, SelectInitial(parser,
            [Item(5, "five"), Item(-2, "negative"), zero, Item(1, "one")], FixedDay.AddHours(12)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialSelectionOtherwiseUsesMinimumStateAndFirstEligibleDuplicate(bool common)
    {
        var parser = NormalParser(common);
        var first = Item(2, "first");
        var second = Item(2, "second");
        var ineligible = Item(0, "outside");
        ineligible.EndTime = FixedDay.AddHours(1);

        Assert.Same(first, SelectInitial(parser,
            [Item(8, "eight"), ineligible, first, second, Item(3, "three")], FixedDay.AddHours(12)));
        Assert.Same(second, SelectInitial(parser, [second, first], FixedDay.AddHours(12)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialSelectionReturnsNullForEmptyOrNonmatchingWindows(bool common)
    {
        var parser = NormalParser(common);
        var item = Item(0, "outside");
        item.StartTime = FixedDay.AddHours(13);
        item.EndTime = FixedDay.AddHours(14);

        Assert.Null(SelectInitial(parser, [], FixedDay.AddHours(12)));
        Assert.Null(SelectInitial(parser, [item], FixedDay.AddHours(12)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialSelectionAcceptsZeroLengthWindowAtItsExactTime(bool common)
    {
        var item = Item(0, "instant");
        item.StartTime = item.EndTime = FixedDay.AddHours(12);

        Assert.Same(item, SelectInitial(NormalParser(common), [item], item.StartTime));
    }

    public static IEnumerable<object[]> HolidayCases()
    {
        for (int parser = 0; parser < 3; parser++)
        foreach (string group in new[] { "POGroupNum", "POGroup1", "POGroup2", "POGroup3" })
        foreach (int offset in new[] { -1, 0, 2, 3 })
            yield return [parser, group, offset, offset is 0 or 2];
    }

    [Theory]
    [MemberData(nameof(HolidayCases))]
    public void HolidayCheckUsesEachGroupAndInclusiveDates(int kind, string group, int offset, bool expected)
    {
        var parser = Parser(kind);
        var alarm = Alarm();
        typeof(ActiveAlarm).GetProperty(group)!.SetValue(alarm, "holiday-group");
        Holidays(parser)["holiday-group"] =
        [
            new Holiday
            {
                HolidayName = "test holiday",
                StartDate = FixedDay.AddHours(23),
                EndDate = FixedDay.AddDays(2).AddHours(1)
            }
        ];

        // Time components are ignored, including on the two boundary dates.
        Assert.Equal(expected, IsHoliday(parser, alarm, FixedDay.AddDays(offset).AddHours(12)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HolidayCheckIgnoresWhitespaceOnlyGroupsButDoesNotTrimDictionaryKeys(int kind)
    {
        var parser = Parser(kind);
        var alarm = Alarm();
        alarm.POGroupNum = "   ";
        alarm.POGroup1 = " group ";
        Holidays(parser)["group"] = [new Holiday { StartDate = FixedDay, EndDate = FixedDay }];

        Assert.False(IsHoliday(parser, alarm, FixedDay));
        Holidays(parser)[" group "] = Holidays(parser)["group"];
        Assert.True(IsHoliday(parser, alarm, FixedDay));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HolidayCheckDoesNotShortCircuitBeforeLaterNullGroup(int kind)
    {
        var parser = Parser(kind);
        var alarm = Alarm();
        alarm.POGroupNum = "holiday-group";
        alarm.POGroup3 = null!;
        Holidays(parser)["holiday-group"] = [new Holiday { StartDate = FixedDay, EndDate = FixedDay }];

        var exception = Assert.Throws<TargetInvocationException>(() => IsHoliday(parser, alarm, FixedDay));
        Assert.IsType<NullReferenceException>(exception.InnerException);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void PrioritySelectionUsesNormalOrHolidayMappingAndCopiesSelectedFields(int kind, bool holiday)
    {
        var parser = Parser(kind);
        var alarm = Alarm();
        alarm.StateNo = 2;
        var normal = Item(2, "normal");
        var special = Item(2, "holiday");
        AddProfile(parser, normal, 10, false);
        AddProfile(parser, special, 20, true);
        if (holiday)
        {
            alarm.POGroup2 = "holiday-group";
            Holidays(parser)["holiday-group"] =
                [new Holiday { StartDate = DateTime.UtcNow.Date.AddDays(-1), EndDate = DateTime.UtcNow.Date.AddDays(1) }];
        }
        List<ActiveAlarm> alarms = [alarm];

        Populate(parser, alarms);

        Assert.Same(alarm, Assert.Single(alarms));
        AssertSelection(alarm, holiday ? special : normal, holiday ? 20 : 10);
    }

    [Theory]
    [InlineData(false, "0", 1)]
    [InlineData(false, "1", 3)]
    [InlineData(true, "0", 1)]
    [InlineData(true, "1", 3)]
    public void NormalMissingProfileRetainsAlarmAndUsesConfiguredDefault(bool common, string flag, int priority)
    {
        var parser = NormalParser(common);
        SetField(parser, "configuration", new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DefaultMcAppFlag"] = flag }).Build());
        var alarm = Alarm();
        alarm.ProfileName = "previous profile";
        alarm.Instruction = "previous instruction";
        List<ActiveAlarm> alarms = [alarm];

        Populate(parser, alarms);

        Assert.Same(alarm, Assert.Single(alarms));
        Assert.Equal(priority, alarm.Priority);
        Assert.Equal(flag == "1" ? "" : "previous profile", alarm.ProfileName);
        Assert.Equal(flag == "1" ? "" : "previous instruction", alarm.Instruction);
    }

    [Theory]
    [InlineData(0, 1, 5, 1, 1)]
    [InlineData(0, 1, 1, -1, 4)]
    [InlineData(1, 1, 1, -1, 4)]
    [InlineData(1, -1, 5, 1, 2)]
    public void StepsIncrementOnlyAtLoopStartAndTransitionAtOrAboveLoopLimit(
        int currentLoop, int loopStart, int limit, int expectedLoop, int expectedState)
    {
        var parser = NewParser<WorkFlowSteps>();
        var alarm = Alarm();
        alarm.StateNo = 2;
        // The loop-start cases use state 1; other cases use state 2.
        if (loopStart == 1) alarm.StateNo = 1;
        alarm.CurrentLoopNumber = currentLoop;
        var current = Item(alarm.StateNo, "current");
        current.LoopStartState = loopStart;
        current.NumberOfLoops = limit;
        var ineligibleNext = Item(3, "same-loop");
        ineligibleNext.LoopStartState = loopStart;
        var next = Item(4, "next");
        next.LoopStartState = -1;
        AddProfile(parser, next, 10, false, ineligibleNext, current);
        List<ActiveAlarm> alarms = [alarm];

        Populate(parser, alarms);

        Assert.Same(alarm, Assert.Single(alarms));
        Assert.Equal(expectedLoop, alarm.CurrentLoopNumber);
        AssertSelection(alarm, expectedState == 4 ? next : current, 10);
        Assert.Equal(expectedState, alarm.StateNo);
    }

    [Fact]
    public void StepsTransitionChoosesLowestEligibleStateAndFirstDuplicate()
    {
        var parser = NewParser<WorkFlowSteps>();
        var alarm = Alarm();
        alarm.StateNo = 2;
        alarm.CurrentLoopNumber = 3;
        var current = Item(2, "current");
        current.LoopStartState = 1;
        current.NumberOfLoops = 3;
        var next = Item(5, "first-next");
        next.LoopStartState = 2;
        var duplicate = Item(5, "second-next");
        duplicate.LoopStartState = -1;
        var sameLoop = Item(3, "same-loop");
        sameLoop.LoopStartState = 1;
        AddProfile(parser, Item(8, "later"), 10, false, duplicate, sameLoop, current);
        var items = Profiles(parser)[10].Events["event"][1];
        items.Insert(0, next);
        List<ActiveAlarm> alarms = [alarm];

        Populate(parser, alarms);

        Assert.Same(alarm, Assert.Single(alarms));
        AssertSelection(alarm, next, 10);
        Assert.Equal(-1, alarm.CurrentLoopNumber);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void StepsUseFirstDuplicateStateAndDoNotGateSelectionOnProcessNext(int processNext)
    {
        var parser = NewParser<WorkFlowSteps>();
        var alarm = Alarm();
        alarm.StateNo = 2;
        alarm.ProcessNextStep = processNext;
        var first = Item(2, "first");
        AddProfile(parser, Item(1, "earlier-state"), 10, false, first, Item(2, "second"));
        List<ActiveAlarm> alarms = [alarm];

        Populate(parser, alarms);

        Assert.Same(alarm, Assert.Single(alarms));
        AssertSelection(alarm, first, 10);
        Assert.Equal(processNext, alarm.ProcessNextStep);
    }

    [Theory]
    [InlineData("missing-mapping")]
    [InlineData("missing-profile")]
    [InlineData("missing-event")]
    [InlineData("missing-day")]
    [InlineData("missing-state")]
    [InlineData("exhausted-loop")]
    [InlineData("zero-length")]
    [InlineData("inverted-window")]
    public void StepsRemoveAlarmWhenNoEligibleProfileItemExists(string scenario)
    {
        var parser = NewParser<WorkFlowSteps>();
        var alarm = Alarm();
        alarm.StateNo = 2;
        var item = Item(2, "unmatched");
        if (scenario == "missing-state") item.StateNo = 3;
        if (scenario == "exhausted-loop") item.NumberOfLoops = -1;
        if (scenario == "zero-length")
        {
            // No possible clock reading satisfies start <= now < end when start == end.
            // This proves exclusion even at the exact endpoint without racing UtcNow.
            item.StartTime = item.EndTime = FixedDay.AddHours(12);
        }
        if (scenario == "inverted-window")
        {
            // Always either before start or at/after end; does not depend on midnight.
            item.StartTime = FixedDay.AddHours(23).AddMinutes(59).AddSeconds(59);
            item.EndTime = FixedDay;
        }
        AddProfile(parser, item, 10, false);
        if (scenario == "missing-mapping") Mapping(parser, false).Clear();
        if (scenario == "missing-profile") Profiles(parser).Clear();
        if (scenario == "missing-event") Profiles(parser)[10].Events.Clear();
        if (scenario == "missing-day") Profiles(parser)[10].Events["event"].Clear();
        List<ActiveAlarm> alarms = [alarm];

        Populate(parser, alarms);

        Assert.Empty(alarms);
    }

    [Fact]
    public void StepsRemoveMultipleUnmatchedAlarmsWithoutDroppingMatchedAlarm()
    {
        var parser = NewParser<WorkFlowSteps>();
        var matched = Alarm();
        matched.StateNo = 2;
        AddProfile(parser, Item(2, "matched"), 10, false);
        List<ActiveAlarm> alarms = [Alarm(), matched, Alarm()];

        Populate(parser, alarms);

        Assert.Same(matched, Assert.Single(alarms));
    }

    private static object NormalParser(bool common) => common ? NewParser<WorkFlowCommon>() : NewParser<WorkFlowInitiator>();

    private static object Parser(int kind) => kind switch
    {
        0 => NewParser<WorkFlowInitiator>(),
        1 => NewParser<WorkFlowCommon>(),
        2 => NewParser<WorkFlowSteps>(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static T NewParser<T>() where T : class
    {
        // Constructors configure database access. Bypass them, and explicitly restore
        // field initializers so these tests exercise only in-memory selection.
        var parser = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        SetField(parser, "log", NullLogger<T>.Instance);
        SetField(parser, "activeAlarms", new List<ActiveAlarm>());
        SetField(parser, "activeProfiles", new Dictionary<int, Profile>());
        SetField(parser, "activeHolidays", new Dictionary<string, List<Holiday>>());
        SetField(parser, "clientProfileMapping", new Dictionary<string, int>());
        SetField(parser, "clientHolidayProfileMapping", new Dictionary<string, int>());
        SetField(parser, "roles", new Dictionary<string, string>());
        SetField(parser, "victims", new Dictionary<string, List<Victim>>());
        if (parser is not WorkFlowSteps)
        {
            SetField(parser, "ClearEvents", new Dictionary<int, List<ProfileItemClear>>());
            SetField(parser, "victimTypeDict", new Dictionary<string, string>());
            SetField(parser, "pnAlarms", new Dictionary<string, HashSet<string>>());
            SetField(parser, "MEZVictims", new Dictionary<string, HashSet<string>>());
            SetField(parser, "AttachedVictimZones", new Dictionary<string, Dictionary<string, HashSet<string>>>());
        }
        SetField(parser, "configuration", new ConfigurationBuilder().AddInMemoryCollection().Build());
        // ClearEvents stays empty; no action 12, role lookups, contacts, parsing,
        // notification dispatch or SQL methods are invoked.
        return parser;
    }

    private static void SetField(object parser, string name, object value) =>
        parser.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(parser, value);

    private static T Field<T>(object parser, string name) =>
        (T)parser.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parser)!;

    private static Dictionary<string, List<Holiday>> Holidays(object parser) => Field<Dictionary<string, List<Holiday>>>(parser, "activeHolidays");
    private static Dictionary<int, Profile> Profiles(object parser) => Field<Dictionary<int, Profile>>(parser, "activeProfiles");
    private static Dictionary<string, int> Mapping(object parser, bool holiday) =>
        Field<Dictionary<string, int>>(parser, holiday ? "clientHolidayProfileMapping" : "clientProfileMapping");

    private static bool IsHoliday(object parser, ActiveAlarm alarm, DateTime current) =>
        (bool)parser.GetType().GetMethod("holidayCheck", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(parser, [alarm, current])!;

    private static ProfileItem? SelectInitial(object parser, List<ProfileItem> items, DateTime current) => parser switch
    {
        WorkFlowInitiator initiator => initiator.FindStepOneProfileItem(items, current),
        WorkFlowCommon common => common.FindStepOneProfileItem(items, current),
        _ => throw new ArgumentException("Expected a normal parser", nameof(parser))
    };

    private static void Populate(object parser, List<ActiveAlarm> alarms)
    {
        object?[] arguments = parser is WorkFlowSteps ? [alarms] : [alarms, 0];
        parser.GetType().GetMethod("getPriorityAndEmail", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(parser, arguments);
        Assert.Same(alarms, arguments[0]);
    }

    private static ActiveAlarm Alarm() => new()
    {
        SystemID = 123,
        ClientID = "client",
        ClientTZ = "UTC",
        AlarmID = "event",
        POGroupNum = "",
        POGroup1 = "",
        POGroup2 = "",
        POGroup3 = ""
    };

    private static ProfileItem Item(int state, string label) => new()
    {
        StartTime = FixedDay,
        // The live selectors truncate UtcNow to seconds. Keep the 23:59:59
        // second inside the exclusive step window, including at midnight rollover.
        EndTime = FixedDay.AddHours(23).AddMinutes(59).AddSeconds(59).AddTicks(1),
        StateNo = state,
        Action = 2,
        Email = label + "@example.test",
        EmailJoin = 1,
        StateTime = 7,
        NextState = state + 1,
        Instruction = label,
        RoleID = 42,
        RoleAction = 2,
        FeedBackRequired = true,
        LoopStartState = -1,
        NumberOfLoops = 100
    };

    private static void AddProfile(object parser, ProfileItem first, int id, bool holiday, params ProfileItem[] rest)
    {
        List<ProfileItem> items = [first, .. rest];
        Mapping(parser, holiday)["client"] = id;
        Profiles(parser)[id] = new Profile
        {
            ProfileID = id,
            ProfileName = "profile-" + id,
            Events = new Dictionary<string, Dictionary<int, List<ProfileItem>>>
            {
                // Populate all weekdays to avoid a race when UtcNow crosses midnight.
                ["event"] = Enumerable.Range(1, 7).ToDictionary(day => day, _ => items)
            }
        };
    }

    private static void AssertSelection(ActiveAlarm alarm, ProfileItem item, int profileId)
    {
        Assert.Equal(item.Action, alarm.Priority);
        Assert.Equal(item.Email, alarm.EmailAddresses);
        Assert.Equal(item.EmailJoin, alarm.EmailJoin);
        Assert.Equal(item.StateNo, alarm.StateNo);
        Assert.Equal(item.StateTime, alarm.StateTime);
        Assert.Equal(item.NextState, alarm.NextStateNo);
        Assert.Equal(item.Instruction, alarm.Instruction);
        Assert.Equal("profile-" + profileId, alarm.ProfileName);
        Assert.Equal(profileId, alarm.ProfileID);
        Assert.Equal(item.RoleID, alarm.RoleID);
        Assert.Equal(item.RoleAction, alarm.RoleAction);
        Assert.Equal(item.FeedBackRequired, alarm.FeedbackREQ);
    }
}