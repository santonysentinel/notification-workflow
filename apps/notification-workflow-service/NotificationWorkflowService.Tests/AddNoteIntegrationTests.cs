using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using ActiveAlarmsParser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Parser;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.Notes;
using Xunit;
using NotificationSender = ActiveAlarmsParser.Service.NotificationService.NotificationService;
using RepositorySpy = NotificationWorkflowService.Tests.WorkflowRepositoryAdapterTests.RepositorySpy;

namespace NotificationWorkflowService.Tests;

/// <summary>Real parser shells/selection and DI, with offline note and workflow dependencies.</summary>
public class AddNoteIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string Template = "Location {{lat}}/{{lon}} — {{current_location_address}}";

    public static IEnumerable<object[]> Parsers()
    { for (int kind = 0; kind < 3; kind++) yield return [kind]; }

    public static IEnumerable<object[]> StateCases()
    {
        foreach (object[] row in Parsers())
        foreach (int next in new[] { 8, -1 }) yield return [row[0], next];
    }

    [Theory]
    [MemberData(nameof(StateCases))]
    public async Task NoteReceivesOriginalTemplateAndOidBeforeFinalAuditArchiveStateAndCheckpoint(int kind, int next)
    {
        var spy = RepositorySpy.Create();
        var events = new List<string>();
        spy.PreparationFailure = call =>
        {
            events.Add(call.Name == "PrepareCreateAlarmAudit" ? "audit" + call.Arguments[0] : call.Name);
            return null;
        };
        var note = new RecordingNote { OnCall = (_, _, _) => { events.Add("note"); return Task.CompletedTask; } };
        var parser = Parser(kind, spy, note);
        var alarm = Alarm(); alarm.NextStateNo = next;
        Set(parser, "activeAlarms", new List<ActiveAlarm> { alarm });
        using var source = new CancellationTokenSource();

        await Parse(parser, source.Token);

        var received = Assert.Single(note.Calls);
        Assert.Equal((Template, "client", source.Token), received);
        string[] expected = ["note", "audit14", "PrepareInsertIntoAlarmNotification", "PrepareInsertIntoCurrentAlarmNotification"];
        if (kind != 2) expected = ["audit15", .. expected, "PrepareUpdateParserActivty"];
        Assert.Equal(expected, events);
        Assert.Equal(73, alarm.ProcessNextStep);
        Assert.Equal(2, alarm.StateNo); Assert.Equal(4, alarm.CurrentStateNo);
        Assert.Equal(Template, alarm.Instruction);
        var audit = Assert.Single(spy.Calls, c => c.Name == "PrepareCreateAlarmAudit" && Equals(c.Arguments[0], 14));
        Assert.Equal(new object?[] { 14, "Action as per the profile assigned:Add Note", alarm.HistoryID, kind == 2 ? 4 : 1 }, audit.Arguments);
        var archive = Assert.Single(spy.Calls, c => c.Name == "PrepareInsertIntoAlarmNotification");
        Assert.Equal(alarm.SystemID, archive.Arguments[0]);
        Assert.Equal(kind == 2 ? 4 : 1, archive.Arguments[1]);
        Assert.Equal("Add Note", archive.Arguments[3]);
        var state = Assert.Single(spy.Calls, c => c.Name == "PrepareInsertIntoCurrentAlarmNotification");
        Assert.Equal(alarm.SystemID, state.Arguments[0]);
        Assert.Equal(next == -1 ? kind == 2 ? 5 : 3 : next, state.Arguments[1]);
        Assert.Equal(kind == 2 ? 4 : 3, state.Arguments[3]);
        Assert.Equal(kind == 2 && next != -1 ? 3 : 0, state.Arguments[4]);
        Assert.Equal(next == -1 ? 1 : 73, state.Arguments[5]);
        Assert.Equal(kind == 2 ? 17 : 101, spy.Checkpoint);
        Assert.All(spy.Calls, call => Assert.True(call.Operation!.Disposed));
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public async Task OriginalHelperForwardsToInjectedNoteServiceAndRetainsFields(int kind)
    {
        var spy = RepositorySpy.Create(); var note = new RecordingNote();
        var parser = Parser(kind, spy, note);
        Assert.Same(note, Field(parser, "noteService").GetValue(parser));
        Assert.Same(spy, Field(parser, "repository").GetValue(parser));
        if (kind != 2) Assert.IsType<NotificationSender>(Field(parser, "notificationService").GetValue(parser));
        // These adapters are the actual private nested operations used by each parse shell.
        Type owner = kind == 2 ? typeof(WorkFlowSteps) : typeof(WorkFlowCommon);
        Type adapterType = owner.GetNestedType("ActionOperations", BindingFlags.NonPublic)!;
        var operations = (NotificationWorkflowService.Parser.Actions.IWorkflowActionOperations)
            Activator.CreateInstance(adapterType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [parser], null)!;
        using var source = new CancellationTokenSource();
        await operations.AddNoteAsync(Template, "original-oid", source.Token);
        Assert.Equal((Template, "original-oid", source.Token), Assert.Single(note.Calls));
        Assert.Empty(spy.Calls);
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (object[] row in Parsers())
        foreach (int index in new[] { 0, 1 }) yield return [row[0], index];
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task NoteFailureStopsNormalBatchWithPreviousSuccessCheckpointButStepsContinue(int kind, int index)
    {
        var spy = RepositorySpy.Create(); var note = new RecordingNote();
        var parser = Parser(kind, spy, note);
        var alarms = Enumerable.Range(0, 3).Select(i => Alarm(101 + i)).ToList();
        foreach (var alarm in alarms) alarm.Instruction = Template + alarm.SystemID;
        note.OnCall = (template, _, _) => template == alarms[index].Instruction
            ? Task.FromException(new InvalidOperationException("offline note failure")) : Task.CompletedTask;
        Set(parser, "activeAlarms", alarms);

        await Parse(parser);

        int[] successful = kind == 2 ? alarms.Where((_, i) => i != index).Select(a => a.SystemID).ToArray()
            : alarms.Take(index).Select(a => a.SystemID).ToArray();
        Assert.Equal(successful, spy.Archived.Order());
        Assert.Equal(successful, spy.CurrentStates.Order());
        Assert.Equal(kind == 2 || index == 0 ? 17 : 101, spy.Checkpoint);
        Assert.Equal(kind == 2 ? 3 : index + 1, note.Calls.Count);
        Assert.DoesNotContain(spy.Calls, c => c.Name == "PrepareCreateAlarmAudit" && Equals(c.Arguments[0], 14)
            && Equals(c.Arguments[2], alarms[index].HistoryID));
        Assert.All(alarms, a => Assert.Equal(73, a.ProcessNextStep));
        Assert.Equal(kind != 2 && index == 1 ? 1 : 0, spy.Calls.Count(c => c.Name == "PrepareUpdateParserActivty"));
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public async Task ParserAwaitsNoteBeforeAnyFinalAuditOrStateWork(int kind)
    {
        var spy = RepositorySpy.Create(); var gate = new AddNoteTests.Gate();
        var note = new RecordingNote { OnCall = (_, _, token) => gate.WaitAsync(token) };
        var parser = Parser(kind, spy, note);
        Set(parser, "activeAlarms", new List<ActiveAlarm> { Alarm() });
        using var source = new CancellationTokenSource();
        Task task = Parse(parser, source.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.False(task.IsCompleted);
            Assert.Equal(kind == 2 ? 0 : 1, spy.Calls.Count);
            Assert.Empty(spy.Archived); Assert.Empty(spy.CurrentStates); Assert.Equal(17, spy.Checkpoint);
            if (kind != 2) Assert.Equal(15, spy.Calls[0].Arguments[0]);
            gate.Release.TrySetResult();
            await task.WaitAsync(Deadline);
        }
        finally { source.Cancel(); gate.Release.TrySetResult(); }
        Assert.Equal(101, Assert.Single(spy.CurrentStates));
    }

    public static IEnumerable<object[]> CancellationCases()
    {
        foreach (object[] row in Parsers())
        foreach (string boundary in new[] { "before", "during", "after-write" }) yield return [row[0], boundary];
    }

    [Theory]
    [MemberData(nameof(CancellationCases))]
    public async Task CancellationIncludingAfterCommittedNotePreventsAllLaterStateAndCheckpoint(int kind, string boundary)
    {
        var spy = RepositorySpy.Create(); var fixture = new AddNoteTests.Fixture();
        var gate = new AddNoteTests.Gate();
        using var source = new CancellationTokenSource();
        if (boundary == "during") fixture.Boundary = (name, token) => name == "execute" ? gate.WaitAsync(token) : Task.CompletedTask;
        if (boundary == "after-write") fixture.AfterBoundary = name => { if (name == "execute") source.Cancel(); };
        var parser = Parser(kind, spy, fixture.Service);
        var alarm = Alarm(); Set(parser, "activeAlarms", new List<ActiveAlarm> { alarm, Alarm(102) });
        if (boundary == "before") source.Cancel();
        Task task = Parse(parser, source.Token);
        try
        {
            if (boundary == "during")
            {
                await gate.Entered.Task.WaitAsync(Deadline);
                Assert.False(task.IsCompleted); source.Cancel();
            }
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Deadline));
            Assert.Equal(source.Token, error.CancellationToken); Assert.True(task.IsCanceled);
        }
        finally { source.Cancel(); gate.Release.TrySetResult(); }
        Assert.Equal(boundary == "after-write" ? 1 : 0, fixture.Repository.Writes.Count);
        Assert.Empty(spy.Archived); Assert.Empty(spy.CurrentStates); Assert.Equal(17, spy.Checkpoint);
        Assert.Equal(boundary == "before" || kind == 2 ? 0 : 1, spy.Calls.Count);
        Assert.Equal(73, alarm.ProcessNextStep);
        Assert.All(fixture.Repository.Operations, op => Assert.True(op.Disposed));
        Assert.All(fixture.Tokens, token => Assert.Equal(source.Token, token));
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public async Task ReplayingSameAlarmReallyWritesTwiceWithNoDeduplication(int kind)
    {
        var spy = RepositorySpy.Create(); var fixture = new AddNoteTests.Fixture();
        var parser = Parser(kind, spy, fixture.Service);
        Set(parser, "activeAlarms", new List<ActiveAlarm> { Alarm() });
        await Parse(parser); await Parse(parser);
        Assert.Equal(new[]
        {
            ("Location 12.50/-45.125 —   address  ", "client"),
            ("Location 12.50/-45.125 —   address  ", "client")
        }, fixture.Repository.Writes);
        Assert.Equal(2, fixture.Location.Oids.Count); Assert.Equal(2, fixture.Address.Coordinates.Count);
        Assert.Equal(2, spy.Calls.Count(c => c.Name == "PrepareInsertIntoCurrentAlarmNotification"));
        Assert.All(fixture.Repository.Operations, op => Assert.True(op.Disposed));
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public async Task RealProfileSelectionCopiesPriority18AndExactInstructionThenExecutesNote(int kind)
    {
        var spy = RepositorySpy.Create(); var note = new RecordingNote();
        var parser = Parser(kind, spy, note); var alarm = Alarm(); alarm.Priority = 11; alarm.Instruction = "old";
        var item = new ProfileItem
        {
            Action = 18, Instruction = Template, StateNo = 2, StateTime = 5, NextState = 8,
            StartTime = DateTime.MinValue, EndTime = DateTime.MinValue.AddDays(1).AddTicks(-1),
            LoopStartState = -1, NumberOfLoops = 100, Email = "", RoleAction = 0, RoleID = 0
        };
        ((Dictionary<string, int>)Field(parser, "clientProfileMapping").GetValue(parser)!)["client"] = 10;
        ((Dictionary<int, Profile>)Field(parser, "activeProfiles").GetValue(parser)!)[10] = new Profile
        {
            ProfileID = 10, ProfileName = "note profile",
            Events = new() { ["event"] = Enumerable.Range(1, 7).ToDictionary(day => day, _ => new List<ProfileItem> { item }) }
        };
        var alarms = new List<ActiveAlarm> { alarm };
        await WorkflowRepositoryAdapterTests.CallAsync(parser, "getPriorityAndEmail", kind == 2 ? [alarms] : [alarms, 0]);
        Assert.Same(alarm, Assert.Single(alarms));
        Assert.Equal(18, alarm.Priority); Assert.Equal(Template, alarm.Instruction);
        Assert.Equal("note profile", alarm.ProfileName); Assert.Equal(10, alarm.ProfileID);
        Set(parser, "activeAlarms", alarms);
        await Parse(parser);
        Assert.Equal((Template, "client", CancellationToken.None), Assert.Single(note.Calls));
        Assert.Equal("Add Note", Assert.Single(spy.Calls, c => c.Name == "PrepareInsertIntoAlarmNotification").Arguments[3]);
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public async Task ProfileLoaderBuildsPriority18InstructionVerbatimBeforeSelectionAndExecution(int kind)
    {
        const string instruction = "  café {{ \tlat }}\r\n{{current_location_address}} 😀  ";
        var spy = RepositorySpy.Create(); var note = new RecordingNote();
        spy.Rows = call =>
        {
            Assert.Equal("PrepareReadAllProfiles", call.Name);
            string[] columns = ["ProfileID", "ProfileName", "Day", "EventCode", "StartTime", "EndTime", "Action",
                "HoldDuration", "GracePeriod", "Instruction", "EmailAddress", "EmailJoin", "StateNo", "StateTime",
                "FeedbackRequired", "ProfileType", "TimeIntervalsID", "NextState", "LoopStartState", "NumberOfLoops", "RoleID", "RoleAction"];
            var table = new DataTable();
            foreach (string name in columns) table.Columns.Add(name, typeof(object));
            for (int day = 1; day <= 7; day++)
                table.Rows.Add(10, "loaded note", day, "event", "00:00:00", "23:59:59", 18,
                    0, 0, instruction, "", 0, 2, 5, false, 0, 0, 8, -1, 100, 0, 0);
            return table;
        };
        var parser = Parser(kind, spy, note);
        using var loading = new CancellationTokenSource(Deadline);
        Assert.Equal(true, await WorkflowRepositoryAdapterTests.CallAsync(parser, "readAllProfiles", loading.Token));
        var profiles = (Dictionary<int, Profile>)Field(parser, "activeProfiles").GetValue(parser)!;
        Assert.All(profiles[10].Events["event"].Values, items =>
        {
            var item = Assert.Single(items);
            Assert.Equal(18, item.Action); Assert.Equal(instruction, item.Instruction);
            // The loader requires HH:mm:ss; extend only the selection window by one tick
            // so the selector's second-truncated clock includes 23:59:59 at midnight rollover.
            item.EndTime = item.EndTime.AddTicks(1);
        });
        var alarm = Alarm(); alarm.Priority = 11; alarm.Instruction = "old";
        ((Dictionary<string, int>)Field(parser, "clientProfileMapping").GetValue(parser)!)["client"] = 10;
        var alarms = new List<ActiveAlarm> { alarm };
        await WorkflowRepositoryAdapterTests.CallAsync(parser, "getPriorityAndEmail", kind == 2 ? [alarms] : [alarms, 0]);
        Assert.Equal(18, alarm.Priority); Assert.Equal(instruction, alarm.Instruction);
        // Return the spy to ordinary empty command results before the parse shell writes.
        spy.Rows = _ => new DataTable();
        Set(parser, "activeAlarms", alarms);
        await Parse(parser);
        Assert.Equal((instruction, "client", CancellationToken.None), Assert.Single(note.Calls));
        Assert.All(spy.Calls, call => Assert.True(call.Operation!.Disposed));
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("Step")]
    public async Task ActualHostResolvesNoteDefaultsAndAcceptsOfflineRepositoryWithoutStarting(string mode)
    {
        var fixture = new AddNoteTests.Fixture();
        using var host = Program.CreateHostBuilder(["--WorkerMode", mode, "--ConnectionStrings:connstr", "offline", "--Platform", "offline"])
            .ConfigureServices((_, services) => services.AddSingleton<IRepository>((IRepository)fixture.Repository)).Build();
        Assert.IsType<UnavailableNoteLocationProvider>(host.Services.GetRequiredService<INoteLocationProvider>());
        Assert.IsType<UnavailableNoteAddressResolver>(host.Services.GetRequiredService<INoteAddressResolver>());
        var note = host.Services.GetRequiredService<INoteService>(); Assert.IsType<NoteService>(note);
        await note.AddNoteAsync("{{lat}}/{{current_location_address}}", "oid");
        Assert.Equal(("N/A/N/A", "oid"), Assert.Single(fixture.Repository.Writes));
        // Do not start workers or resolve the notification sender/parser factory (static authentication).
    }

    [Fact]
    public async Task PreRegisteredProvidersAreUsedByDefaultNoteServiceRatherThanUnavailableDefaults()
    {
        var fixture = new AddNoteTests.Fixture();
        var services = new ServiceCollection().AddSingleton<IRepository>((IRepository)fixture.Repository)
            .AddSingleton<INoteLocationProvider>(fixture.Location).AddSingleton<INoteAddressResolver>(fixture.Address);
        services.AddNoteServices();
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<INoteService>().AddNoteAsync("{{current_location_address}}", "oid");
        Assert.Equal(("  address  ", "oid"), Assert.Single(fixture.Repository.Writes));
        Assert.Single(fixture.Location.Oids); Assert.Single(fixture.Address.Coordinates);
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void LegacyRepositoryConstructorSuppliesDefaultNoteServiceWithoutInitializingSender(int kind)
    {
        var spy = RepositorySpy.Create();
        var sender = (NotificationSender)RuntimeHelpers.GetUninitializedObject(typeof(NotificationSender));
        object parser = kind switch
        {
            0 => new WorkFlowCommon(NullLogger<WorkFlowCommon>.Instance, Configuration(), sender, (IRepository)spy),
            1 => new WorkFlowInitiator(NullLogger<WorkFlowInitiator>.Instance, Configuration(), sender, (IRepository)spy),
            _ => new WorkFlowSteps(NullLogger<WorkFlowSteps>.Instance, Configuration(), (IRepository)spy)
        };
        Assert.IsType<NoteService>(Field(parser, "noteService").GetValue(parser));
        Assert.Empty(spy.Calls);
        Assert.Equal(kind == 2 ? new[] { 2, 3, 4 } : [3, 4, 5], parser.GetType().GetConstructors().Select(c => c.GetParameters().Length).Order());
    }

    private sealed class RecordingNote : INoteService
    {
        public List<(string Template, string Oid, CancellationToken Token)> Calls { get; } = [];
        public Func<string, string, CancellationToken, Task> OnCall { get; set; } = (_, _, _) => Task.CompletedTask;
        public Task AddNoteAsync(string template, string oid, CancellationToken cancellationToken = default)
        { Calls.Add((template, oid, cancellationToken)); return OnCall(template, oid, cancellationToken); }
    }

    private static object Parser(int kind, RepositorySpy spy, INoteService note)
    {
        var sender = (NotificationSender)RuntimeHelpers.GetUninitializedObject(typeof(NotificationSender));
        return kind switch
        {
            0 => new WorkFlowCommon(NullLogger<WorkFlowCommon>.Instance, Configuration(), sender, (IRepository)spy, note),
            1 => new WorkFlowInitiator(NullLogger<WorkFlowInitiator>.Instance, Configuration(), sender, (IRepository)spy, note),
            2 => new WorkFlowSteps(NullLogger<WorkFlowSteps>.Instance, Configuration(), (IRepository)spy, note),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["ConnectionStrings:connstr"] = "offline", ["ParserID"] = "47", ["OfflineParserID"] = "47", ["DefaultMcAppFlag"] = "0" }).Build();

    private static ActiveAlarm Alarm(int id = 101) => new()
    {
        SystemID = id, HistoryID = id + 1000, AlarmSystemID = 901, AgencyID = 31, ClientSystemID = 41,
        ClientID = "client", ClientTZ = "UTC", AlarmID = "event", DeviceID = "device", Priority = 18,
        POGroupNum = "group", POGroup1 = "", POGroup2 = "", POGroup3 = "", Instruction = Template,
        StateNo = 2, CurrentStateNo = 4, NextStateNo = 8, CurrentLoopNumber = 3, ProcessNextStep = 73,
        StateTime = 5, EventDateTime = 1_700_000_000, ReceivedDateTime = 1_700_000_060,
        EventDateTimeLocal = "2023-11-14 22:13:20", EventDateTimeUTC = "2023-11-14 22:13:20",
        EmailAddresses = "", EmailJoin = 0, ProfileID = 10, ProfileName = "profile", AlarmText = "alarm",
        MEZEventVictimID = "", ZoneID = "zone", ZoneCategory = "category", ZoneName = "name",
        ZoneAddress = "address", PolyZoneName = "poly", OffenderName = "offender"
    };

    private static Task Parse(object parser, CancellationToken token = default) => parser is WorkFlowSteps steps
        ? steps.parseAlarmsAsync(token) : ((WorkFlowCommon)parser).parseAlarmsAsync(token);
    private static void Set(object target, string name, object value) => Field(target, name).SetValue(target, value);
    private static FieldInfo Field(object target, string name)
    {
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly) is { } field)
                return field;
        throw new MissingFieldException(target.GetType().FullName, name);
    }
}