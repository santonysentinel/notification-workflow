using System.Collections;
using System.Data;
using System.Data.Common;
using System.Reflection;
using ActiveAlarmsParser;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Parser;
using NotificationWorkflowService.Parser.Actions;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service;
using Xunit;
using WorkflowRepository = NotificationWorkflowService.Repository.Repository;

namespace NotificationWorkflowService.Tests;

/// <summary>
/// Native async, instance-local cancellation contracts. Gates, not sleeps, establish
/// ordering. Never start a host, initialize a sender/settings, open SQL or reach Title.
/// The adapter suite's exposed async reflection helper also traverses inherited privates.
/// </summary>
public class WorkflowAsyncCancellationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly string[] ReferenceFields =
    [
        "clientProfileMapping", "clientHolidayProfileMapping", "activeProfiles", "activeHolidays",
        "roles", "victims", "victimTypeDict", "MEZVictims", "AttachedVictimZones", "ClearEvents"
    ];
    private static readonly string[] SetupCalls =
    [
        "PrepareFetchClientProfile", "PrepareFetchClientProfile", "PrepareReadAllProfiles",
        "PrepareReadAllHolidays", "PrepareReadAllRoles", "PrepareReadVictims",
        "PrepareReadMEZVictims", "PrepareReadAttachedVictimZones", "PrepareReadProfileItemsClear"
    ];

    public static IEnumerable<object[]> PublicParserCalls()
    {
        for (int kind = 0; kind < 3; kind++)
        {
            foreach (string method in kind == 2
                ? new[] { "setUpParserAsync", "getExpiredAlarmsAsync", "parseAlarmsAsync" }
                : new[] { "setUpParserAsync", "readPointsAsync", "parseAlarmsAsync", "PushAlertsToVictimsAsync", "PushNotificationToVictimAsync" })
                yield return [kind, method];
        }
    }

    [Theory]
    [MemberData(nameof(PublicParserCalls))]
    public async Task EveryPublicParserEntryRejectsPreCancellationBeforePreparingAnything(int kind, string method)
    {
        var spy = CancellationRepository.Create();
        object parser = Parser(kind, spy);
        var old = SeedSnapshot(parser, kind);
        var alarms = new List<ActiveAlarm> { Alarm() };
        Set(parser, "activeAlarms", alarms);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        object?[] arguments = method == "setUpParserAsync" ? ["Offline", cancellation.Token]
            : method.StartsWith("Push", StringComparison.Ordinal) ? [alarms[0], cancellation.Token]
            : [cancellation.Token];

        Task task = Call(parser, method, arguments);
        await AssertCanceled(task, cancellation.Token);

        Assert.Empty(spy.Calls);
        Assert.Same(alarms, Get(parser, "activeAlarms"));
        AssertSnapshot(parser, old);
        Assert.Null(Get(parser, "pendingReferenceData"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServicePreCancellationStopsBeforeNormalFactoryOrStepParserConstruction(bool step)
    {
        var spy = CancellationRepository.Create();
        int factories = 0;
        // Missing connection string would make construction of the step parser fail.
        var configuration = new ConfigurationBuilder().Build();
        var service = Service(spy, configuration, () => { factories++; throw new InvalidOperationException("Factory must not run"); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertCanceled(step ? service.startParseSteps("Offline", cancellation.Token)
            : service.startParse("Offline", cancellation.Token), cancellation.Token);

        Assert.Equal(0, factories);
        Assert.Empty(spy.Calls);
    }

    public static IEnumerable<object[]> SetupCancellationPositions()
    {
        for (int kind = 0; kind < 3; kind++)
        for (int position = 0; position < SetupCount(kind); position++)
        foreach (string boundary in new[] { "Open", "ExecuteReader", "Read" })
            yield return [kind, position, boundary];
    }

    [Theory]
    [MemberData(nameof(SetupCancellationPositions))]
    public async Task SetupCancellationAtEveryAwaitRetainsEntireOldSnapshotAndDiscardsEarlierStaging(
        int kind, int position, string boundary)
    {
        var spy = CancellationRepository.Create();
        // The first two completed loaders stage real, different mappings, not just empties.
        spy.Rows = call => call.Name == "PrepareFetchClientProfile"
            ? Table(["OID", "ProfileID"], ["fresh", 41]) : new DataTable();
        object parser = Parser(kind, spy);
        var old = SeedSnapshot(parser, kind);
        using var cancellation = new CancellationTokenSource();
        var gate = new AsyncGate();
        spy.OnBoundary = (operation, name, token) => operation.Index == position && name == boundary
            ? gate.WaitAsync(token) : Task.CompletedTask;

        Task task = Call(parser, "setUpParserAsync", "Offline", cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.False(task.IsCompleted);
            Assert.NotNull(Get(parser, "pendingReferenceData"));
            AssertSnapshot(parser, old);
            Assert.Equal(SetupCalls.Take(position + 1), spy.Calls.Select(c => c.Name));
            Assert.All(spy.Calls.Take(position), c => Assert.True(c.Operation!.Disposed));
            if (position > 0)
                Assert.NotSame(old["clientProfileMapping"].Dictionary,
                    Get(Get(parser, "pendingReferenceData")!, "<ClientProfileMapping>k__BackingField"));
            cancellation.Cancel();
            await AssertCanceled(task, cancellation.Token);
        }
        finally { cancellation.Cancel(); }

        AssertSnapshot(parser, old);
        Assert.Null(Get(parser, "pendingReferenceData"));
        Assert.Equal(position + 1, spy.Calls.Count);
        AssertCleanupAndTokens(spy, cancellation.Token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DirectLoaderCancellationDuringSecondReadDoesNotPublishPartiallyMappedRows(int kind)
    {
        var spy = CancellationRepository.Create();
        spy.Rows = _ => Table(["OID", "ProfileID"], ["fresh", 41], ["later", 42]);
        object parser = Parser(kind, spy);
        var old = SeedSnapshot(parser, kind);
        using var cancellation = new CancellationTokenSource();
        var gate = new AsyncGate();
        spy.OnBoundary = (operation, name, token) => name == "Read" && operation.Reader!.ReadCalls == 2
            ? gate.WaitAsync(token) : Task.CompletedTask;

        Task task = Call(parser, "FetchClientProfileAsync", 0, cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            var reader = Assert.Single(spy.Calls).Operation!.Reader!;
            Assert.Equal(1, reader.RowsRead);
            Assert.True(reader.ColumnReads > 0);
            Assert.False(task.IsCompleted);
            AssertSnapshot(parser, old);
            cancellation.Cancel();
            await AssertCanceled(task, cancellation.Token);
            Assert.Equal(1, reader.RowsRead);
        }
        finally { cancellation.Cancel(); }

        AssertSnapshot(parser, old);
        Assert.Null(Get(parser, "pendingReferenceData"));
        AssertCleanupAndTokens(spy, cancellation.Token);
    }

    public static IEnumerable<object[]> ReadCalls()
    {
        for (int kind = 0; kind < 4; kind++)
        foreach (string method in kind == 2 ? new[] { "getExpiredAlarmsAsync" }
            : new[] { "ReadCurrentActiveAlarmPointAsync", "readLastSuccessfulProcessAsync" })
        foreach (string boundary in new[] { "Open", "ExecuteReader", "Read" })
            yield return [kind, method, boundary];
    }

    [Theory]
    [MemberData(nameof(ReadCalls))]
    public async Task ParserAndServiceReadersForwardTokenAndDisposeWhenAwaitCanceled(int kind, string method, string boundary)
    {
        var spy = CancellationRepository.Create();
        object target = kind == 3 ? Service(spy) : Parser(kind, spy);
        using var cancellation = new CancellationTokenSource();
        var gate = new AsyncGate();
        spy.OnBoundary = (_, name, token) => name == boundary ? gate.WaitAsync(token) : Task.CompletedTask;
        Task task = method == "readLastSuccessfulProcessAsync" ? Call(target, method, 47, cancellation.Token)
            : Call(target, method, cancellation.Token);

        await CancelAtGate(task, gate, cancellation);

        Assert.Single(spy.Calls);
        if (kind == 2) Assert.Empty((List<ActiveAlarm>)Get(target, "activeAlarms")!);
        AssertCleanupAndTokens(spy, cancellation.Token);
    }

    [Theory]
    [InlineData(0, "ReadCurrentActiveAlarmPointAsync")]
    [InlineData(0, "readLastSuccessfulProcessAsync")]
    [InlineData(1, "ReadCurrentActiveAlarmPointAsync")]
    [InlineData(1, "readLastSuccessfulProcessAsync")]
    [InlineData(3, "ReadCurrentActiveAlarmPointAsync")]
    [InlineData(3, "readLastSuccessfulProcessAsync")]
    public async Task PrivateCheckpointReadersAlsoRejectPreCancellationWithoutPreparation(int kind, string method)
    {
        var spy = CancellationRepository.Create();
        object target = kind == 3 ? Service(spy) : Parser(kind, spy);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertCanceled(method == "readLastSuccessfulProcessAsync" ? Call(target, method, 47, cancellation.Token)
            : Call(target, method, cancellation.Token), cancellation.Token);

        Assert.Empty(spy.Calls);
    }

    public static IEnumerable<object[]> ServiceAndWorkerCases()
    {
        foreach (bool worker in new[] { false, true })
        foreach (bool step in new[] { false, true })
        foreach (bool setup in new[] { false, true })
            yield return [worker, step, setup];
    }

    [Theory]
    [MemberData(nameof(ServiceAndWorkerCases))]
    public async Task ActualWorkerAndServicePassStoppingTokenThroughSetupAndFirstLoop(bool worker, bool step, bool setup)
    {
        var spy = CancellationRepository.Create();
        var configuration = Configuration();
        var parser = (WorkFlowCommon)Parser(0, spy, configuration);
        int factories = 0;
        var service = Service(spy, configuration, () => { factories++; return parser; });
        using BackgroundService background = step
            ? new StepWorker(NullLogger<StepWorker>.Instance, service, configuration)
            : new Worker(NullLogger<Worker>.Instance, service, configuration);
        using var cancellation = new CancellationTokenSource();
        var gate = new AsyncGate();
        // For normal mode, service reads current + checkpoint before readPoints' first
        // checkpoint. Block that checkpoint before readPoints touches Console.Title.
        int normalCheckpointReads = 0;
        spy.OnBoundary = (operation, name, token) =>
        {
            bool selected = setup ? operation.Index == 1 && name == "ExecuteReader"
                : step ? operation.Call.Name == "PrepareGetExpiredAlarms" && name == "Read"
                : operation.Call.Name == "PrepareReadLastSuccessfulProcess" && name == "ExecuteReader"
                    && ++normalCheckpointReads == 2;
            return selected ? gate.WaitAsync(token) : Task.CompletedTask;
        };
        Task task = worker ? InvokeTask(background, "ExecuteAsync", cancellation.Token)
            : step ? service.startParseSteps("Offline", cancellation.Token) : service.startParse("Offline", cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.False(task.IsCompleted);
            cancellation.Cancel();
            if (worker)
            {
                await task.WaitAsync(Deadline); // Only the host-shutdown boundary consumes OCE.
                Assert.True(task.IsCompletedSuccessfully);
            }
            else await AssertCanceled(task, cancellation.Token);
        }
        finally { cancellation.Cancel(); }

        Assert.Equal(step ? 0 : 1, factories);
        Assert.Equal(setup ? 2 : step ? 7 : 12, spy.Calls.Count);
        Assert.Equal(SetupCalls.Take(step ? 6 : 9).Take(setup ? 2 : 9),
            spy.Calls.Take(setup ? 2 : step ? 6 : 9).Select(c => c.Name));
        Assert.Empty(spy.Archived);
        Assert.Empty(spy.CurrentStates);
        Assert.Equal(17, spy.Checkpoint);
        AssertCleanupAndTokens(spy, cancellation.Token);
        if (!step) Assert.Null(Get(parser, "pendingReferenceData"));
    }

    // Independently specified callback sequences include every read, write and role branch.
    private static readonly (int Priority, int Role, string[] Calls)[] Actions =
    [
        (1, 0, []), (2, 0, ["officers", "queue", "audit", "history"]),
        (3, 0, ["officers", "mcapp"]), (4, 0, ["officers"]),
        (5, 0, ["officers", "queue", "insertEmails", "audit", "history"]),
        (6, 0, ["officers", "mcapp"]),
        (7, 0, ["mcapp", "officers", "queue", "audit", "history"]),
        (9, 0, ["officers", "mcapp", "queue", "insertEmails", "audit", "history"]),
        (11, 0, []), (12, 1, ["mcapp"]), (12, 2, ["queue", "audit", "history"]),
        (12, 3, ["queue", "audit", "history"]),
        (13, 0, ["victimQueue", "audit", "history"]), (14, 0, ["officers", "mcapp"]),
        (15, 0, ["clientEmail", "victimQueue", "audit", "history"]),
        (16, 0, ["clientText", "victimQueue", "audit", "history"]),
        (17, 0, ["victimQueue", "audit", "history"]),
        (0, 0, []), (8, 0, []), (10, 0, []), (18, 0, []), (-1, 0, [])
    ];

    public static IEnumerable<object[]> ActionCases()
    {
        foreach (bool step in new[] { false, true })
        foreach (var action in Actions) yield return [step, action.Priority, action.Role];
    }

    public static IEnumerable<object[]> ActionCancellationPositions()
    {
        foreach (object[] row in ActionCases())
        for (int position = 0; position < ActionSequence((int)row[1], (int)row[2]).Length; position++)
            yield return [.. row, position];
    }

    [Theory]
    [MemberData(nameof(ActionCases))]
    public async Task ExecutorPreCancellationRejectsEveryPriorityModeAndRoleBeforeCallbacks(bool step, int priority, int role)
    {
        var operations = new CancellationActions();
        var alarm = Alarm(priority);
        alarm.RoleAction = role;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertCanceled(WorkflowActionExecutor.ExecuteAsync(alarm, Context(step, operations), cancellation.Token), cancellation.Token);

        Assert.Empty(operations.Calls);
        Assert.Equal(73, alarm.ProcessNextStep);
    }

    [Theory]
    [MemberData(nameof(ActionCancellationPositions))]
    public async Task CancellationDuringEveryExecutorCallbackPropagatesWithoutLocalRecoveryOrLaterMutation(
        bool step, int priority, int role, int position)
    {
        var operations = new CancellationActions { BlockAt = position };
        var alarm = Alarm(priority);
        alarm.RoleAction = role;
        using var cancellation = new CancellationTokenSource();
        Task task = WorkflowActionExecutor.ExecuteAsync(alarm, Context(step, operations), cancellation.Token);

        await CancelAtGate(task, operations.Gate, cancellation);

        Assert.Equal(ActionSequence(priority, role).Take(position + 1), operations.Calls);
        Assert.All(operations.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.All(operations.ProcessNextValues, value => Assert.Equal(73, value));
        Assert.Equal(73, alarm.ProcessNextStep);
        Assert.Equal(2, alarm.StateNo);
        Assert.Equal(4, alarm.CurrentStateNo);
        Assert.Equal(8, alarm.NextStateNo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutorAwaitsFirstCallbackBeforeBeginningNextOne(bool step)
    {
        var operations = new CancellationActions { BlockAt = 0 };
        var alarm = Alarm(3);
        using var cancellation = new CancellationTokenSource();
        Task<WorkflowActionResult> task = WorkflowActionExecutor.ExecuteAsync(alarm, Context(step, operations), cancellation.Token);
        try
        {
            await operations.Gate.Entered.Task.WaitAsync(Deadline);
            Assert.False(task.IsCompleted);
            Assert.Equal(new[] { "officers" }, operations.Calls);
            Assert.Equal(73, alarm.ProcessNextStep);
            operations.Gate.Release.TrySetResult();
            await task.WaitAsync(Deadline);
        }
        finally { cancellation.Cancel(); }

        Assert.Equal(new[] { "officers", "mcapp" }, operations.Calls);
        Assert.Equal(0, alarm.ProcessNextStep);
        Assert.All(operations.ProcessNextValues, value => Assert.Equal(73, value));
        Assert.All(operations.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    public static IEnumerable<object[]> BatchCancellationPositions()
    {
        for (int kind = 0; kind < 3; kind++)
        foreach (int alarmIndex in new[] { 0, 1 })
        foreach (string operation in new[] { "PreparePushAlertToMcApp", "PrepareInsertIntoAlarmNotification", "PrepareInsertIntoCurrentAlarmNotification" })
            yield return [kind, alarmIndex, operation];
        for (int kind = 0; kind < 2; kind++) yield return [kind, 2, "PrepareUpdateParserActivty"];
    }

    [Theory]
    [MemberData(nameof(BatchCancellationPositions))]
    public async Task ParserCancellationStopsBatchBeforeNextAlarmOrCheckpointAndRetainsOnlyCompletedWrites(
        int kind, int alarmIndex, string prepare)
    {
        var spy = CancellationRepository.Create();
        object parser = Parser(kind, spy);
        var alarms = Enumerable.Range(0, 3).Select(i => Alarm(prepare == "PreparePushAlertToMcApp" ? 3 : 11, 101 + i)).ToList();
        Set(parser, "activeAlarms", alarms);
        using var cancellation = new CancellationTokenSource();
        var gate = new AsyncGate();
        spy.OnBoundary = (operation, name, token) => operation.Call.Name == prepare && name == "ExecuteNonQuery"
            && (prepare == "PrepareUpdateParserActivty" || CallAlarmID(operation.Call) == alarms[alarmIndex].SystemID)
            ? gate.WaitAsync(token) : Task.CompletedTask;

        await CancelAtGate(Call(parser, "parseAlarmsAsync", cancellation.Token), gate, cancellation);

        int[] completed = alarms.Take(prepare == "PrepareUpdateParserActivty" ? 3 : alarmIndex).Select(a => a.SystemID).ToArray();
        Assert.Equal(prepare == "PrepareInsertIntoCurrentAlarmNotification" ? completed.Append(alarms[alarmIndex].SystemID) : completed, spy.Archived);
        Assert.Equal(completed, spy.CurrentStates);
        Assert.Equal(17, spy.Checkpoint);
        Assert.Equal(prepare, spy.Calls[^1].Name);
        if (prepare != "PrepareUpdateParserActivty")
        {
            Assert.DoesNotContain(spy.Calls, c => c.Name == "PrepareUpdateParserActivty");
            Assert.DoesNotContain(spy.Calls, c => CallAlarmID(c) == alarms[alarmIndex + 1].SystemID);
        }
        Assert.All(alarms.Skip(alarmIndex), alarm => Assert.Equal(73, alarm.ProcessNextStep));
        AssertCleanupAndTokens(spy, cancellation.Token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OrdinaryExecutionFailureEntersCancelableRetryDelayWithoutSleepingOrSecondAttempt(int kind)
    {
        var spy = CancellationRepository.Create();
        var thrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        spy.OnBoundary = (_, boundary, _) =>
        {
            if (boundary == "ExecuteReader")
            {
                thrown.TrySetResult();
                throw new InvalidOperationException("Ordinary offline failure, not SqlException");
            }
            return Task.CompletedTask;
        };
        object target = kind == 3 ? Service(spy) : Parser(kind, spy);
        using var cancellation = new CancellationTokenSource();
        // Each await before the delay completes inline: returning from Call establishes
        // that the catch has reached Task.Delay, not merely that execution was attempted.
        Task task = kind == 3 ? Call(target, "ReadCurrentActiveAlarmPointAsync", cancellation.Token)
            : Call(target, "FetchClientProfileAsync", 0, cancellation.Token);
        try
        {
            await thrown.Task.WaitAsync(Deadline);
            Assert.False(task.IsCompleted);
            cancellation.Cancel();
            await AssertCanceled(task, cancellation.Token);
        }
        finally { cancellation.Cancel(); }

        var operation = Assert.Single(spy.Calls).Operation!;
        Assert.Equal(1, operation.Events.Count(e => e == "Open"));
        Assert.Equal(1, operation.Events.Count(e => e == "ExecuteReader"));
        AssertCleanupAndTokens(spy, cancellation.Token);
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("ExecuteReader")]
    [InlineData("ExecuteNonQuery")]
    public async Task RealRepositoryPreCancellationReturnsCanceledTaskWithoutNetworkAndDisposesClosedConnection(string entry)
    {
        var repository = new WorkflowRepository(Configuration());
        var operation = Assert.IsType<WorkflowRepository.WorkflowOperation>(repository.PrepareReadAllRoles());
        SqlConnection connection = operation.Command.Connection!;
        Assert.Equal(ConnectionState.Closed, connection.State); // Prepare is synchronous and offline.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task? task = null;
        Exception? synchronousFailure;
        try
        {
            synchronousFailure = Record.Exception(() =>
            {
                task = entry switch
                {
                    "Open" => operation.OpenAsync(cancellation.Token),
                    "ExecuteReader" => operation.ExecuteReaderAsync(cancellation.Token),
                    _ => operation.ExecuteNonQueryAsync(cancellation.Token)
                };
            });
            // Check safety/cleanup even if the Task-returning API unexpectedly throws inline.
            Assert.Equal(ConnectionState.Closed, connection.State);
            if (task is not null) await AssertCanceled(task, cancellation.Token);
            else Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(synchronousFailure).CancellationToken);
        }
        finally
        {
            await operation.CloseAsync();
            await operation.DisposeAsync();
            await operation.DisposeAsync(); // Idempotent asynchronous ownership cleanup.
        }
        Assert.Equal(ConnectionState.Closed, connection.State);
        // SqlClient retains the connection reference on a disposed command; disposal
        // is not a promise to null it. Check the owner's state and closed connection.
        Assert.Same(connection, operation.Command.Connection);
        Assert.True((bool)Get(operation, "disposed")!);
        Assert.Null(synchronousFailure); // Regression contract: cancellation belongs on the Task.
        Assert.NotNull(task);
        Assert.True(task.IsCanceled);
    }

    private static string[] ActionSequence(int priority, int role) => Assert.Single(Actions, a => a.Priority == priority && a.Role == role).Calls;
    private static int SetupCount(int kind) => kind == 2 ? 6 : 9;
    private static Task<object?> Call(object target, string method, params object?[] arguments) =>
        WorkflowRepositoryAdapterTests.CallAsync(target, method, arguments);

    private static Task InvokeTask(object target, string method, CancellationToken token)
    {
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
            if (type.GetMethod(method, Declared) is { } info)
                return Assert.IsAssignableFrom<Task>(info.Invoke(target, [token]));
        throw new MissingMethodException(target.GetType().FullName, method);
    }

    private static async Task AssertCanceled(Task task, CancellationToken token)
    {
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Deadline));
        Assert.Equal(token, exception.CancellationToken);
        Assert.True(task.IsCanceled);
    }

    private static async Task CancelAtGate(Task task, AsyncGate gate, CancellationTokenSource cancellation)
    {
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.False(task.IsCompleted);
            cancellation.Cancel();
            await AssertCanceled(task, cancellation.Token);
        }
        finally { cancellation.Cancel(); }
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:connstr"] = "Server=offline.invalid;Database=Offline;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=1;Pooling=false",
        ["Platform"] = "Offline", ["ParserID"] = "47", ["OfflineParserID"] = "47",
        ["ParserRefreshTime"] = "60", ["StepParserRefreshTime"] = "60", ["StepParserSleepTime"] = "60000",
        ["DefaultMcAppFlag"] = "0", ["NumberOfProcessPoints"] = "10"
    }).Build();

    private static object Parser(int kind, CancellationRepository spy, IConfiguration? configuration = null) => kind switch
    {
        0 => new WorkFlowCommon(NullLogger<WorkFlowCommon>.Instance, configuration ?? Configuration(), null!, (IRepository)spy),
        1 => new WorkFlowInitiator(NullLogger<WorkFlowInitiator>.Instance, configuration ?? Configuration(), null!, (IRepository)spy),
        2 => new WorkFlowSteps(NullLogger<WorkFlowSteps>.Instance, configuration ?? Configuration(), (IRepository)spy),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static WorkFlowInitiatorService Service(CancellationRepository spy, IConfiguration? configuration = null, Func<WorkFlowCommon>? factory = null) =>
        new(NullLogger<WorkFlowInitiatorService>.Instance, configuration ?? Configuration(), NullLoggerFactory.Instance,
            factory ?? (() => throw new InvalidOperationException("Reader must not construct parser")), (IRepository)spy);

    private static ActiveAlarm Alarm(int priority = 11, int systemID = 101) => new()
    {
        SystemID = systemID, HistoryID = systemID + 1000, ClientID = "client", ClientTZ = "UTC",
        AlarmID = "event", POGroupNum = "group", POGroup1 = "", POGroup2 = "", POGroup3 = "",
        Priority = priority, RoleID = 61, Instruction = "instruction", EmailAddresses = "officer@example.test",
        EmailJoin = 0, ProfileID = 10, StateNo = 2, CurrentStateNo = 4, NextStateNo = 8,
        StateTime = 5, ProcessNextStep = 73, EventDateTime = 1_700_000_000, ReceivedDateTime = 1_700_000_060,
        EventDateTimeLocal = "2023-11-14 22:13:20", EventDateTimeUTC = "2023-11-14 22:13:20"
    };

    private static WorkflowActionContext Context(bool step, CancellationActions operations) => new(
        step ? WorkflowActionMode.Step : WorkflowActionMode.Normal, "Offline", NullLogger.Instance, operations,
        new Dictionary<int, string> { [1] = "Call", [2] = "E-mail", [3] = "Text" },
        new Dictionary<string, string> { ["61"] = "Supervisor" },
        new Dictionary<string, List<Victim>> { ["client"] = [new() { Email = "victim@example.test", CellPhone = "123" }] });

    private static FieldInfo Field(object target, string name)
    {
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, Declared) is { } field) return field;
        throw new MissingFieldException(target.GetType().FullName, name);
    }
    private static object? Get(object target, string name) => Field(target, name).GetValue(target);
    private static void Set(object target, string name, object value) => Field(target, name).SetValue(target, value);
    private sealed record SnapshotEntry(IDictionary Dictionary, object Key, object Value);

    private static Dictionary<string, SnapshotEntry> SeedSnapshot(object parser, int kind)
    {
        var snapshot = new Dictionary<string, SnapshotEntry>();
        foreach (string name in ReferenceFields.Take(kind == 2 ? 6 : ReferenceFields.Length))
        {
            var dictionary = (IDictionary)Get(parser, name)!;
            Type[] types = dictionary.GetType().GenericTypeArguments;
            object key = types[0] == typeof(int) ? -99 : "stale";
            object value = types[1] == typeof(int) ? -99 : types[1] == typeof(string) ? "old-value"
                : Activator.CreateInstance(types[1])!;
            dictionary.Add(key, value);
            snapshot.Add(name, new(dictionary, key, value));
        }
        return snapshot;
    }

    private static void AssertSnapshot(object parser, Dictionary<string, SnapshotEntry> snapshot)
    {
        foreach (var (name, old) in snapshot)
        {
            Assert.Same(old.Dictionary, Get(parser, name));
            Assert.Single(old.Dictionary.Keys);
            Assert.Equal(old.Value, old.Dictionary[old.Key]);
        }
    }

    private static int? CallAlarmID(CancellationCall call) => call.Name switch
    {
        "PreparePushAlertToMcApp" => ((ActiveAlarm)call.Arguments[0]!).SystemID,
        "PrepareCreateAlarmAudit" => (int)call.Arguments[2]!,
        "PrepareInsertIntoAlarmNotification" or "PrepareInsertIntoCurrentAlarmNotification" => (int)call.Arguments[0]!,
        _ => null
    };

    private static void AssertCleanupAndTokens(CancellationRepository spy, CancellationToken token)
    {
        Assert.NotEmpty(spy.Calls);
        foreach (var call in spy.Calls)
        {
            var operation = call.Operation!;
            Assert.True(operation.Disposed);
            Assert.Equal(1, operation.DisposeCalls);
            Assert.Equal("Dispose", operation.Events[^1]);
            Assert.DoesNotContain("Close", operation.Events); // Cancellation must not enter retry recovery.
            Assert.NotEmpty(operation.Tokens);
            Assert.All(operation.Tokens, observed => Assert.Equal(token, observed));
            if (operation.Reader is { } reader)
            {
                Assert.True(reader.IsClosed);
                Assert.Equal(1, reader.DisposeCalls);
                Assert.All(reader.Tokens, observed => Assert.Equal(token, observed));
                Assert.True(spy.Events.IndexOf($"{operation.Index}:ReaderDispose") < spy.Events.IndexOf($"{operation.Index}:Dispose"));
            }
            if (operation.Index + 1 < spy.Calls.Count)
                Assert.True(spy.Events.IndexOf($"{operation.Index}:Dispose") < spy.Events.IndexOf($"{operation.Index + 1}:Prepare"));
        }
    }

    private static DataTable Table(string[] columns, params object[][] rows)
    {
        var table = new DataTable();
        foreach (string column in columns) table.Columns.Add(column, typeof(object));
        foreach (object[] row in rows) table.Rows.Add(row);
        return table;
    }

    public sealed class AsyncGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
        }
    }

    public sealed record CancellationCall(string Name, object?[] Arguments)
    {
        public CancellationOperation? Operation { get; set; }
    }

    // Extends the adapter spy pattern with real asynchronous, cancellation-aware
    // Open/Execute/Read gates and committed-write tracking; Prepare never performs I/O.
    public class CancellationRepository : DispatchProxy
    {
        public List<CancellationCall> Calls { get; } = [];
        public List<string> Events { get; } = [];
        public List<int> Archived { get; } = [];
        public List<int> CurrentStates { get; } = [];
        public int Checkpoint { get; private set; } = 17;
        public Func<CancellationCall, DataTable> Rows { get; set; } = _ => new DataTable();
        public Func<CancellationOperation, string, CancellationToken, Task> OnBoundary { get; set; } = (_, _, _) => Task.CompletedTask;
        public static CancellationRepository Create() => (CancellationRepository)DispatchProxy.Create<IRepository, CancellationRepository>();
        protected override object Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(targetMethod);
            Assert.Equal(typeof(IWorkflowOperation), targetMethod.ReturnType);
            var call = new CancellationCall(targetMethod.Name, args?.ToArray() ?? []);
            int index = Calls.Count;
            Calls.Add(call);
            Events.Add($"{index}:Prepare");
            call.Operation = new CancellationOperation(this, call, index, Rows(call));
            return call.Operation;
        }
        internal void Commit(CancellationCall call)
        {
            switch (call.Name)
            {
                case "PrepareInsertIntoAlarmNotification": Archived.Add((int)call.Arguments[0]!); break;
                case "PrepareInsertIntoCurrentAlarmNotification": CurrentStates.Add((int)call.Arguments[0]!); break;
                case "PrepareUpdateParserActivty": Checkpoint = (int?)call.Arguments[0] ?? Checkpoint; break;
            }
        }
    }

    public sealed class CancellationOperation(CancellationRepository spy, CancellationCall call, int index, DataTable rows) : IWorkflowOperation
    {
        public CancellationCall Call { get; } = call;
        public int Index { get; } = index;
        public List<string> Events { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public CancellationReader? Reader { get; private set; }
        public bool Disposed { get; private set; }
        public int DisposeCalls { get; private set; }
        private bool opened;
        internal async Task Boundary(string name, CancellationToken token)
        {
            Assert.False(Disposed);
            token.ThrowIfCancellationRequested();
            Tokens.Add(token);
            Events.Add(name);
            spy.Events.Add($"{Index}:{name}");
            await spy.OnBoundary(this, name, token);
            token.ThrowIfCancellationRequested();
        }
        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            Assert.False(opened);
            await Boundary("Open", cancellationToken);
            opened = true;
        }
        public async Task<DbDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
        {
            Assert.True(opened);
            await Boundary("ExecuteReader", cancellationToken);
            Reader = new CancellationReader(rows.CreateDataReader(), this, () => spy.Events.Add($"{Index}:ReaderDispose"));
            return Reader;
        }
        public async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
        {
            Assert.True(opened);
            await Boundary("ExecuteNonQuery", cancellationToken);
            spy.Commit(Call);
            return -7;
        }
        public Task CloseAsync() { Events.Add("Close"); opened = false; return Task.CompletedTask; }
        public async ValueTask DisposeAsync()
        {
            Assert.False(Disposed);
            DisposeCalls++;
            if (Reader is { IsClosed: false }) await Reader.DisposeAsync();
            Events.Add("Dispose");
            spy.Events.Add($"{Index}:Dispose");
            rows.Dispose();
            Disposed = true;
        }
    }

    public sealed class CancellationReader(DbDataReader inner, CancellationOperation operation, Action onDispose) : DbDataReader
    {
        public List<CancellationToken> Tokens { get; } = [];
        public int ReadCalls { get; private set; }
        public int RowsRead { get; private set; }
        public int ColumnReads { get; private set; }
        public int DisposeCalls { get; private set; }
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            ReadCalls++;
            await operation.Boundary("Read", cancellationToken);
            bool found = await inner.ReadAsync(cancellationToken);
            if (found) RowsRead++;
            return found;
        }
        public override async ValueTask DisposeAsync()
        {
            Assert.Equal(0, DisposeCalls++);
            await inner.DisposeAsync();
            onDispose();
            GC.SuppressFinalize(this);
        }
        public override bool Read() => throw new InvalidOperationException("Synchronous read forbidden");
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
        public override bool NextResult() => throw new InvalidOperationException("Synchronous next result forbidden");
    }

    private sealed class CancellationActions : IWorkflowActionOperations
    {
        public int BlockAt { get; init; } = -1;
        public AsyncGate Gate { get; } = new();
        public List<string> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public List<int> ProcessNextValues { get; } = [];
        private ActiveAlarm? current;
        private async Task Callback(string name, CancellationToken token, ActiveAlarm? alarm = null)
        {
            token.ThrowIfCancellationRequested();
            current = alarm ?? current;
            Calls.Add(name);
            Tokens.Add(token);
            ProcessNextValues.Add(current!.ProcessNextStep);
            if (Calls.Count - 1 == BlockAt) await Gate.WaitAsync(token);
            token.ThrowIfCancellationRequested();
        }
        public Task SendNotificationsToOfficersInSameGroupAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => Callback("officers", cancellationToken, a);
        public async Task<bool> PushAlertToMcAppAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        { await Callback("mcapp", cancellationToken, a); return true; }
        public async Task<bool> AddToNotificationQueueAsync(ActiveAlarm a, int insertType, CancellationToken cancellationToken = default)
        { await Callback("queue", cancellationToken, a); return true; }
        public Task CreateAlarmAuditAsync(int type, string action, int historyID, int StepNo, CancellationToken cancellationToken = default) => Callback("audit", cancellationToken);
        public Task AddActiveAlarmActionToActivityAsync(int historyID, string email, int type, CancellationToken cancellationToken = default) => Callback("history", cancellationToken);
        public async Task<bool> insertNotificationQueueVictimAsync(ActiveAlarm a, int insertType, string victimsEmails, bool isVictimNotification = false, CancellationToken cancellationToken = default)
        { await Callback("victimQueue", cancellationToken, a); return true; }
        public async Task<string> getInsertEmailsAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        { await Callback("insertEmails", cancellationToken, a); return "insert@example.test"; }
        public async Task<string> getClientEmailAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        { await Callback("clientEmail", cancellationToken, a); return "client@example.test"; }
        public async Task<string> getClientTextAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        { await Callback("clientText", cancellationToken, a); return "123"; }
    }
}