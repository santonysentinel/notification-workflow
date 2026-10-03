using System.Collections;
using System.Data;
using System.Data.Common;
using System.Reflection;
using ActiveAlarmsParser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Parser;
using NotificationWorkflowService.Parser.ReferenceData;
using NotificationWorkflowService.Repository;
using Xunit;

namespace NotificationWorkflowService.Tests;

/// <summary>
/// In-memory reference refresh contracts. Preparation and disposal failures are
/// outside the parser retry loops; partial-read failures are tested only against
/// the pure builders. No SQL, sender, static settings, Title or sleep paths.
/// </summary>
public class WorkflowReferenceDataTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly DateTime Day = new(2026, 10, 1);
    private static readonly string[] NormalFields =
    [
        "clientProfileMapping", "clientHolidayProfileMapping", "activeProfiles", "activeHolidays",
        "roles", "victims", "victimTypeDict", "MEZVictims", "AttachedVictimZones", "ClearEvents"
    ];
    private static readonly string[] Loaders =
    [
        "FetchClientProfile:0", "FetchClientProfile:1", "readAllProfiles", "readAllHolidays",
        "readAllRoles", "readVictims", "readMEZVictims", "readAttachedVictimZones", "readProfileItemsClear"
    ];

    public static IEnumerable<object[]> RefreshPositions()
    {
        for (int kind = 0; kind < 3; kind++)
        for (int position = 0; position < Count(kind); position++)
            yield return [kind, position];
    }

    [Theory]
    [MemberData(nameof(RefreshPositions))]
    public async Task PreparationFailureAtEverySetupPositionKeepsEntireLiveSnapshotAsync(int kind, int position)
    {
        var spy = ReferenceRepository.Create();
        object parser = Parser(kind, spy);
        var old = Seed(parser, kind);
        spy.FailPrepareAt = position;
        bool? result = null;
        Exception? failure = await Record.ExceptionAsync(async () => result = await SetupAsync(parser));

        // Check rollback even when the legacy step victim preparation path rethrows.
        AssertSnapshot(parser, old);
        Assert.Null(Value(parser, "pendingReferenceData"));
        Assert.Equal(ExpectedCalls(kind).Take(position + 1), spy.Calls.Select(c => c.Key));
        Assert.Null(spy.Calls[position].Operation);
        AssertLifetimes(spy);
        await AssertDirectLoaderPublishesAsync(parser, kind, spy, old);
        if (kind == 2 && position == 5)
        {
            // Preserve the step victim loader's existing preparation rethrow;
            // setup must discard staging and clean up in either failure contract.
            Assert.IsType<ReferenceFailure>(failure);
            Assert.Null(result);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(false, result);
        }
    }

    [Theory]
    [MemberData(nameof(RefreshPositions))]
    public async Task DisposalFailureAtEverySetupPositionDiscardsEarlierStagingAsync(int kind, int position)
    {
        var spy = ReferenceRepository.Create();
        object parser = Parser(kind, spy);
        var old = Seed(parser, kind);
        spy.FailDisposeAt = position;
        ObserveLiveSnapshot(spy, parser, old);

        Assert.False(await SetupAsync(parser));

        AssertSnapshot(parser, old);
        Assert.Null(Value(parser, "pendingReferenceData"));
        Assert.Equal(ExpectedCalls(kind).Take(position + 1), spy.Calls.Select(c => c.Key));
        AssertLifetimes(spy);
        Assert.Equal((position + 1) * 2, spy.ReadObservations);
        await AssertDirectLoaderPublishesAsync(parser, kind, spy, old);
    }

    [Theory]
    [MemberData(nameof(RefreshPositions))]
    public async Task DirectLoaderDisposalFailureNeverPublishesEvenAfterFullyMappingRowsAsync(int kind, int position)
    {
        var spy = ReferenceRepository.Create();
        object parser = Parser(kind, spy);
        var old = Seed(parser, kind);
        spy.FailDisposeAt = 0;
        ObserveLiveSnapshot(spy, parser, old, pending: false);

        Assert.Equal(false, await LoadAsync(parser, Loaders[position]));

        AssertSnapshot(parser, old);
        Assert.Null(Value(parser, "pendingReferenceData"));
        Assert.Equal(ExpectedCalls(kind)[position], Assert.Single(spy.Calls).Key);
        Assert.Equal(2, spy.ReadObservations);
        AssertLifetimes(spy);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task SuccessfulSetupPublishesAllFreshDictionariesOnlyAfterAllReadersAndDisposalsAsync(int kind, bool empty)
    {
        var spy = ReferenceRepository.Create();
        spy.Empty = empty;
        object parser = Parser(kind, spy);
        var old = Seed(parser, kind);
        ObserveLiveSnapshot(spy, parser, old);

        Assert.True(await SetupAsync(parser));

        Assert.Equal(ExpectedCalls(kind), spy.Calls.Select(c => c.Key));
        Assert.Equal(Count(kind) * (empty ? 1 : 2), spy.ReadObservations);
        AssertLifetimes(spy);
        Assert.Null(Value(parser, "pendingReferenceData"));
        foreach (var pair in old)
        {
            var live = Assert.IsAssignableFrom<IDictionary>(Value(parser, pair.Key));
            Assert.NotSame(pair.Value, live);
            Assert.Equal(empty ? 0 : 1, live.Count);
            Assert.False(live.Contains(pair.Key is "activeProfiles" or "ClearEvents" ? -99 : "stale"));
            Assert.Single(pair.Value.Keys); // Old dictionaries are not cleared in place.
        }
        if (!empty)
        {
            Assert.Equal(10, Field<Dictionary<string, int>>(parser, "clientProfileMapping")["client"]);
            Assert.Equal(20, Field<Dictionary<string, int>>(parser, "clientHolidayProfileMapping")["client"]);
            Assert.Equal("profile", Field<Dictionary<int, Profile>>(parser, "activeProfiles")[10].ProfileName);
            Assert.Equal("role", Field<Dictionary<string, string>>(parser, "roles")["role-id"]);
            Assert.Equal("victim", Assert.Single(Field<Dictionary<string, List<Victim>>>(parser, "victims")["client"]).OID);
            if (kind != 2)
            {
                Assert.Equal("VAPP", Field<Dictionary<string, string>>(parser, "victimTypeDict")["victim"]);
                Assert.Contains("victim", Field<Dictionary<string, HashSet<string>>>(parser, "MEZVictims")["client"]);
                Assert.Contains("victim", Field<Dictionary<string, Dictionary<string, HashSet<string>>>>(parser, "AttachedVictimZones")["client"]["zone|category"]);
                Assert.Equal("clear", Assert.Single(Field<Dictionary<int, List<ProfileItemClear>>>(parser, "ClearEvents")[10]).ClearingEvent);
            }
        }
        var published = Snapshot(parser, kind);
        await AssertDirectLoaderPublishesAsync(parser, kind, spy, published);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SuccessfulRefreshAfterLateFailureCannotReuseAbandonedStagingAsync(int kind)
    {
        var spy = ReferenceRepository.Create();
        object parser = Parser(kind, spy);
        var old = Seed(parser, kind);
        spy.FailDisposeAt = Count(kind) - 1;
        Assert.False(await SetupAsync(parser));
        AssertSnapshot(parser, old);
        spy.FailDisposeAt = null;
        spy.Empty = true;

        Assert.True(await SetupAsync(parser));

        foreach (string name in Fields(kind))
        {
            var live = Assert.IsAssignableFrom<IDictionary>(Value(parser, name));
            Assert.NotSame(old[name], live);
            Assert.Empty(live);
        }
        Assert.Null(Value(parser, "pendingReferenceData"));
        AssertLifetimes(spy);
    }

    public static IEnumerable<object[]> BuilderCases()
    {
        foreach (string builder in new[] { "client:0", "client:1", "profiles", "holidays", "roles", "victims", "step-victims", "mez", "zones", "clear" })
            yield return [builder];
    }

    [Theory]
    [MemberData(nameof(BuilderCases))]
    public async Task EveryBuilderReturnsFreshEmptyCollectionsAndDoesNotOwnReaderAsync(string builder)
    {
        using var table = BuilderTable(builder);
        table.Clear();
        await using var first = table.CreateDataReader();
        await using var second = table.CreateDataReader();
        object a = await BuildAsync(builder, first);
        object b = await BuildAsync(builder, second);
        Assert.NotSame(a, b);
        foreach (var dict in Dictionaries(a)) Assert.Empty(dict);
        foreach (var dict in Dictionaries(b)) Assert.Empty(dict);
        Assert.False(first.IsClosed);
        Assert.False(second.IsClosed);
    }

    [Theory]
    [MemberData(nameof(BuilderCases))]
    public async Task EveryBuilderEscapesFailureAfterOneMappedRowWithoutReturningPartialResultsAsync(string builder)
    {
        using var table = BuilderTable(builder);
        await using var underlying = table.CreateDataReader();
        var reader = ReaderProxy.Create(underlying);
        var expected = new ReferenceFailure();
        reader.FailAfterFirst = expected;
        object? result = null;

        Assert.Same(expected, await Assert.ThrowsAsync<ReferenceFailure>(async () => result = await BuildAsync(builder, reader)));

        Assert.Null(result);
        Assert.Equal(1, reader.RowsRead);
        Assert.True(reader.ColumnReads > 0); // Not a failure before mapping began.
        Assert.Equal(2, reader.ReadCalls);
        Assert.Equal(0, reader.DisposeCalls);
        Assert.False(underlying.IsClosed);
        await using var retry = table.CreateDataReader();
        var rebuilt = Dictionaries(await BuildAsync(builder, retry)).ToArray();
        Assert.Single(rebuilt[0].Keys);
        if (rebuilt.Length == 2)
        {
            if (builder == "step-victims") Assert.Empty(rebuilt[1]);
            else Assert.Single(rebuilt[1].Keys);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ClientBuilderPreservesCaseEmptyKeysConversionsAndFirstDuplicateAsync(int type)
    {
        using var table = Table(["OID", "ProfileID"], ["client", "10"], ["client", "not-an-int"], ["Client", "20"], [DBNull.Value, "30"]);
        await using var reader = table.CreateDataReader();
        var actual = await WorkflowReferenceDataBuilders.BuildClientProfilesAsync(reader, type);
        Assert.Equal(3, actual.Count);
        Assert.Equal(10, actual["client"]);
        Assert.Equal(20, actual["Client"]);
        Assert.Equal(30, actual[""]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public async Task InvalidClientProfileTypeConsumesRowsWithoutReadingColumnsAsync(int type)
    {
        using var table = Table(["unrelated"], ["one"], ["two"]);
        await using var underlying = table.CreateDataReader();
        var reader = ReaderProxy.Create(underlying);
        Assert.Empty(await WorkflowReferenceDataBuilders.BuildClientProfilesAsync(reader, type));
        Assert.Equal(2, reader.RowsRead);
        Assert.Equal(0, reader.ColumnReads);
    }

    [Fact]
    public async Task ProfileBuilderMapsEveryFieldAndPreservesAllGroupingBranchesAndOrderAsync()
    {
        using var table = Profiles();
        object?[] first = table.Rows[0].ItemArray;
        table.Rows.Add(first);
        object?[] otherDay = (object?[])first.Clone(); otherDay[2] = 2; table.Rows.Add(otherDay);
        object?[] otherEvent = (object?[])first.Clone(); otherEvent[3] = "Event"; table.Rows.Add(otherEvent);
        object?[] otherProfile = (object?[])first.Clone(); otherProfile[0] = 20; otherProfile[1] = "other"; table.Rows.Add(otherProfile);
        table.Rows[1]["ProfileName"] = "ignored";
        table.Rows[1]["Instruction"] = "second";
        await using var reader = table.CreateDataReader();
        var profiles = await WorkflowReferenceDataBuilders.BuildProfilesAsync(reader);
        Assert.Equal(2, profiles.Count);
        Assert.Equal(10, profiles[10].ProfileID);
        Assert.Equal("profile", profiles[10].ProfileName);
        Assert.Equal("other", profiles[20].ProfileName);
        Assert.Equal(new[] { "instruction", "second" }, profiles[10].Events["event"][1].Select(i => i.Instruction));
        Assert.Single(profiles[10].Events["event"][2]);
        Assert.Single(profiles[10].Events["Event"][1]);
        Assert.Single(profiles[20].Events["event"][1]);
        ProfileItem item = profiles[10].Events["event"][1][0];
        Assert.Equal(10, item.ProfileID); Assert.Equal("event", item.EventCode); Assert.Equal(1, item.Day);
        Assert.Equal(TimeSpan.FromHours(8), item.StartTime.TimeOfDay); Assert.Equal(TimeSpan.FromHours(18), item.EndTime.TimeOfDay);
        Assert.Equal(3, item.Action); Assert.Equal(4, item.HoldDuration); Assert.Equal(5, item.GracePeriod);
        Assert.Equal("instruction", item.Instruction); Assert.Equal("email", item.Email); Assert.Equal(1, item.EmailJoin);
        Assert.Equal(2, item.StateNo); Assert.Equal(6, item.StateTime); Assert.True(item.FeedBackRequired);
        Assert.Equal(7, item.ProfileType); Assert.Equal(8, item.TimeIntervalsID); Assert.Equal(9, item.NextState);
        Assert.Equal(10, item.LoopStartState); Assert.Equal(11, item.NumberOfLoops); Assert.Equal(12, item.RoleID); Assert.Equal(2, item.RoleAction);
    }

    [Theory]
    [InlineData("StartTime", "8:00:00")]
    [InlineData("EndTime", "18:00")]
    [InlineData("Day", "invalid")]
    public async Task ProfileBuilderRejectsMalformedLaterRowWithoutExposingFirstProfileAsync(string column, string bad)
    {
        using var table = Profiles();
        table.Rows.Add(table.Rows[0].ItemArray);
        table.Rows[1][column] = bad;
        await using var reader = table.CreateDataReader();
        await Assert.ThrowsAsync<FormatException>(() => WorkflowReferenceDataBuilders.BuildProfilesAsync(reader));
        Assert.False(reader.IsClosed);
    }

    [Fact]
    public async Task HolidayBuilderPreservesContiguousOrderDatesDuplicatesAndEmptyGroupSentinelAsync()
    {
        using var table = Table(["POGroup", "StartDate", "EndDate", "HolidayName"],
            ["", Day, Day.AddDays(1), "empty"], ["group", Day.AddHours(23), Day.AddDays(2).AddHours(1), "first"],
            ["group", Day, Day, "duplicate"], ["Group", Day, Day, "case"]);
        await using var reader = table.CreateDataReader();
        var result = await WorkflowReferenceDataBuilders.BuildHolidaysAsync(reader);
        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "empty", "first", "duplicate" }, result["group"].Select(h => h.HolidayName));
        Assert.Equal(Day.AddHours(23), result["group"][1].StartDate);
        Assert.Equal(Day.AddDays(2).AddHours(1), result["group"][1].EndDate);
        Assert.Equal("case", Assert.Single(result["Group"]).HolidayName);
    }

    [Fact]
    public async Task HolidayBuilderRejectsRevisitedFlushedGroupAsync()
    {
        using var table = Table(["POGroup", "StartDate", "EndDate", "HolidayName"],
            ["a", Day, Day, "first"], ["b", Day, Day, "second"], ["a", Day, Day, "third"]);
        await using var reader = table.CreateDataReader();
        await Assert.ThrowsAsync<ArgumentException>(() => WorkflowReferenceDataBuilders.BuildHolidaysAsync(reader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RolesSeedIsCopiedAndNeverMutatedOnSuccessOrLaterDuplicateFailureAsync(bool fail)
    {
        var seed = new Dictionary<string, string> { ["old"] = "original" };
        using var table = Table(["SystemID", "RoleName"], ["new", "first"], [fail ? "old" : "NEW", "second"]);
        await using var reader = table.CreateDataReader();
        if (fail) await Assert.ThrowsAsync<ArgumentException>(() => WorkflowReferenceDataBuilders.BuildRolesAsync(reader, seed));
        else
        {
            var built = await WorkflowReferenceDataBuilders.BuildRolesAsync(reader, seed);
            Assert.NotSame(seed, built);
            Assert.Equal(3, built.Count);
            built["old"] = "changed";
        }
        Assert.Equal("original", seed["old"]);
        Assert.Single(seed);
        Assert.False(reader.IsClosed);
    }

    [Fact]
    public async Task RolesSeedIsUnchangedAfterPartialReaderFailureAsync()
    {
        var seed = new Dictionary<string, string> { ["old"] = "original" };
        using var table = BuilderTable("roles");
        await using var underlying = table.CreateDataReader();
        var reader = ReaderProxy.Create(underlying);
        reader.FailAfterFirst = new ReferenceFailure();
        await Assert.ThrowsAsync<ReferenceFailure>(() => WorkflowReferenceDataBuilders.BuildRolesAsync(reader, seed));
        Assert.Equal("original", Assert.Single(seed).Value);
        Assert.Equal(1, reader.RowsRead);
        Assert.Equal(0, reader.DisposeCalls);
    }

    [Fact]
    public async Task VictimBuilderRetainsDuplicatesNoncontiguousOffendersAndFirstTypeAcrossOffendersAsync()
    {
        using var table = Table(["Offender", "Victim", "EmailAddress", "CellPhone", "VictimType"],
            ["a", "victim", "first", "111", "VAPP"], ["b", "victim", "second", "222", "OTHER"],
            ["a", "victim", "third", "333", "OTHER"]);
        await using var reader = table.CreateDataReader();
        var result = await WorkflowReferenceDataBuilders.BuildVictimsAsync(reader);
        Assert.Equal(new[] { "first", "third" }, result.Victims["a"].Select(v => v.Email));
        Assert.Equal(new[] { "111", "333" }, result.Victims["a"].Select(v => v.CellPhone));
        Assert.Equal("OTHER", Assert.Single(result.Victims["b"]).VictimType);
        Assert.Equal("VAPP", Assert.Single(result.VictimTypeDict).Value);
        Assert.All(result.Victims["a"], v => Assert.Equal("victim", v.OID));
    }

    [Fact]
    public async Task StepVictimBuilderDoesNotAccessVictimTypeColumnAsync()
    {
        using var table = Table(["Offender", "Victim", "EmailAddress", "CellPhone"], ["client", "victim", "email", "cell"]);
        await using var reader = table.CreateDataReader();
        var result = await WorkflowReferenceDataBuilders.BuildVictimsAsync(reader, step: true);
        Assert.Empty(result.VictimTypeDict);
        Assert.Null(Assert.Single(result.Victims["client"]).VictimType);
    }

    [Fact]
    public async Task MezAndZoneBuildersDeduplicateOnlyExactKeysAndPreserveLegacyCompositeCollisionAsync()
    {
        using var mez = Table(["Offender", "Victim"], ["a", "v"], ["b", "v"], ["a", "v"], ["a", "V"]);
        await using var mezReader = mez.CreateDataReader();
        var victims = await WorkflowReferenceDataBuilders.BuildMezVictimsAsync(mezReader);
        Assert.Equal(2, victims["a"].Count); Assert.Single(victims["b"]);
        using var zones = Table(["ZoneID", "ZoneCategory", "OffenderID", "VictimID"],
            ["z|c", "d", "a", "v"], ["z", "c|d", "a", "v"], ["z", "c|d", "a", "V"], ["z", "C|d", "a", "v"]);
        await using var zoneReader = zones.CreateDataReader();
        var attached = await WorkflowReferenceDataBuilders.BuildAttachedVictimZonesAsync(zoneReader);
        Assert.Equal(2, attached["a"].Count);
        Assert.Equal(2, attached["a"]["z|c|d"].Count);
        Assert.Single(attached["a"]["z|C|d"]);
    }

    [Fact]
    public async Task ClearBuilderRetainsDuplicateRowsOrderAndConvertedProfileIdsAsync()
    {
        using var table = Table(["ProfileID", "ClearingEvent", "EventCode"], ["10", "first", "event"], ["20", "other", "Event"], ["10", "first", "event"], ["10", "last", "Event"]);
        await using var reader = table.CreateDataReader();
        var result = await WorkflowReferenceDataBuilders.BuildClearEventsAsync(reader);
        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "first", "first", "last" }, result[10].Select(i => i.ClearingEvent));
        Assert.Equal(new[] { "event", "event", "Event" }, result[10].Select(i => i.EventCode));
    }

    public static IEnumerable<object[]> HolidayCases()
    {
        foreach (string group in new[] { "POGroupNum", "POGroup1", "POGroup2", "POGroup3" })
        foreach (int offset in new[] { -1, 0, 2, 3 })
            yield return [group, offset];
    }

    [Theory]
    [MemberData(nameof(HolidayCases))]
    public void PureHolidayResolverChecksAllGroupsAndInclusiveDates(string group, int offset)
    {
        var alarm = Alarm();
        typeof(ActiveAlarm).GetProperty(group)!.SetValue(alarm, "group");
        var holidays = new Dictionary<string, List<Holiday>> { ["group"] = [new() { StartDate = Day.AddHours(23), EndDate = Day.AddDays(2).AddHours(1) }] };
        Assert.Equal(offset is 0 or 2, WorkflowProfileResolver.IsHoliday(alarm, Day.AddDays(offset).AddHours(12), holidays));
    }

    [Theory]
    [InlineData("group", true)]
    [InlineData("Group", false)]
    [InlineData(" group ", false)]
    [InlineData("   ", false)]
    [InlineData("missing", false)]
    public void PureHolidayResolverPreservesCaseAndUsesTrimOnlyForEmptiness(string group, bool expected)
    {
        var alarm = Alarm(); alarm.POGroupNum = group;
        Assert.Equal(expected, WorkflowProfileResolver.IsHoliday(alarm, Day, Holidays()));
    }

    [Theory]
    [InlineData("group")]
    [InlineData("list")]
    [InlineData("item")]
    public void HolidayMatchDoesNotHideLaterInvalidGroupListOrItem(string invalid)
    {
        var alarm = Alarm(); alarm.POGroupNum = "group";
        var holidays = Holidays();
        if (invalid == "group") alarm.POGroup3 = null!;
        else
        {
            alarm.POGroup3 = "later";
            holidays["later"] = invalid == "list" ? null! : [null!];
        }
        Assert.Throws<NullReferenceException>(() => WorkflowProfileResolver.IsHoliday(alarm, Day, holidays));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void ProfileResolutionUsesOnlySelectedMappingAndNeverFallsBack(bool holiday, int missing)
    {
        var alarm = Alarm(); alarm.POGroupNum = holiday ? "group" : "";
        var normal = new Dictionary<string, int> { ["client"] = 10 };
        var special = new Dictionary<string, int> { ["client"] = 20 };
        var profiles = new Dictionary<int, Profile> { [10] = new() { ProfileID = 10 }, [20] = new() { ProfileID = 20 } };
        if (missing == 1) (holiday ? special : normal).Clear();
        if (missing == 2) profiles.Remove(holiday ? 20 : 10);
        Profile? resolved = WorkflowProfileResolver.ResolveProfile(alarm, Day, normal, special, profiles, Holidays());
        if (missing != 0) Assert.Null(resolved);
        else Assert.Same(profiles[holiday ? 20 : 10], resolved);
        Assert.Equal(7, alarm.CurrentLoopNumber);
        Assert.Equal(2, alarm.StateNo);
    }

    [Fact]
    public void ProfileResolutionPreservesClientCaseAndHasNoDefaultProfile()
    {
        var alarm = Alarm(); alarm.ClientID = "Client";
        Assert.Null(WorkflowProfileResolver.ResolveProfile(alarm, Day, new Dictionary<string, int> { ["client"] = 10 },
            new Dictionary<string, int>(), new Dictionary<int, Profile> { [10] = new() }, new Dictionary<string, List<Holiday>>()));
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(60, true, false)]
    [InlineData(-1, false, false)]
    [InlineData(61, false, false)]
    public void InitialAndStepSelectionHaveDifferentEndBoundaries(int minutes, bool initial, bool step)
    {
        var item = Item(2); item.StartTime = Day.AddHours(9); item.EndTime = Day.AddHours(10);
        DateTime current = Day.AddHours(9).AddMinutes(minutes);
        Assert.Same(initial ? item : null, WorkflowProfileResolver.FindInitialProfileItem([item], current));
        var alarm = Alarm(); alarm.CurrentLoopNumber = 0;
        Assert.Same(step ? item : null, WorkflowProfileResolver.FindStepProfileItem(alarm, [item], current));
        Assert.Equal(0, alarm.CurrentLoopNumber);
    }

    [Fact]
    public void InitialSelectionPrefersZeroThenMinimumAndFirstEligibleDuplicateWithoutSortingInput()
    {
        var zero = Item(0); var negative = Item(-2); var first = Item(2); var duplicate = Item(2);
        List<ProfileItem> items = [Item(8), first, duplicate, negative, zero];
        var original = items.ToArray();
        Assert.Same(zero, WorkflowProfileResolver.FindInitialProfileItem(items, Day.AddHours(12)));
        items.Remove(zero);
        Assert.Same(negative, WorkflowProfileResolver.FindInitialProfileItem(items, Day.AddHours(12)));
        items.Remove(negative);
        Assert.Same(first, WorkflowProfileResolver.FindInitialProfileItem(items, Day.AddHours(12)));
        Assert.Equal(original.Take(3), items);
        Assert.Null(WorkflowProfileResolver.FindInitialProfileItem([], Day));
    }

    [Fact]
    public void ZeroLengthWindowIsInitialOnlyAndOvernightWindowsDoNotWrap()
    {
        var instant = Item(2); instant.StartTime = instant.EndTime = Day.AddHours(12);
        Assert.Same(instant, WorkflowProfileResolver.FindInitialProfileItem([instant], Day.AddHours(12)));
        Assert.Null(WorkflowProfileResolver.FindStepProfileItem(Alarm(), [instant], Day.AddHours(12)));
        var overnight = Item(2); overnight.StartTime = Day.AddHours(23); overnight.EndTime = Day.AddHours(1);
        Assert.Null(WorkflowProfileResolver.FindInitialProfileItem([overnight], Day));
        Assert.Null(WorkflowProfileResolver.FindStepProfileItem(Alarm(), [overnight], Day));
    }

    [Theory]
    [InlineData(0, 2, 5, 1, false)]
    [InlineData(4, 2, 5, -1, true)]
    [InlineData(6, 1, 5, -1, true)]
    [InlineData(-1, 2, 5, 0, false)]
    public void StepSelectionIncrementsOnlyLoopStartAndTransitionsAtOrAboveLimit(int loop, int loopStart, int limit, int expectedLoop, bool transition)
    {
        var alarm = Alarm(); alarm.CurrentLoopNumber = loop;
        var current = Item(2); current.LoopStartState = loopStart; current.NumberOfLoops = limit;
        var sameLoop = Item(3); sameLoop.LoopStartState = loopStart;
        var next = Item(4); next.LoopStartState = -1;
        List<ProfileItem> items = [next, sameLoop, current];
        Assert.Same(transition ? next : current, WorkflowProfileResolver.FindStepProfileItem(alarm, items, Day.AddHours(12)));
        Assert.Equal(expectedLoop, alarm.CurrentLoopNumber);
        Assert.Equal(2, alarm.StateNo); Assert.Equal("unchanged", alarm.Instruction);
        Assert.Equal(new[] { next, sameLoop, current }, items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedStepWithoutEligibleSuccessorReturnsNullButRetainsIncrement(bool atStart)
    {
        var alarm = Alarm(); alarm.CurrentLoopNumber = 3;
        var current = Item(2); current.NumberOfLoops = 3; current.LoopStartState = atStart ? 2 : 1;
        var sameLoop = Item(3); sameLoop.LoopStartState = current.LoopStartState;
        Assert.Null(WorkflowProfileResolver.FindStepProfileItem(alarm, [sameLoop, current], Day.AddHours(12)));
        Assert.Equal(atStart ? 4 : 3, alarm.CurrentLoopNumber);
        Assert.Equal(2, alarm.StateNo);
    }

    [Fact]
    public void StepSelectionIsStableChoosesLowestEligibleSuccessorAndFirstDuplicate()
    {
        var alarm = Alarm(); alarm.CurrentLoopNumber = 3;
        var current = Item(2); current.LoopStartState = 1; current.NumberOfLoops = 3;
        var first = Item(5); first.LoopStartState = 2;
        var duplicate = Item(5); duplicate.LoopStartState = -1;
        var excluded = Item(3); excluded.LoopStartState = 1;
        List<ProfileItem> items = [Item(8), first, duplicate, excluded, current];
        var before = items.ToArray();
        Assert.Same(first, WorkflowProfileResolver.FindStepProfileItem(alarm, items, Day.AddHours(12)));
        Assert.Equal(-1, alarm.CurrentLoopNumber);
        Assert.Equal(before, items);
        alarm.CurrentLoopNumber = 0;
        current.NumberOfLoops = 10;
        var otherCurrent = Item(2);
        Assert.Same(current, WorkflowProfileResolver.FindStepProfileItem(alarm, [current, otherCurrent], Day.AddHours(12)));
        alarm.StateNo = 99;
        Assert.Null(WorkflowProfileResolver.FindStepProfileItem(alarm, items, Day.AddHours(12)));
        Assert.Equal(0, alarm.CurrentLoopNumber);
    }

    private static int Count(int kind) => kind == 2 ? 6 : 9;
    private static IEnumerable<string> Fields(int kind) => kind == 2 ? NormalFields.Take(6) : NormalFields;
    private static string[] ExpectedCalls(int kind) => Loaders.Take(Count(kind)).Select(PrepareKey).ToArray();
    private static string PrepareKey(string loader)
    {
        string[] parts = loader.Split(':');
        return "Prepare" + char.ToUpperInvariant(parts[0][0]) + parts[0][1..] + (parts.Length == 2 ? ":" + parts[1] : "");
    }
    private static Task<object?> LoadAsync(object parser, string loader)
    {
        string[] parts = loader.Split(':');
        return WorkflowRepositoryAdapterTests.CallAsync(parser, parts[0], parts.Length == 2 ? [int.Parse(parts[1])] : []);
    }
    private static async Task<bool> SetupAsync(object parser) => (bool)(await WorkflowRepositoryAdapterTests.CallAsync(parser, "setUpParser", "Offline"))!;
    private static FieldInfo FieldInfo(object parser, string name)
    {
        for (Type? type = parser.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, Instance | BindingFlags.DeclaredOnly) is { } field)
                return field;
        throw new MissingFieldException(parser.GetType().FullName, name);
    }
    private static object? Value(object parser, string field) => FieldInfo(parser, field).GetValue(parser);
    private static T Field<T>(object parser, string field) => (T)Value(parser, field)!;
    private static object Parser(int kind, ReferenceRepository spy)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:connstr"] = "Server=offline.invalid;Database=Offline;Integrated Security=true;TrustServerCertificate=true",
            ["ParserID"] = "47", ["OfflineParserID"] = "47", ["DefaultMcAppFlag"] = "0"
        }).Build();
        return kind switch
        {
            0 => new WorkFlowCommon(NullLogger<WorkFlowCommon>.Instance, configuration, null!, (IRepository)spy),
            1 => new WorkFlowInitiator(NullLogger<WorkFlowInitiator>.Instance, configuration, null!, (IRepository)spy),
            2 => new WorkFlowSteps(NullLogger<WorkFlowSteps>.Instance, configuration, (IRepository)spy),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
    private static Dictionary<string, IDictionary> Snapshot(object parser, int kind) => Fields(kind).ToDictionary(n => n, n => (IDictionary)Value(parser, n)!);
    private static Dictionary<string, IDictionary> Seed(object parser, int kind)
    {
        var snapshot = Snapshot(parser, kind);
        foreach (var pair in snapshot)
        {
            object key = pair.Key is "activeProfiles" or "ClearEvents" ? -99 : "stale";
            object value = pair.Key switch
            {
                "clientProfileMapping" or "clientHolidayProfileMapping" => -99,
                "activeProfiles" => new Profile { ProfileID = -99, ProfileName = "stale" },
                "activeHolidays" => new List<Holiday> { new() { HolidayName = "stale", StartDate = Day, EndDate = Day } },
                "roles" or "victimTypeDict" => "stale-value",
                "victims" => new List<Victim> { new() { OID = "stale" } },
                "MEZVictims" => new HashSet<string> { "stale" },
                "AttachedVictimZones" => new Dictionary<string, HashSet<string>> { ["stale"] = ["stale"] },
                "ClearEvents" => new List<ProfileItemClear> { new() { EventCode = "stale" } },
                _ => throw new InvalidOperationException(pair.Key)
            };
            pair.Value.Add(key, value);
        }
        return snapshot;
    }
    private static void AssertSnapshot(object parser, Dictionary<string, IDictionary> old)
    {
        foreach (var pair in old)
        {
            Assert.Same(pair.Value, Value(parser, pair.Key));
            Assert.Single(pair.Value.Keys);
            object key = pair.Key is "activeProfiles" or "ClearEvents" ? -99 : "stale";
            Assert.True(pair.Value.Contains(key));
            object? value = pair.Value[key];
            switch (pair.Key)
            {
                case "clientProfileMapping":
                case "clientHolidayProfileMapping": Assert.Equal(-99, value); break;
                case "activeProfiles": Assert.Equal("stale", Assert.IsType<Profile>(value).ProfileName); break;
                case "activeHolidays": Assert.Equal("stale", Assert.Single(Assert.IsType<List<Holiday>>(value)).HolidayName); break;
                case "roles":
                case "victimTypeDict": Assert.Equal("stale-value", value); break;
                case "victims": Assert.Equal("stale", Assert.Single(Assert.IsType<List<Victim>>(value)).OID); break;
                case "MEZVictims": Assert.Equal("stale", Assert.Single(Assert.IsType<HashSet<string>>(value))); break;
                case "AttachedVictimZones":
                    var zones = Assert.IsType<Dictionary<string, HashSet<string>>>(value);
                    Assert.Equal("stale", Assert.Single(zones).Key);
                    Assert.Equal("stale", Assert.Single(zones["stale"]));
                    break;
                case "ClearEvents": Assert.Equal("stale", Assert.Single(Assert.IsType<List<ProfileItemClear>>(value)).EventCode); break;
            }
        }
    }
    private static void ObserveLiveSnapshot(ReferenceRepository spy, object parser, Dictionary<string, IDictionary> old, bool pending = true)
    {
        void Observe()
        {
            AssertSnapshot(parser, old);
            if (pending) Assert.NotNull(Value(parser, "pendingReferenceData"));
            else Assert.Null(Value(parser, "pendingReferenceData"));
        }
        spy.OnRead = Observe;
        spy.OnDispose = Observe;
    }
    private static async Task AssertDirectLoaderPublishesAsync(object parser, int kind, ReferenceRepository spy, Dictionary<string, IDictionary> before)
    {
        spy.OnRead = null; spy.OnDispose = null; spy.FailPrepareAt = null; spy.FailDisposeAt = null; spy.Empty = false;
        Assert.Equal(true, await LoadAsync(parser, "FetchClientProfile:0"));
        Assert.NotSame(before["clientProfileMapping"], Value(parser, "clientProfileMapping"));
        Assert.Equal(10, Assert.Single(Field<Dictionary<string, int>>(parser, "clientProfileMapping")).Value);
        foreach (string name in Fields(kind).Where(n => n != "clientProfileMapping")) Assert.Same(before[name], Value(parser, name));
        Assert.Null(Value(parser, "pendingReferenceData"));
    }
    private static void AssertLifetimes(ReferenceRepository spy)
    {
        foreach (var call in spy.Calls)
        {
            if (call.Operation is not { } op) continue;
            Assert.Equal(new[] { "Open", "ExecuteReader", "Dispose" }, op.Events);
            Assert.True(op.Reader!.IsClosed);
            Assert.Equal(1, op.DisposeCalls);
        }
        Assert.Equal(spy.Calls.SelectMany(c => c.Operation == null ? new[] { c.Key + ":Prepare" }
            : new[] { c.Key + ":Prepare", c.Key + ":Open", c.Key + ":ExecuteReader", c.Key + ":Dispose" }), spy.Events);
    }
    private static ActiveAlarm Alarm() => new() { ClientID = "client", POGroupNum = "", POGroup1 = "", POGroup2 = "", POGroup3 = "", StateNo = 2, CurrentLoopNumber = 7, Instruction = "unchanged" };
    private static ProfileItem Item(int state) => new() { StateNo = state, StartTime = Day.AddHours(8), EndTime = Day.AddHours(18), LoopStartState = -1, NumberOfLoops = 100 };
    private static Dictionary<string, List<Holiday>> Holidays() => new() { ["group"] = [new() { StartDate = Day, EndDate = Day }] };
    private static IEnumerable<IDictionary> Dictionaries(object result) => result is WorkflowVictimReferenceData victims
        ? new IDictionary[] { victims.Victims, victims.VictimTypeDict } : [(IDictionary)result];
    private static async Task<object> BuildAsync(string builder, DbDataReader reader) => builder switch
    {
        "client:0" => await WorkflowReferenceDataBuilders.BuildClientProfilesAsync(reader, 0),
        "client:1" => await WorkflowReferenceDataBuilders.BuildClientProfilesAsync(reader, 1),
        "profiles" => await WorkflowReferenceDataBuilders.BuildProfilesAsync(reader),
        "holidays" => await WorkflowReferenceDataBuilders.BuildHolidaysAsync(reader),
        "roles" => await WorkflowReferenceDataBuilders.BuildRolesAsync(reader),
        "victims" => await WorkflowReferenceDataBuilders.BuildVictimsAsync(reader),
        "step-victims" => await WorkflowReferenceDataBuilders.BuildVictimsAsync(reader, step: true),
        "mez" => await WorkflowReferenceDataBuilders.BuildMezVictimsAsync(reader),
        "zones" => await WorkflowReferenceDataBuilders.BuildAttachedVictimZonesAsync(reader),
        "clear" => await WorkflowReferenceDataBuilders.BuildClearEventsAsync(reader),
        _ => throw new InvalidOperationException(builder)
    };
    private static DataTable BuilderTable(string builder) => Rows(builder switch
    {
        "client:0" => "PrepareFetchClientProfile:0", "client:1" => "PrepareFetchClientProfile:1",
        "profiles" => "PrepareReadAllProfiles", "holidays" => "PrepareReadAllHolidays", "roles" => "PrepareReadAllRoles",
        "victims" or "step-victims" => "PrepareReadVictims", "mez" => "PrepareReadMEZVictims",
        "zones" => "PrepareReadAttachedVictimZones", "clear" => "PrepareReadProfileItemsClear",
        _ => throw new InvalidOperationException(builder)
    });
    private static DataTable Rows(string key) => key switch
    {
        "PrepareFetchClientProfile:0" => Table(["OID", "ProfileID"], ["client", 10]),
        "PrepareFetchClientProfile:1" => Table(["OID", "ProfileID"], ["client", 20]),
        "PrepareReadAllProfiles" => Profiles(),
        "PrepareReadAllHolidays" => Table(["POGroup", "StartDate", "EndDate", "HolidayName"], ["group", Day, Day, "holiday"]),
        "PrepareReadAllRoles" => Table(["SystemID", "RoleName"], ["role-id", "role"]),
        "PrepareReadVictims" => Table(["Offender", "Victim", "EmailAddress", "CellPhone", "VictimType"], ["client", "victim", "email", "cell", "VAPP"]),
        "PrepareReadMEZVictims" => Table(["Offender", "Victim"], ["client", "victim"]),
        "PrepareReadAttachedVictimZones" => Table(["ZoneID", "ZoneCategory", "OffenderID", "VictimID"], ["zone", "category", "client", "victim"]),
        "PrepareReadProfileItemsClear" => Table(["ProfileID", "ClearingEvent", "EventCode"], [10, "clear", "event"]),
        _ => throw new InvalidOperationException("Unexpected repository call: " + key)
    };
    private static DataTable Profiles() => Table(
        ["ProfileID", "ProfileName", "Day", "EventCode", "StartTime", "EndTime", "Action", "HoldDuration", "GracePeriod", "Instruction", "EmailAddress", "EmailJoin", "StateNo", "StateTime", "FeedbackRequired", "ProfileType", "TimeIntervalsID", "NextState", "LoopStartState", "NumberOfLoops", "RoleID", "RoleAction"],
        [10, "profile", "1", "event", "08:00:00", "18:00:00", 3, 4, 5, "instruction", "email", 1, 2, 6, true, 7, 8, 9, 10, 11, 12, 2]);
    private static DataTable Table(string[] columns, params object[][] rows)
    {
        var table = new DataTable();
        foreach (string column in columns) table.Columns.Add(column, typeof(object));
        foreach (object[] row in rows) table.Rows.Add(row);
        return table;
    }

    public sealed class ReferenceFailure : Exception;
    public sealed class ReferenceCall(string key)
    {
        public string Key { get; } = key;
        public ReferenceOperation? Operation { get; set; }
    }
    public class ReferenceRepository : DispatchProxy
    {
        public List<ReferenceCall> Calls { get; } = [];
        public List<string> Events { get; } = [];
        public int? FailPrepareAt { get; set; }
        public int? FailDisposeAt { get; set; }
        public bool Empty { get; set; }
        public Action? OnRead { get; set; }
        public Action? OnDispose { get; set; }
        public int ReadObservations { get; set; }
        public static ReferenceRepository Create() => (ReferenceRepository)DispatchProxy.Create<IRepository, ReferenceRepository>();
        protected override object Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.NotNull(method);
            Assert.Equal(typeof(IWorkflowOperation), method.ReturnType);
            string key = method.Name + (method.Name == "PrepareFetchClientProfile" ? ":" + Assert.Single(args!) : "");
            int position = Calls.Count;
            var call = new ReferenceCall(key); Calls.Add(call); Events.Add(key + ":Prepare");
            if (position == FailPrepareAt) throw new ReferenceFailure();
            var rows = Rows(key);
            if (Empty) rows.Clear();
            return call.Operation = new ReferenceOperation(this, call, rows, position == FailDisposeAt);
        }
    }
    public sealed class ReferenceOperation(ReferenceRepository spy, ReferenceCall call, DataTable table, bool failDispose) : IWorkflowOperation
    {
        public List<string> Events { get; } = [];
        public DataTableReader? Reader { get; private set; }
        public int DisposeCalls { get; private set; }
        public Task OpenAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Assert.Empty(Events); Track("Open"); return Task.CompletedTask; }
        public Task<DbDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(new[] { "Open" }, Events); Track("ExecuteReader");
            Reader = table.CreateDataReader();
            var proxy = ReaderProxy.Create(Reader);
            proxy.OnRead = () => { spy.ReadObservations++; spy.OnRead?.Invoke(); };
            return Task.FromResult<DbDataReader>(proxy);
        }
        public Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Reference readers must not write");
        public Task CloseAsync() => throw new InvalidOperationException("Unexpected Close/retry");
        public async ValueTask DisposeAsync()
        {
            Assert.Equal(0, DisposeCalls++); Track("Dispose");
            // Observe old live dictionaries even after the last row has been consumed.
            try { spy.OnDispose?.Invoke(); }
            finally { if (Reader is not null) await Reader.DisposeAsync(); table.Dispose(); }
            if (failDispose) throw new ReferenceFailure();
        }
        private void Track(string name) { Events.Add(name); spy.Events.Add(call.Key + ":" + name); }
    }
    public sealed class ReaderProxy(DbDataReader inner) : DbDataReader
    {
        public Action? OnRead { get; set; }
        public Exception? FailAfterFirst { get; set; }
        public int RowsRead { get; private set; }
        public int ReadCalls { get; private set; }
        public int ColumnReads { get; private set; }
        public int DisposeCalls { get; private set; }
        public static ReaderProxy Create(DbDataReader reader) => new(reader);
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++; OnRead?.Invoke();
            if (RowsRead == 1 && FailAfterFirst is { } failure) throw failure;
            bool read = await inner.ReadAsync(cancellationToken);
            if (read) RowsRead++;
            return read;
        }
        public override bool Read() => throw new InvalidOperationException("Builders must read asynchronously");
        public override object this[int ordinal] { get { ColumnReads++; return inner[ordinal]; } }
        public override object this[string name] { get { ColumnReads++; return inner[name]; } }
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
        public override void Close() { DisposeCalls++; inner.Close(); }
        public override async ValueTask DisposeAsync() { DisposeCalls++; await inner.DisposeAsync(); }
    }
}