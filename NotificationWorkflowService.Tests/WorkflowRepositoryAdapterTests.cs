using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using ActiveAlarmsParser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Parser;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service;
using Xunit;

namespace NotificationWorkflowService.Tests;

/// <summary>
/// Runtime adapter tests only: no real repository, SQL connection, sender, credentials,
/// static notification settings, culture changes or retry/sleep paths. Normal readPoints
/// is deliberately excluded because it writes the process-wide Console.Title.
/// </summary>
public class WorkflowRepositoryAdapterTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly DateTime Expiry = new(2026, 10, 1, 12, 34, 56, DateTimeKind.Utc);

    public static IEnumerable<object[]> AdapterCases()
    {
        for (int kind = 0; kind < 3; kind++)
        {
            foreach (string helper in new[]
            {
                "FetchClientProfile:0", "FetchClientProfile:1", "readAllProfiles", "readAllHolidays",
                "readAllRoles", "readVictims", "readRoles:1", "readRoles:2", "readRoles:3",
                "getInsertEmails", "getClientEmail", "getClientText", "PushAlertToMcApp",
                "AddToNotificationQueue:1", "AddToNotificationQueue:2", "AddToNotificationQueue:3",
                "insertNotificationQueueVictim:0", "insertNotificationQueueVictim:1",
                "CreateAlarmAudit", "AddActiveAlarmActionToActivity", "insertIntoAlarmNotification",
                "insertIntoCurrentAlarmNotification"
            })
                yield return [kind, helper];

            if (kind == 2)
                yield return [kind, "getExpiredAlarms"];
            else
                foreach (string helper in new[]
                {
                    "readMEZVictims", "readAttachedVictimZones", "readProfileItemsClear",
                    "insertPushNotificationQueue", "ClearMcAppAlarm", "readLastSuccessfulProcess",
                    "ReadCurrentActiveAlarmPoint", "updateParserActivty", "updateParserActivty:null"
                })
                    yield return [kind, helper];
        }
    }

    [Theory]
    [MemberData(nameof(AdapterCases))]
    public void HelpersPassArgumentsAndOwnExactlyOneOperation(int kind, string helper)
    {
        var spy = RepositorySpy.Create();
        object parser = Parser(kind, spy);
        var alarm = Alarm();
        var (method, prepare, args, expected) = Adapter(helper, alarm);
        DateTime before = DateTime.UtcNow;

        object? result = Call(parser, method, args);

        var call = Assert.Single(spy.Calls);
        Assert.Equal(prepare, call.Name);
        if (method == "getExpiredAlarms")
        {
            var time = Assert.IsType<DateTime>(Assert.Single(call.Arguments));
            Assert.Equal(DateTimeKind.Utc, time.Kind);
            Assert.InRange(time, before, DateTime.UtcNow);
            Assert.Empty(Field<List<ActiveAlarm>>(parser, "activeAlarms"));
        }
        else
            AssertArguments(expected, call.Arguments);

        Type returnType = Method(parser, method).ReturnType;
        if (returnType == typeof(bool)) Assert.Equal(true, result);
        else if (returnType == typeof(int)) Assert.Equal(0, result);
        else if (returnType == typeof(string)) Assert.Equal("", result);
        else Assert.Null(result);
        AssertLifetime(spy);
    }

    [Theory]
    [MemberData(nameof(AdapterCases))]
    public void PreparationFailuresPreserveHelperSwallowAndRethrowContracts(int kind, string helper)
    {
        var spy = RepositorySpy.Create();
        object parser = Parser(kind, spy);
        var (method, prepare, args, expected) = Adapter(helper, Alarm());
        var failure = new PreparationException();
        spy.PreparationFailure = _ => failure;

        if (Rethrows(kind, method))
        {
            var exception = Assert.Throws<TargetInvocationException>(() => Call(parser, method, args));
            Assert.Same(failure, exception.InnerException);
        }
        else
        {
            object? result = Call(parser, method, args);
            Type returnType = Method(parser, method).ReturnType;
            if (returnType == typeof(bool)) Assert.Equal(false, result);
            else if (returnType == typeof(string)) Assert.Equal("", result);
            else Assert.Null(result);
        }

        var call = Assert.Single(spy.Calls);
        Assert.Equal(prepare, call.Name);
        if (method != "getExpiredAlarms") AssertArguments(expected, call.Arguments);
        Assert.Null(call.Operation); // Preparation never returned an owned resource.
        Assert.Equal(new[] { prepare + ":Prepare" }, spy.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PublicConstructorRetainsInjectedReadonlyRepositoryWithoutPreparingAnything(int kind)
    {
        var spy = RepositorySpy.Create();
        object target = kind == 3 ? Service(spy) : Parser(kind, spy);
        var field = FieldInfo(target, "repository");
        Assert.True(field.IsInitOnly);
        Assert.Same(spy, field.GetValue(target));
        Assert.Contains(target.GetType().GetConstructors(), ctor =>
            ctor.IsPublic && ctor.GetParameters().Any(p => p.ParameterType == typeof(IRepository)));
        Assert.Empty(spy.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IsolatedUninitializedActionHelpersNeedOnlyInstanceRepositoryAndLogger(int kind)
    {
        var spy = RepositorySpy.Create();
        object parser = RuntimeHelpers.GetUninitializedObject(ParserType(kind));
        SetField(parser, "repository", spy);
        SetField(parser, "log", Logger(kind));
        // No constructor, configuration, static notification initialization or sender is needed.
        var alarm = Alarm();
        Assert.Equal(true, Call(parser, "PushAlertToMcApp", alarm));
        Assert.Equal(true, Call(parser, "AddToNotificationQueue", alarm, 3));
        Call(parser, "CreateAlarmAudit", 14, "action", 29, 4);
        Assert.Equal(new[] { "PreparePushAlertToMcApp", "PrepareAddToNotificationQueue", "PrepareCreateAlarmAudit" },
            spy.Calls.Select(c => c.Name));
        AssertLifetime(spy);
    }

    public static IEnumerable<object[]> ContactCases()
    {
        for (int kind = 0; kind < 3; kind++)
        {
            yield return [kind, "getInsertEmails", "POMSGAddress", "a,a,b"];
            yield return [kind, "getClientEmail", "EmailAddress", "aab"];
            yield return [kind, "getClientText", "Cell", "aab"];
        }
    }

    [Theory]
    [MemberData(nameof(ContactCases))]
    public void ContactRowsRetainDuplicatesAndOriginalConcatenation(int kind, string method, string column, string expected)
    {
        var spy = RepositorySpy.Create();
        spy.Rows = _ => Table([column], ["a"], ["a"], ["b"]);
        object parser = Parser(kind, spy);
        var alarm = Alarm();

        Assert.Equal(expected, Call(parser, method, alarm));

        Assert.Same(alarm, Assert.Single(Assert.Single(spy.Calls).Arguments));
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public void RoleRowsOverwriteContactsButAppendCallInstructions(int kind, int action)
    {
        var spy = RepositorySpy.Create();
        spy.Rows = _ => Table(["EmailAddress", "MessageAddress", "RoleName", "OfficePhone"],
            ["first-email", "first-text", "first-role", "111"],
            ["last-email", "last-text", "last-role", "222"]);
        object parser = Parser(kind, spy);
        var alarm = Alarm();

        Assert.Equal(true, Call(parser, "readRoles", alarm, 43, action));

        AssertArguments([alarm, 43, action], Assert.Single(spy.Calls).Arguments);
        if (action == 1)
            Assert.Equal("instruction Please Call first-role And Inform The Following Phone Number(s) 111" +
                " Please Call last-role And Inform The Following Phone Number(s) 222", alarm.Instruction);
        else
            Assert.Equal(action == 2 ? "last-email" : "last-text", alarm.EmailAddresses);
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ClientMappingsAreRebuiltAndFirstDuplicateWinsForEachProfileType(int kind)
    {
        var spy = RepositorySpy.Create();
        spy.Rows = _ => Table(["OID", "ProfileID"], ["client", 10], ["client", 20], ["other", 30]);
        object parser = Parser(kind, spy);
        foreach (int profileType in new[] { 0, 1 })
        {
            string field = profileType == 0 ? "clientProfileMapping" : "clientHolidayProfileMapping";
            Field<Dictionary<string, int>>(parser, field)["stale"] = 99;
            Assert.Equal(true, Call(parser, "FetchClientProfile", profileType));
            var mapping = Field<Dictionary<string, int>>(parser, field);
            Assert.Equal(2, mapping.Count);
            Assert.Equal(10, mapping["client"]);
            Assert.Equal(30, mapping["other"]);
            Assert.False(mapping.ContainsKey("stale"));
        }
        Assert.Equal(new object?[] { 0, 1 }, spy.Calls.Select(c => c.Arguments[0]));
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProfileRowsBuildEveryBranchRetainDuplicateItemsAndFirstProfileName(int kind)
    {
        var spy = RepositorySpy.Create();
        spy.Rows = _ => ProfileRows();
        object parser = Parser(kind, spy);

        Assert.Equal(true, Call(parser, "readAllProfiles"));

        var profiles = Field<Dictionary<int, Profile>>(parser, "activeProfiles");
        Assert.Equal(2, profiles.Count);
        Assert.Equal("first-name", profiles[10].ProfileName);
        var items = profiles[10].Events["event"][1];
        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal(10, item.ProfileID);
            Assert.Equal("event", item.EventCode);
            Assert.Equal(TimeSpan.FromHours(8), item.StartTime.TimeOfDay);
            Assert.Equal(TimeSpan.FromHours(18), item.EndTime.TimeOfDay);
            Assert.Equal(3, item.Action);
            Assert.Equal(4, item.HoldDuration);
            Assert.Equal(5, item.GracePeriod);
            Assert.Equal("instruction", item.Instruction);
            Assert.Equal("email", item.Email);
            Assert.Equal(1, item.EmailJoin);
            Assert.Equal(2, item.StateNo);
            Assert.Equal(6, item.StateTime);
            Assert.True(item.FeedBackRequired);
            Assert.Equal(7, item.ProfileType);
            Assert.Equal(8, item.TimeIntervalsID);
            Assert.Equal(9, item.NextState);
            Assert.Equal(10, item.LoopStartState);
            Assert.Equal(11, item.NumberOfLoops);
            Assert.Equal(12, item.RoleID);
            Assert.Equal(2, item.RoleAction);
        });
        Assert.Single(profiles[10].Events["event"][2]);
        Assert.Single(profiles[10].Events["other-event"][1]);
        Assert.Single(profiles[20].Events["event"][1]);
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void VictimRowsRetainDuplicatesWhileNormalVictimTypeIsFirstWins(int kind)
    {
        var spy = RepositorySpy.Create();
        spy.Rows = _ => Table(["Offender", "Victim", "EmailAddress", "CellPhone", "VictimType"],
            ["client", "victim", "first", "111", "VAPP"],
            ["client", "victim", "second", "222", "OTHER"],
            ["other", "victim", "third", "333", "OTHER"]);
        object parser = Parser(kind, spy);

        Assert.Equal(true, Call(parser, "readVictims"));

        var victims = Field<Dictionary<string, List<Victim>>>(parser, "victims");
        Assert.Equal(new[] { "first", "second" }, victims["client"].Select(v => v.Email));
        Assert.Equal(new[] { "111", "222" }, victims["client"].Select(v => v.CellPhone));
        Assert.All(victims["client"], v => Assert.Equal("victim", v.OID));
        Assert.Single(victims["other"]);
        if (kind != 2)
        {
            Assert.Equal("VAPP", Field<Dictionary<string, string>>(parser, "victimTypeDict")["victim"]);
            Assert.Equal(new[] { "VAPP", "OTHER" }, victims["client"].Select(v => v.VictimType));
        }
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HolidayRowsGroupInOrderIncludingFinalGroupAndRoleRowsBuildDictionary(int kind)
    {
        var spy = RepositorySpy.Create();
        spy.Rows = call => call.Name == "PrepareReadAllHolidays"
            ? Table(["POGroup", "StartDate", "EndDate", "HolidayName"],
                ["group1", Expiry, Expiry.AddDays(1), "first"],
                ["group1", Expiry, Expiry.AddDays(2), "second"],
                ["group2", Expiry, Expiry.AddDays(3), "last"])
            : Table(["SystemID", "RoleName"], [43, "officer"], [44, "supervisor"]);
        object parser = Parser(kind, spy);
        Assert.Equal(true, Call(parser, "readAllHolidays"));
        Assert.Equal(true, Call(parser, "readAllRoles"));

        var holidays = Field<Dictionary<string, List<Holiday>>>(parser, "activeHolidays");
        Assert.Equal(2, holidays.Count);
        Assert.Equal(new[] { "first", "second" }, holidays["group1"].Select(h => h.HolidayName));
        Assert.Equal("last", Assert.Single(holidays["group2"]).HolidayName);
        Assert.Equal(Expiry, holidays["group1"][0].StartDate);
        Assert.Equal(Expiry.AddDays(3), holidays["group2"][0].EndDate);
        var roles = Field<Dictionary<string, string>>(parser, "roles");
        Assert.Equal(2, roles.Count);
        Assert.Equal("officer", roles["43"]);
        Assert.Equal("supervisor", roles["44"]);
        AssertLifetime(spy);
    }

    [Fact]
    public void ExpiredAlarmRowsMapStateAndRetainOnlyAlarmsWithMatchingProfiles()
    {
        var spy = RepositorySpy.Create();
        string[] columns = ["HistoryID", "AgencyID", "ClientID", "OID", "OTZ", "AlarmSystemID", "AlarmID",
            "DeviceID", "StateID", "ReceivedDateTime", "EventDateTime", "EventDescription", "SystemID",
            "POGroupNum", "POGroup1", "POGroup2", "POGroup3", "ExpiryTime", "ParserStateNo", "CurrentStateNo", "CurrentLoopNo"];
        object[] Row(int systemID, string client, int current) => [1101, 31, 41, client, "UTC", 901, "event",
            "device", 5, 1_700_000_060L, 1_700_000_000L, "alarm", systemID, "group", "", "", "",
            "2026-10-01 12:34:56", 2, current, 3];
        spy.Rows = _ => Table(columns, Row(101, "client", 1), Row(102, "client", 4), Row(103, "missing", 1));
        object parser = Parser(2, spy);
        Field<Dictionary<string, int>>(parser, "clientProfileMapping")["client"] = 10;
        var item = new ProfileItem
        {
            StateNo = 2, Action = 11, StartTime = DateTime.MinValue, EndTime = DateTime.MinValue.AddDays(1).AddTicks(-1),
            StateTime = 6, NextState = 8, LoopStartState = -1, NumberOfLoops = 100, Instruction = "selected",
            Email = "selected-email", RoleAction = 2, RoleID = 43, FeedBackRequired = true
        };
        Field<Dictionary<int, Profile>>(parser, "activeProfiles")[10] = new Profile
        {
            ProfileID = 10, ProfileName = "selected-profile",
            Events = new() { ["event"] = Enumerable.Range(1, 7).ToDictionary(day => day, _ => new List<ProfileItem> { item }) }
        };
        DateTime before = DateTime.UtcNow;

        Assert.Equal(true, Call(parser, "getExpiredAlarms"));

        var call = Assert.Single(spy.Calls);
        Assert.Equal("PrepareGetExpiredAlarms", call.Name);
        Assert.InRange(Assert.IsType<DateTime>(Assert.Single(call.Arguments)), before, DateTime.UtcNow);
        var alarms = Field<List<ActiveAlarm>>(parser, "activeAlarms");
        Assert.Equal(new[] { 101, 102 }, alarms.Select(a => a.SystemID));
        Assert.Equal(new[] { 2, 5 }, alarms.Select(a => a.CurrentStateNo));
        Assert.All(alarms, a =>
        {
            Assert.Equal(1101, a.HistoryID);
            Assert.Equal(31, a.AgencyID);
            Assert.Equal(41, a.ClientSystemID);
            Assert.Equal("client", a.ClientID);
            Assert.Equal("UTC", a.ClientTZ);
            Assert.Equal(901, a.AlarmSystemID);
            Assert.Equal("event", a.AlarmID);
            Assert.Equal("device", a.DeviceID);
            Assert.Equal(5, a.StateID);
            Assert.Equal(1_700_000_060L, a.ReceivedDateTime);
            Assert.Equal(1_700_000_000L, a.EventDateTime);
            Assert.Equal("alarm", a.AlarmText);
            Assert.Equal("group", a.POGroupNum);
            Assert.Equal("", a.POGroup1);
            Assert.Equal("", a.POGroup2);
            Assert.Equal("", a.POGroup3);
            Assert.Equal("2026-10-01 12:34:56", a.ExpiryTime);
            Assert.Equal(2, a.StateNo);
            Assert.Equal(3, a.CurrentLoopNumber);
            Assert.Equal(11, a.Priority);
            Assert.Equal("selected-email", a.EmailAddresses);
            Assert.Equal("selected", a.Instruction);
            Assert.Equal("selected-profile", a.ProfileName);
            Assert.Equal(10, a.ProfileID);
            Assert.Equal(6, a.StateTime);
            Assert.Equal(8, a.NextStateNo);
            Assert.True(a.FeedbackREQ);
        });
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void VictimZoneSetsDeduplicateButClearingEventListsKeepDuplicates(int kind)
    {
        var spy = RepositorySpy.Create();
        spy.Rows = call => call.Name switch
        {
            "PrepareReadMEZVictims" => Table(["Offender", "Victim"], ["client", "v"], ["client", "v"], ["client", "w"]),
            "PrepareReadAttachedVictimZones" => Table(["OffenderID", "ZoneID", "ZoneCategory", "VictimID"],
                ["client", "zone", "category", "v"], ["client", "zone", "category", "v"],
                ["client", "zone", "other", "w"], ["other", "zone", "category", "v"]),
            "PrepareReadProfileItemsClear" => Table(["ProfileID", "ClearingEvent", "EventCode"],
                [10, "clear", "event"], [10, "clear", "event"], [20, "clear2", "event2"]),
            _ => throw new InvalidOperationException("Unexpected reference read")
        };
        object parser = Parser(kind, spy);
        Assert.Equal(true, Call(parser, "readMEZVictims"));
        Assert.Equal(true, Call(parser, "readAttachedVictimZones"));
        Assert.Equal(true, Call(parser, "readProfileItemsClear"));

        Assert.Equal(new[] { "v", "w" }, Field<Dictionary<string, HashSet<string>>>(parser, "MEZVictims")["client"].Order());
        var zones = Field<Dictionary<string, Dictionary<string, HashSet<string>>>>(parser, "AttachedVictimZones");
        Assert.Equal("v", Assert.Single(zones["client"]["zone|category"]));
        Assert.Equal("w", Assert.Single(zones["client"]["zone|other"]));
        Assert.Equal("v", Assert.Single(zones["other"]["zone|category"]));
        var clearing = Field<Dictionary<int, List<ProfileItemClear>>>(parser, "ClearEvents");
        Assert.Equal(2, clearing[10].Count);
        Assert.All(clearing[10], c => { Assert.Equal("clear", c.ClearingEvent); Assert.Equal("event", c.EventCode); });
        Assert.Single(clearing[20]);
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SetupPreparesReferencesInOrderAndStopsOnPreparationFailure(int kind)
    {
        var spy = RepositorySpy.Create();
        object parser = Parser(kind, spy);
        string[] expected = ["PrepareFetchClientProfile", "PrepareFetchClientProfile", "PrepareReadAllProfiles",
            "PrepareReadAllHolidays", "PrepareReadAllRoles", "PrepareReadVictims"];
        if (kind != 2) expected = [.. expected, "PrepareReadMEZVictims", "PrepareReadAttachedVictimZones", "PrepareReadProfileItemsClear"];

        Assert.Equal(true, Call(parser, "setUpParser", "Offline"));
        Assert.Equal(expected, spy.Calls.Select(c => c.Name));
        AssertArguments([0], spy.Calls[0].Arguments);
        AssertArguments([1], spy.Calls[1].Arguments);
        AssertLifetime(spy);

        var failingSpy = RepositorySpy.Create();
        failingSpy.PreparationFailure = call => call.Name == "PrepareReadAllProfiles" ? new PreparationException() : null;
        Assert.Equal(false, Call(Parser(kind, failingSpy), "setUpParser", "Offline"));
        Assert.Equal(expected.Take(3), failingSpy.Calls.Select(c => c.Name));
        AssertLifetime(failingSpy);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void CheckpointReadersUseLastRowOrZeroAndDisposeReader(int kind, bool empty)
    {
        var spy = RepositorySpy.Create();
        spy.Rows = call => empty ? new DataTable() : call.Name == "PrepareReadLastSuccessfulProcess"
            ? Table(["StatusID"], [12], [34]) : Table(["SystemID"], [56], [78]);
        object target = kind == 3 ? Service(spy) : Parser(kind, spy);

        Assert.Equal(empty ? 0 : 34, Call(target, "readLastSuccessfulProcess", 47));
        Assert.Equal(empty ? 0 : 78, Call(target, "ReadCurrentActiveAlarmPoint"));

        Assert.Equal("PrepareReadLastSuccessfulProcess", spy.Calls[0].Name);
        AssertArguments([47, kind == 3], spy.Calls[0].Arguments);
        Assert.Equal("PrepareReadCurrentActiveAlarmPoint", spy.Calls[1].Name);
        AssertArguments([kind == 3], spy.Calls[1].Arguments);
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData("readLastSuccessfulProcess")]
    [InlineData("ReadCurrentActiveAlarmPoint")]
    public void ServiceSwallowsCheckpointPreparationFailureWhereNormalParsersRethrow(string method)
    {
        for (int kind = 0; kind < 4; kind++)
        {
            if (kind == 2) continue;
            var spy = RepositorySpy.Create();
            var failure = new PreparationException();
            spy.PreparationFailure = _ => failure;
            object target = kind == 3 ? Service(spy) : Parser(kind, spy);
            object?[] args = method == "readLastSuccessfulProcess" ? [47] : [];
            if (kind == 3) Assert.Equal(0, Call(target, method, args));
            else Assert.Same(failure, Assert.Throws<TargetInvocationException>(() => Call(target, method, args)).InnerException);
            var call = Assert.Single(spy.Calls);
            Assert.Equal("Prepare" + char.ToUpperInvariant(method[0]) + method[1..], call.Name);
            AssertArguments([.. args, kind == 3], call.Arguments);
            Assert.Null(call.Operation);
            Assert.Single(spy.Events);
        }
    }

    public static IEnumerable<object[]> ParseCases()
    {
        for (int kind = 0; kind < 3; kind++)
        foreach (int priority in new[] { 1, 3, 5, 7, 11, 15, 16 })
            yield return [kind, priority];
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void ParseAlarmsOrdersActionsArchiveStateThenNormalCheckpoint(int kind, int priority)
    {
        var spy = RepositorySpy.Create();
        spy.Rows = call => call.Name switch
        {
            "PrepareGetInsertEmails" => Table(["POMSGAddress"], ["page"]),
            "PrepareGetClientEmail" => Table(["EmailAddress"], ["client-email"]),
            "PrepareGetClientText" => Table(["Cell"], ["client-text"]),
            _ => new DataTable()
        };
        object parser = Parser(kind, spy);
        var alarm = Alarm(priority);
        SetField(parser, "activeAlarms", new List<ActiveAlarm> { alarm });
        DateTime before = DateTime.UtcNow;

        Call(parser, "parseAlarms");

        var expected = new List<string>();
        if (kind != 2) expected.Add("PrepareCreateAlarmAudit");
        switch (priority)
        {
            case 3: expected.Add("PreparePushAlertToMcApp"); break;
            case 5: expected.AddRange(["PrepareAddToNotificationQueue", "PrepareGetInsertEmails", "PrepareCreateAlarmAudit", "PrepareAddActiveAlarmActionToActivity"]); break;
            case 7: expected.AddRange(["PreparePushAlertToMcApp", "PrepareAddToNotificationQueue", "PrepareCreateAlarmAudit", "PrepareAddActiveAlarmActionToActivity"]); break;
            case 15: case 16: expected.AddRange([priority == 15 ? "PrepareGetClientEmail" : "PrepareGetClientText", "PrepareInsertNotificationQueueVictim", "PrepareCreateAlarmAudit", "PrepareAddActiveAlarmActionToActivity"]); break;
        }
        expected.AddRange(["PrepareCreateAlarmAudit", "PrepareInsertIntoAlarmNotification"]);
        if (priority != 1) expected.Add("PrepareInsertIntoCurrentAlarmNotification");
        if (kind != 2) expected.Add("PrepareUpdateParserActivty");
        Assert.Equal(expected, spy.Calls.Select(c => c.Name));
        var archive = Assert.Single(spy.Calls, c => c.Name == "PrepareInsertIntoAlarmNotification");
        Assert.Equal(alarm.SystemID, archive.Arguments[0]);
        Assert.Equal(kind == 2 ? alarm.CurrentStateNo : 1, archive.Arguments[1]);
        DateTime applied = Assert.IsType<DateTime>(archive.Arguments[2]);
        if (kind == 2) Assert.InRange(applied, before.AddMinutes(alarm.StateTime), DateTime.UtcNow.AddMinutes(alarm.StateTime));
        else Assert.Equal(DateTime.Parse(alarm.EventRecievedDateTime()).AddMinutes(alarm.StateTime), applied);
        Assert.Equal(PriorityName(priority), archive.Arguments[3]);
        if (priority != 1)
        {
            var state = Assert.Single(spy.Calls, c => c.Name == "PrepareInsertIntoCurrentAlarmNotification");
            Assert.Equal(alarm.SystemID, state.Arguments[0]);
            Assert.Equal(alarm.NextStateNo, state.Arguments[1]);
            Assert.Equal(kind == 2 ? alarm.CurrentStateNo : alarm.StateNo + 1, state.Arguments[3]);
            Assert.Equal(kind == 2 ? alarm.CurrentLoopNumber : 0, state.Arguments[4]);
            Assert.Equal(priority is 3 or 7 ? 0 : 1, state.Arguments[5]);
        }
        if (priority is 5 or 7)
            AssertArguments([alarm, priority == 5 ? 1 : 3], Assert.Single(spy.Calls, c => c.Name == "PrepareAddToNotificationQueue").Arguments);
        if (priority is 15 or 16)
            AssertArguments([alarm, priority == 15 ? 3 : 4, priority == 15 ? "client-email" : "client-text", false],
                Assert.Single(spy.Calls, c => c.Name == "PrepareInsertNotificationQueueVictim").Arguments);
        if (kind != 2) AssertArguments([alarm.SystemID, "47"], spy.Calls[^1].Arguments);
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MissingExplicitNextStateUsesParserSpecificFallbackParameters(int kind)
    {
        var spy = RepositorySpy.Create();
        object parser = Parser(kind, spy);
        var alarm = Alarm();
        alarm.NextStateNo = -1;
        SetField(parser, "activeAlarms", new List<ActiveAlarm> { alarm });

        Call(parser, "parseAlarms");

        var call = Assert.Single(spy.Calls, c => c.Name == "PrepareInsertIntoCurrentAlarmNotification");
        Assert.Equal(alarm.SystemID, call.Arguments[0]);
        Assert.Equal(kind == 2 ? alarm.CurrentStateNo + 1 : alarm.StateNo + 1, call.Arguments[1]);
        Assert.Equal(kind == 2 ? alarm.CurrentStateNo : alarm.StateNo + 1, call.Arguments[3]);
        Assert.Equal(0, call.Arguments[4]);
        Assert.Equal(1, call.Arguments[5]);
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SwallowedAuditPreparationFailureStillArchivesAdvancesAndCheckpoints(int kind)
    {
        var spy = RepositorySpy.Create();
        spy.PreparationFailure = call => call.Name == "PrepareCreateAlarmAudit" ? new PreparationException() : null;
        object parser = Parser(kind, spy);
        SetField(parser, "activeAlarms", new List<ActiveAlarm> { Alarm() });

        Call(parser, "parseAlarms");

        Assert.Equal(101, Assert.Single(spy.Archived));
        Assert.Equal(101, Assert.Single(spy.CurrentStates));
        Assert.Equal(kind == 2 ? 17 : 101, spy.Checkpoint);
        Assert.All(spy.Calls.Where(c => c.Name == "PrepareCreateAlarmAudit"), c => Assert.Null(c.Operation));
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CheckpointPreparationFailureRetainsPreviousCheckpointAndCompletedArchiveState(int kind)
    {
        var spy = RepositorySpy.Create();
        spy.PreparationFailure = call => call.Name == "PrepareUpdateParserActivty" ? new PreparationException() : null;
        object parser = Parser(kind, spy);
        SetField(parser, "activeAlarms", new List<ActiveAlarm> { Alarm(), Alarm(systemID: 102) });

        Call(parser, "parseAlarms");

        Assert.Equal(new[] { 101, 102 }, spy.Archived.Order());
        Assert.Equal(new[] { 101, 102 }, spy.CurrentStates.Order());
        Assert.Equal(17, spy.Checkpoint);
        Assert.Equal("PrepareUpdateParserActivty", spy.Calls[^1].Name);
        AssertArguments([102, "47"], spy.Calls[^1].Arguments);
        Assert.Null(spy.Calls[^1].Operation);
        AssertLifetime(spy);
    }

    public static IEnumerable<object[]> ParseFailureCases()
    {
        for (int kind = 0; kind < 3; kind++)
        foreach (int failedIndex in new[] { 0, 1 })
        foreach (string prepare in new[] { "PreparePushAlertToMcApp", "PrepareInsertIntoAlarmNotification", "PrepareInsertIntoCurrentAlarmNotification" })
            yield return [kind, failedIndex, prepare];
    }

    [Theory]
    [MemberData(nameof(ParseFailureCases))]
    public void PreparationFailureBreaksNormalBatchButStepsContinueAndRetainSuccessfulWrites(
        int kind, int failedIndex, string prepare)
    {
        var spy = RepositorySpy.Create();
        object parser = Parser(kind, spy);
        var alarms = Enumerable.Range(0, 3).Select(i => Alarm(prepare == "PreparePushAlertToMcApp" ? 3 : 11, 101 + i)).ToList();
        int failedID = alarms[failedIndex].SystemID;
        spy.PreparationFailure = call => call.Name == prepare && AlarmID(call) == failedID ? new PreparationException() : null;
        SetField(parser, "activeAlarms", alarms);

        Call(parser, "parseAlarms");

        int[] successful = kind == 2 ? alarms.Where(a => a.SystemID != failedID).Select(a => a.SystemID).ToArray()
            : alarms.Take(failedIndex).Select(a => a.SystemID).ToArray();
        Assert.Equal(successful, spy.CurrentStates.Order());
        int[] archived = prepare == "PrepareInsertIntoCurrentAlarmNotification"
            ? successful.Append(failedID).Order().ToArray() : successful;
        Assert.Equal(archived, spy.Archived.Order());
        var failed = Assert.Single(spy.Calls, c => c.Name == prepare && AlarmID(c) == failedID);
        Assert.Null(failed.Operation);
        if (kind == 2)
        {
            Assert.DoesNotContain(spy.Calls, c => c.Name == "PrepareUpdateParserActivty");
            Assert.Equal(17, spy.Checkpoint);
        }
        else if (failedIndex == 0)
        {
            Assert.Equal(17, spy.Checkpoint);
            Assert.DoesNotContain(spy.Calls, c => c.Name == "PrepareUpdateParserActivty");
        }
        else
        {
            Assert.Equal(alarms[0].SystemID, spy.Checkpoint);
            Assert.Equal("PrepareUpdateParserActivty", spy.Calls[^1].Name);
            AssertArguments([alarms[0].SystemID, "47"], spy.Calls[^1].Arguments);
        }
        if (kind != 2)
            Assert.DoesNotContain(spy.Calls, c => AlarmID(c) == alarms[failedIndex + 1].SystemID);
        AssertLifetime(spy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PushQueueExecutionFailureIsSwallowedAndOwnedOperationStillDisposedWithoutRetry(int kind)
    {
        var spy = RepositorySpy.Create();
        spy.ExecutionFailure = new InvalidOperationException("offline execution failure");
        Call(Parser(kind, spy), "insertPushNotificationQueue", Alarm(), "victim", "offender");
        AssertLifetime(spy);
    }

    private static (string Method, string Prepare, object?[] Arguments, object?[] Expected) Adapter(string helper, ActiveAlarm a)
    {
        string[] parts = helper.Split(':');
        string method = parts[0];
        int variant = parts.Length == 2 && parts[1] != "null" ? int.Parse(parts[1]) : 0;
        object?[] args = method switch
        {
            "FetchClientProfile" => [variant],
            "readRoles" => [a, 43, variant],
            "getInsertEmails" or "getClientEmail" or "getClientText" or "PushAlertToMcApp" => [a],
            "AddToNotificationQueue" => [a, variant],
            "insertPushNotificationQueue" => [a, "victim", "offender"],
            "insertNotificationQueueVictim" => [a, 4, "first;second;", variant == 1],
            "CreateAlarmAudit" => [14, "action", 29, 4],
            "AddActiveAlarmActionToActivity" => [29, "email", 3],
            "ClearMcAppAlarm" => ["event1;event2", "offender"],
            "readLastSuccessfulProcess" => [47],
            "updateParserActivty" => [parts.Length == 2 ? null : 101],
            "insertIntoAlarmNotification" => [101, 4, Expiry, "archive-action"],
            "insertIntoCurrentAlarmNotification" => [101, 4, Expiry, 6, 8, 0],
            _ => []
        };
        string prepare = "Prepare" + char.ToUpperInvariant(method[0]) + method[1..];
        object?[] expected = prepare switch
        {
            "PrepareReadLastSuccessfulProcess" or "PrepareReadCurrentActiveAlarmPoint" => [.. args, false],
            "PrepareUpdateParserActivty" => [args[0], "47"],
            _ => args
        };
        return (method, prepare, args, expected);
    }

    private static bool Rethrows(int kind, string method) => method switch
    {
        "FetchClientProfile" or "readAllProfiles" or "readAllHolidays" or "readAllRoles" or
        "readMEZVictims" or "readAttachedVictimZones" or "readProfileItemsClear" or
        "insertPushNotificationQueue" or "ClearMcAppAlarm" or "CreateAlarmAudit" or
        "AddActiveAlarmActionToActivity" or "getExpiredAlarms" => false,
        "getInsertEmails" or "readVictims" => kind == 2,
        _ => true
    };

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["ConnectionStrings:connstr"] = "Server=offline.invalid;Database=Offline;Integrated Security=true;TrustServerCertificate=true",
            ["ParserID"] = "47", ["OfflineParserID"] = "47", ["NumberOfProcessPoints"] = "10", ["DefaultMcAppFlag"] = "0"
        }).Build();

    // Prefer real constructors: empty instance dictionaries suppress the victim prelude,
    // so the null notification sender is never accessed and no global settings are touched.
    private static object Parser(int kind, RepositorySpy repository) => kind switch
    {
        0 => new WorkFlowCommon(NullLogger<WorkFlowCommon>.Instance, Configuration(), null!, (IRepository)repository),
        1 => new WorkFlowInitiator(NullLogger<WorkFlowInitiator>.Instance, Configuration(), null!, (IRepository)repository),
        2 => new WorkFlowSteps(NullLogger<WorkFlowSteps>.Instance, Configuration(), (IRepository)repository),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Type ParserType(int kind) => kind switch
    {
        0 => typeof(WorkFlowCommon), 1 => typeof(WorkFlowInitiator), 2 => typeof(WorkFlowSteps),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static object Logger(int kind) => kind switch
    {
        0 => NullLogger<WorkFlowCommon>.Instance, 1 => NullLogger<WorkFlowInitiator>.Instance,
        2 => NullLogger<WorkFlowSteps>.Instance, _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static WorkFlowInitiatorService Service(RepositorySpy repository) => new(
        NullLogger<WorkFlowInitiatorService>.Instance, Configuration(), NullLoggerFactory.Instance,
        () => throw new InvalidOperationException("Checkpoint readers must not construct parsers"), (IRepository)repository);

    private static ActiveAlarm Alarm(int priority = 11, int systemID = 101) => new()
    {
        SystemID = systemID, AlarmSystemID = 901, HistoryID = systemID + 1000, AgencyID = 31,
        ClientSystemID = 41, ClientID = "client", ClientTZ = "UTC", AlarmID = "event", DeviceID = "device",
        POGroupNum = "group", POGroup1 = "", POGroup2 = "", POGroup3 = "", Priority = priority,
        EmailAddresses = "officer-email", EmailJoin = 0, AlarmText = "alarm", Instruction = "instruction",
        ProfileName = "profile", ProfileID = 10, StateNo = 2, CurrentStateNo = 4, NextStateNo = 8,
        StateTime = 5, CurrentLoopNumber = 3, ProcessNextStep = 1,
        EventDateTime = 1_700_000_000, ReceivedDateTime = 1_700_000_060,
        EventDateTimeLocal = "2023-11-14 22:13:20", EventDateTimeUTC = "2023-11-14 22:13:20",
        ExpiryTime = "2026-10-01 12:34:56", ZoneID = "zone", ZoneCategory = "category", ZoneName = "name",
        ZoneAddress = "address", PolyZoneName = "poly", MEZEventVictimID = "", OffenderName = "offender"
    };

    private static string PriorityName(int priority) => priority switch
    {
        1 => "Do Nothing", 3 => "McApp", 5 => "Auto Page", 7 => "McApp and Auto Email",
        11 => "Delay", 15 => "Alert Client - Email", 16 => "Alert Client - Text",
        _ => throw new ArgumentOutOfRangeException(nameof(priority))
    };

    private static MethodInfo Method(object target, string name)
    {
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
            if (type.GetMethod(name, Instance | BindingFlags.DeclaredOnly) is { } method)
                return method;
        throw new MissingMethodException(target.GetType().FullName, name);
    }
    private static FieldInfo FieldInfo(object target, string name)
    {
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, Instance | BindingFlags.DeclaredOnly) is { } field)
                return field;
        throw new MissingFieldException(target.GetType().FullName, name);
    }
    private static object? Call(object target, string name, params object?[] args) => Method(target, name).Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)FieldInfo(target, name).GetValue(target)!;
    private static void SetField(object target, string name, object value) => FieldInfo(target, name).SetValue(target, value);

    private static void AssertArguments(object?[] expected, object?[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] is ActiveAlarm) Assert.Same(expected[i], actual[i]);
            else Assert.Equal(expected[i], actual[i]);
    }

    private static int? AlarmID(RecordedCall call) => call.Name switch
    {
        "PreparePushAlertToMcApp" => ((ActiveAlarm)call.Arguments[0]!).SystemID,
        "PrepareInsertIntoAlarmNotification" or "PrepareInsertIntoCurrentAlarmNotification" => (int)call.Arguments[0]!,
        _ => null
    };

    private static void AssertLifetime(RepositorySpy spy)
    {
        var expected = new List<string>();
        foreach (var call in spy.Calls)
        {
            expected.Add(call.Name + ":Prepare");
            if (call.Operation is not { } operation) continue;
            Assert.Equal(new[] { "Open", operation.Reader is null ? "ExecuteNonQuery" : "ExecuteReader", "Dispose" }, operation.Events);
            Assert.True(operation.Disposed);
            if (operation.Reader is not null) Assert.True(operation.Reader.IsClosed);
            expected.AddRange(operation.Events.Select(e => call.Name + ":" + e));
        }
        Assert.Equal(expected, spy.Events); // No interleaved lifetimes or use after disposal.
    }

    private static DataTable Table(string[] columns, params object[][] rows)
    {
        var table = new DataTable();
        for (int i = 0; i < columns.Length; i++)
            table.Columns.Add(columns[i], rows.Length == 0 ? typeof(object) : rows[0][i].GetType());
        foreach (object[] row in rows) table.Rows.Add(row);
        return table;
    }

    private static DataTable ProfileRows()
    {
        string[] columns = ["ProfileID", "ProfileName", "Day", "EventCode", "StartTime", "EndTime", "Action",
            "HoldDuration", "GracePeriod", "Instruction", "EmailAddress", "EmailJoin", "StateNo", "StateTime",
            "FeedbackRequired", "ProfileType", "TimeIntervalsID", "NextState", "LoopStartState", "NumberOfLoops", "RoleID", "RoleAction"];
        object[] Row(int id, string name, string evt, int day) =>
            [id, name, day, evt, "08:00:00", "18:00:00", 3, 4, 5, "instruction", "email", 1, 2, 6, true, 7, 8, 9, 10, 11, 12, 2];
        return Table(columns, Row(10, "first-name", "event", 1), Row(10, "ignored-name", "event", 1),
            Row(10, "ignored-name", "event", 2), Row(10, "ignored-name", "other-event", 1), Row(20, "other-name", "event", 1));
    }

    private sealed class PreparationException : Exception;

    public sealed class RecordedCall(string name, object?[] arguments)
    {
        public string Name { get; } = name;
        public object?[] Arguments { get; } = arguments;
        public RecordingOperation? Operation { get; set; }
    }

    // DispatchProxy intercepts the entire 27-method interface, without a hand-written
    // repository implementation that could accidentally open SQL or omit a new signature.
    public class RepositorySpy : DispatchProxy
    {
        public List<RecordedCall> Calls { get; } = [];
        public List<string> Events { get; } = [];
        public Func<RecordedCall, DataTable> Rows { get; set; } = _ => new DataTable();
        public Func<RecordedCall, Exception?> PreparationFailure { get; set; } = _ => null;
        public Exception? ExecutionFailure { get; set; }
        public HashSet<int> Archived { get; } = [];
        public HashSet<int> CurrentStates { get; } = [];
        public int Checkpoint { get; private set; } = 17;

        public static RepositorySpy Create() => (RepositorySpy)DispatchProxy.Create<IRepository, RepositorySpy>();

        protected override object Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(targetMethod);
            Assert.Equal(typeof(IWorkflowOperation), targetMethod.ReturnType);
            var call = new RecordedCall(targetMethod.Name, args?.ToArray() ?? []);
            Assert.Equal(targetMethod.GetParameters().Length, call.Arguments.Length);
            Calls.Add(call);
            Events.Add(call.Name + ":Prepare");
            if (PreparationFailure(call) is { } failure) throw failure;
            call.Operation = new RecordingOperation(this, call, Rows(call));
            return call.Operation;
        }

        internal void Executed(RecordedCall call)
        {
            switch (call.Name)
            {
                case "PrepareInsertIntoAlarmNotification": Archived.Add((int)call.Arguments[0]!); break;
                case "PrepareInsertIntoCurrentAlarmNotification": CurrentStates.Add((int)call.Arguments[0]!); break;
                case "PrepareUpdateParserActivty": Checkpoint = (int?)call.Arguments[0] ?? Checkpoint; break;
            }
        }
    }

    public sealed class RecordingOperation(RepositorySpy spy, RecordedCall call, DataTable rows) : IWorkflowOperation
    {
        public List<string> Events { get; } = [];
        public DataTableReader? Reader { get; private set; }
        public bool Disposed { get; private set; }
        private bool opened;

        public void Open()
        {
            Assert.False(Disposed);
            Assert.False(opened); // A second Open would expose an unexpected retry immediately.
            opened = true;
            Record("Open");
        }

        public IDataReader ExecuteReader()
        {
            Assert.True(opened);
            Assert.False(Disposed);
            Record("ExecuteReader");
            Reader = rows.CreateDataReader();
            return Reader;
        }

        public int ExecuteNonQuery()
        {
            Assert.True(opened);
            Assert.False(Disposed);
            Record("ExecuteNonQuery");
            if (spy.ExecutionFailure is { } failure) throw failure;
            spy.Executed(call);
            return -7; // Helpers report completion, not affected-row counts.
        }

        public void Close() { Assert.False(Disposed); Record("Close"); }

        public void Dispose()
        {
            Assert.False(Disposed);
            Record("Dispose");
            Reader?.Dispose();
            rows.Dispose();
            Disposed = true;
        }

        private void Record(string name) { Events.Add(name); spy.Events.Add(call.Name + ":" + name); }
    }
}