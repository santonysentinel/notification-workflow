using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Parser.Actions;
using Xunit;

namespace NotificationWorkflowService.Tests;

/// <summary>
/// Runtime, offline executor characterization using recording operations and instance-local
/// reference data. Officer expansion and the production helpers' SQL/retry/swallow behavior
/// are outside this boundary; a throwing audit double tests executor propagation, not SQL.
/// </summary>
public class WorkflowActionExecutorTests
{
    private const int InitialProcessNext = 73;
    private const string OfficerMail = "officer@example.test;other@example.test";
    private const string InsertMail = "insert@example.test";
    private const string VictimMail = "victim@example.test;victim@example.test;";
    private const string VictimText = "123;123;";

    // Independently specified expected summaries and operation sequences, not source projections.
    private sealed record Scenario(int Priority, int Role, string Normal, string Step,
        bool NormalInsert, bool StepInsert, bool Stops, string[] Calls);

    private static readonly Scenario[] Scenarios =
    [
        new(1, 0, "Do Nothing", "Do Nothing", false, false, false, []),
        new(2, 0, "Auto Email", "Auto Email", true, true, false, ["officers", "queue3", "emailAudit", "emailHistory"]),
        new(3, 0, "McApp", "McApp", true, true, true, ["officers", "mcapp"]),
        new(4, 0, "Auto Fax", "Auto Fax", true, true, false, ["officers"]),
        new(5, 0, "Auto Page", "Auto Page", true, true, false, ["officers", "queue1", "insertEmails", "pageAudit", "pageHistory"]),
        new(6, 0, "McApp and Auto Fax", "McApp and Auto Fax", true, true, true, ["officers", "mcapp"]),
        new(7, 0, "McApp and Auto Email", "McApp and Auto Email", true, true, true, ["mcapp", "officers", "queue3", "combinedAudit", "emailHistory"]),
        new(9, 0, "McApp and Auto Page", "McApp and Auto Page", true, true, true, ["officers", "mcapp", "queue1", "insertEmails", "pageAudit", "pageHistory"]),
        new(11, 0, "Delay", "Delay", true, true, false, []),
        new(12, 1, "Role Based - Call Supervisor", "Role Based - Call Supervisor", true, true, true, ["mcapp"]),
        new(12, 2, "Role Based - E-mail Supervisor", "Role Based - E-mail Supervisor", true, true, false, ["queue3", "roleAudit", "emailHistory"]),
        new(12, 3, "Role Based - Text Supervisor", "Role Based - Text Supervisor", true, true, false, ["queue3", "roleAudit", "emailHistory"]),
        new(13, 0, "Email-Victims", "", true, true, false, ["victimEmailQueue", "victimEmailAudit", "victimEmailHistory"]),
        new(14, 0, "Contact Victims", "Contact Victim", true, true, true, ["officers", "mcapp"]),
        new(15, 0, "Alert Client - Email", "", true, true, false, ["clientEmail", "clientEmailQueue", "clientEmailAudit", "clientEmailHistory"]),
        new(16, 0, "Alert Client - Text", "", true, true, false, ["clientText", "clientTextQueue", "clientTextAudit", "clientTextHistory"]),
        new(17, 0, "Text All Victims", "Text All Victims", true, true, false, ["victimTextQueue", "victimTextAudit", "victimTextHistory"]),
        new(18, 0, "Add Note", "Add Note", true, true, false, ["note"]),
        new(19, 0, "Do Nothing", "Do Nothing", false, true, false, []),
        new(int.MaxValue, 0, "Do Nothing", "Do Nothing", false, true, false, []),
        new(0, 0, "Do Nothing", "Do Nothing", false, true, false, []),
        new(8, 0, "Do Nothing", "Do Nothing", false, true, false, []),
        new(10, 0, "Do Nothing", "Do Nothing", false, true, false, []),
        new(-1, 0, "Do Nothing", "Do Nothing", false, true, false, [])
    ];

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool step in new[] { false, true })
        foreach (Scenario scenario in Scenarios)
            yield return [step, scenario.Priority, scenario.Role];
    }

    public static IEnumerable<object[]> FailurePositions()
    {
        foreach (object[] row in Cases())
        {
            Scenario scenario = Find((int)row[1], (int)row[2]);
            // Includes every recorded write and read position, even repeated helper categories.
            for (int index = 0; index < scenario.Calls.Length; index++)
                yield return [row[0], row[1], row[2], index];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryPriorityAndRole_ExactArgumentsOrderSummaryInsertAndMutationTimingAsync(bool step, int priority, int role)
    {
        var fixture = new Fixture(step, priority, role);
        Scenario scenario = Find(priority, role);
        WorkflowActionResult result = await fixture.ExecuteAsync();
        AssertResult(scenario, step, result);
        Assert.Equal(Expected(scenario, fixture.Alarm, step), fixture.Operations.Calls);
        Assert.Equal(scenario.Stops ? 0 : InitialProcessNext, fixture.Alarm.ProcessNextStep);
        Assert.Empty(fixture.Logger.Errors);
        // Every recorded operation sees the pre-assignment value, including McApp itself
        // and the audit/history after it in priorities 7 and 9.
        Assert.All(fixture.Operations.Calls, call => Assert.Equal(InitialProcessNext, call.ProcessNext));
        Assert.Equal(31, fixture.Alarm.StateNo);
        Assert.Equal(47, fixture.Alarm.CurrentStateNo);
        Assert.Equal(53, fixture.Alarm.NextStateNo);
        Assert.Equal(OfficerMail, fixture.Alarm.EmailAddresses);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FalseBooleanReturns_AreIgnoredAndOfficerOperationIsNotExpandedAsync(bool step, int priority, int role)
    {
        var fixture = new Fixture(step, priority, role);
        fixture.Operations.BooleanResult = false;
        Scenario scenario = Find(priority, role);
        AssertResult(scenario, step, await fixture.ExecuteAsync());
        Assert.Equal(Expected(scenario, fixture.Alarm, step), fixture.Operations.Calls);
        Assert.Equal(scenario.Stops ? 0 : InitialProcessNext, fixture.Alarm.ProcessNextStep);
        Assert.Empty(fixture.Logger.Errors);
    }

    [Theory]
    [MemberData(nameof(FailurePositions))]
    public async Task FailureAtEveryRecordedPosition_PropagatesOrLocallyClearsAndHasNoUnexpectedLaterEffectsAsync(
        bool step, int priority, int role, int index)
    {
        var fixture = new Fixture(step, priority, role);
        Scenario scenario = Find(priority, role);
        Call[] expected = Expected(scenario, fixture.Alarm, step);
        fixture.Operations.ThrowAt = index;
        bool localCatch = priority is 13 or 15 or 16 or 17 &&
            expected[index].Name is "victimQueue" or "clientEmail" or "clientText";
        if (localCatch)
        {
            AssertResult(scenario, step, await fixture.ExecuteAsync());
            string emptyMessage = priority == 17 ? "Text sent to: " : "Pages sent to: ";
            Assert.Equal(expected.Take(index + 1).Concat(new[]
            {
                Audit(fixture.Alarm, 14, emptyMessage, 1), History(fixture.Alarm, emptyMessage, 1)
            }), fixture.Operations.Calls);
            var error = Assert.Single(fixture.Logger.Errors);
            Assert.Same(fixture.Operations.Failure, error.Exception);
            Assert.Equal(priority switch
            {
                13 => step ? "victimsMail" : "Read Victim Emails",
                15 => "clientMail",
                16 => step ? "clientText" : "getClientText",
                _ => "victimsText"
            }, error.Message);
        }
        else
        {
            Exception thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync());
            Assert.Same(fixture.Operations.Failure, thrown);
            Assert.Equal(expected.Take(index + 1), fixture.Operations.Calls);
            Assert.Empty(fixture.Logger.Errors);
        }
        Assert.Equal(InitialProcessNext, fixture.Alarm.ProcessNextStep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Priority3_McAppThrowOccursBeforeProcessNextAssignmentAsync(bool step)
    {
        var fixture = new Fixture(step, 3);
        fixture.Alarm.ProcessNextStep = 19;
        fixture.Operations.ThrowAt = 1;
        Assert.Same(fixture.Operations.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync()));
        Assert.Equal(new[] { "officers", "mcapp" }, fixture.Operations.Calls.Select(c => c.Name));
        Assert.All(fixture.Operations.Calls, c => Assert.Equal(19, c.ProcessNext));
        Assert.Equal(19, fixture.Alarm.ProcessNextStep);
    }

    public static IEnumerable<object[]> RoleInputs()
    {
        foreach (bool step in new[] { false, true })
        foreach (int role in new[] { 1, 2, 3 })
        foreach (string? input in new string?[] { "", " ", null })
            yield return [step, role, input!];
    }

    [Theory]
    [MemberData(nameof(RoleInputs))]
    public async Task RoleGuards_AreLiteralEmptyChecksNotWhitespaceOrNullChecksAsync(bool step, int role, string? input)
    {
        var fixture = new Fixture(step, 12, role);
        if (role == 1) fixture.Alarm.Instruction = input!;
        else fixture.Alarm.EmailAddresses = input!;
        Scenario scenario = Find(12, role);
        AssertResult(scenario, step, await fixture.ExecuteAsync());
        Call[] expected = input == "" ? [] : role == 1 ? [Operation("mcapp", fixture.Alarm)] :
        [
            Queue(fixture.Alarm, 3), Audit(fixture.Alarm, 14, "Pages sent to: " + input, step ? 47 : 1),
            History(fixture.Alarm, input, 1)
        ];
        Assert.Equal(expected, fixture.Operations.Calls);
        Assert.Equal(role == 1 && input != "" ? 0 : InitialProcessNext, fixture.Alarm.ProcessNextStep);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingRoleLookup_ThrowsBeforeAnyOperationAsync(bool step, bool missingRoleAction)
    {
        var fixture = new Fixture(step, 12, 1);
        if (missingRoleAction) fixture.RoleActions.Clear();
        else fixture.Roles.Clear();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.ExecuteAsync());
        Assert.Empty(fixture.Operations.Calls);
        Assert.Equal(InitialProcessNext, fixture.Alarm.ProcessNextStep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownRoleAction_WithLookupEntryHasSummaryButNoOperationsAsync(bool step)
    {
        var fixture = new Fixture(step, 12, 4);
        fixture.RoleActions[4] = "Unknown";
        Assert.Equal(new WorkflowActionResult(true, "Role Based - Unknown Supervisor"), await fixture.ExecuteAsync());
        Assert.Empty(fixture.Operations.Calls);
        Assert.Equal(InitialProcessNext, fixture.Alarm.ProcessNextStep);
    }

    public static IEnumerable<object[]> RecipientInputs()
    {
        foreach (bool step in new[] { false, true })
        foreach (int priority in new[] { 13, 15, 16, 17 })
        foreach (string variant in new[] { "empty", "whitespace", "null", "missing" })
            yield return [step, priority, variant];
    }

    [Theory]
    [MemberData(nameof(RecipientInputs))]
    public async Task RecipientEdges_EmptyClientsDoNotQueueButVictimsDoAndAllStillAuditAsync(bool step, int priority, string variant)
    {
        var fixture = new Fixture(step, priority);
        bool victims = priority is 13 or 17;
        bool error = victims ? variant is "null" or "missing" : variant == "null";
        string recipients = "";
        if (victims)
        {
            if (variant == "missing") fixture.Victims.Clear();
            else if (variant == "null") fixture.Victims[fixture.Alarm.ClientID] = null!;
            else if (variant == "empty") fixture.Victims[fixture.Alarm.ClientID] = [];
            else
            {
                fixture.Victims[fixture.Alarm.ClientID] = [new Victim { Email = " ", CellPhone = " " }];
                recipients = " ;";
            }
        }
        else
        {
            string? input = variant switch { "null" => null, "whitespace" => " \t\r\n ", _ => "" };
            if (priority == 15) fixture.Operations.ClientEmail = input!;
            else fixture.Operations.ClientText = input!;
        }
        AssertResult(Find(priority, 0), step, await fixture.ExecuteAsync());
        string message = (priority == 17 ? "Text sent to: " : "Pages sent to: ") + recipients;
        var expected = new List<Call>();
        if (victims && !error) expected.Add(VictimQueue(fixture.Alarm, priority == 13 ? 3 : 4, recipients, true));
        if (!victims) expected.Add(Operation(priority == 15 ? "clientEmail" : "clientText", fixture.Alarm));
        expected.Add(Audit(fixture.Alarm, 14, message, 1));
        expected.Add(History(fixture.Alarm, message, 1));
        Assert.Equal(expected, fixture.Operations.Calls);
        Assert.Equal(error ? 1 : 0, fixture.Logger.Errors.Count);
        Assert.Equal(InitialProcessNext, fixture.Alarm.ProcessNextStep);
    }

    [Theory]
    [InlineData(false, 13)]
    [InlineData(true, 13)]
    [InlineData(false, 17)]
    [InlineData(true, 17)]
    public async Task NullVictimElement_DiscardsPartialJoinAndDoesNotQueueAsync(bool step, int priority)
    {
        var fixture = new Fixture(step, priority);
        fixture.Victims[fixture.Alarm.ClientID].Insert(1, null!);
        AssertResult(Find(priority, 0), step, await fixture.ExecuteAsync());
        string message = priority == 13 ? "Pages sent to: " : "Text sent to: ";
        Assert.Equal(new[] { Audit(fixture.Alarm, 14, message, 1), History(fixture.Alarm, message, 1) }, fixture.Operations.Calls);
        Assert.IsType<NullReferenceException>(Assert.Single(fixture.Logger.Errors).Exception);
    }

    public static IEnumerable<object[]> TruncationCases()
    {
        foreach (bool step in new[] { false, true })
        foreach (int priority in new[] { 2, 5, 7, 9, 12, 13, 15, 16 })
        foreach (int length in new[] { 4081, 4082, 4083, 5000 })
            yield return [step, priority, length];
    }

    [Theory]
    [MemberData(nameof(TruncationCases))]
    public async Task PageMessages_TruncateOnlyAuditAndApplicableHistoryNotQueueRecipientsAsync(bool step, int priority, int length)
    {
        int role = priority == 12 ? 3 : 0;
        var fixture = new Fixture(step, priority, role);
        string recipients = new('x', length);
        fixture.Alarm.EmailAddresses = recipients;
        fixture.Operations.InsertEmails = recipients;
        fixture.Operations.ClientEmail = " " + recipients + " ";
        fixture.Operations.ClientText = " " + recipients + " ";
        fixture.Victims[fixture.Alarm.ClientID] = [new Victim { Email = recipients, CellPhone = "" }];
        AssertResult(Find(priority, role), step, await fixture.ExecuteAsync());
        string joined = priority == 13 ? recipients + ";" : recipients;
        string full = "Pages sent to: " + joined;
        string message = full[..Math.Min(4096, full.Length)];
        Call audit = Assert.Single(fixture.Operations.Calls, c => c.Name == "audit");
        Assert.Equal(Audit(fixture.Alarm, priority switch
        {
            2 => 1, 7 => step ? 3 : 1, 5 or 9 => 3, _ => 14
        }, message, priority >= 13 ? 1 : step ? 47 : 1), audit);
        Assert.Equal(History(fixture.Alarm, priority is 2 or 7 or 12 ? recipients : message,
            priority is 5 or 9 ? 0 : 1), Assert.Single(fixture.Operations.Calls, c => c.Name == "history"));
        if (priority >= 13)
            Assert.Equal(VictimQueue(fixture.Alarm, priority == 16 ? 4 : 3, joined, priority == 13),
                Assert.Single(fixture.Operations.Calls, c => c.Name == "victimQueue"));
        Assert.Empty(fixture.Logger.Errors);
    }

    public static IEnumerable<object[]> VictimTextLengths()
    {
        foreach (bool step in new[] { false, true })
        // Joined length includes the semicolon. The normal-mode substring uses the
        // recipient string, despite the condition testing the prefixed message length.
        foreach (int joinedLength in new[] { 4082, 4083, 4084, 4095, 4096, 4097, 5000 })
            yield return [step, joinedLength];
    }

    [Theory]
    [MemberData(nameof(VictimTextLengths))]
    public async Task VictimText_NormalRetainsUnusedSubstringBugWhileStepTruncatesAsync(bool step, int joinedLength)
    {
        var fixture = new Fixture(step, 17);
        string recipients = new string('9', joinedLength - 1) + ";";
        fixture.Victims[fixture.Alarm.ClientID] = [new Victim { Email = "", CellPhone = recipients[..^1] }];
        string full = "Text sent to: " + recipients;
        if (!step && joinedLength is > 4082 and < 4096)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.ExecuteAsync());
            Assert.Equal(new[] { VictimQueue(fixture.Alarm, 4, recipients, true) }, fixture.Operations.Calls);
            // The buggy substring is outside the local catch; no audit/history follows.
            Assert.Empty(fixture.Logger.Errors);
        }
        else
        {
            AssertResult(Find(17, 0), step, await fixture.ExecuteAsync());
            string message = step && full.Length > 4096 ? full[..4096] : full;
            Assert.Equal(new[] { VictimQueue(fixture.Alarm, 4, recipients, true),
                Audit(fixture.Alarm, 14, message, 1), History(fixture.Alarm, message, 1) }, fixture.Operations.Calls);
        }
        Assert.Equal(InitialProcessNext, fixture.Alarm.ProcessNextStep);
    }

    private static Scenario Find(int priority, int role) => Assert.Single(Scenarios, s => s.Priority == priority && s.Role == role);

    private static void AssertResult(Scenario scenario, bool step, WorkflowActionResult result) =>
        Assert.Equal(new WorkflowActionResult(step ? scenario.StepInsert : scenario.NormalInsert,
            step ? scenario.Step : scenario.Normal), result);

    private sealed record Call(string Name, ActiveAlarm? Alarm = null, int? Type = null,
        string? Text = null, int? HistoryID = null, int? StepNo = null, bool? IsVictim = null,
        int ProcessNext = InitialProcessNext, string? Oid = null);

    private static Call Operation(string name, ActiveAlarm a) => new(name, a);
    private static Call Queue(ActiveAlarm a, int type) => new("queue", a, type);
    private static Call VictimQueue(ActiveAlarm a, int type, string recipients, bool victim) => new("victimQueue", a, type, recipients, IsVictim: victim);
    private static Call Audit(ActiveAlarm a, int type, string text, int step) => new("audit", Type: type, Text: text, HistoryID: a.HistoryID, StepNo: step);
    private static Call History(ActiveAlarm a, string? text, int type) => new("history", Type: type, Text: text, HistoryID: a.HistoryID);

    private static Call[] Expected(Scenario scenario, ActiveAlarm a, bool step) => scenario.Calls.Select(token => token switch
    {
        "note" => new Call("note", Text: a.Instruction, Oid: a.ClientID),
        "officers" => Operation("officers", a),
        "mcapp" => Operation("mcapp", a),
        "queue3" => Queue(a, 3),
        "queue1" => Queue(a, 1),
        "insertEmails" => Operation("insertEmails", a),
        "clientEmail" => Operation("clientEmail", a),
        "clientText" => Operation("clientText", a),
        "emailAudit" => Audit(a, 1, "Pages sent to: " + OfficerMail, step ? 47 : 1),
        "combinedAudit" => Audit(a, step ? 3 : 1, "Pages sent to: " + OfficerMail, step ? 47 : 1),
        "roleAudit" => Audit(a, 14, "Pages sent to: " + OfficerMail, step ? 47 : 1),
        "emailHistory" => History(a, OfficerMail, 1),
        "pageAudit" => Audit(a, 3, "Pages sent to: " + InsertMail, step ? 47 : 1),
        "pageHistory" => History(a, "Pages sent to: " + InsertMail, 0),
        "victimEmailQueue" => VictimQueue(a, 3, VictimMail, true),
        "victimEmailAudit" => Audit(a, 14, "Pages sent to: " + VictimMail, 1),
        "victimEmailHistory" => History(a, "Pages sent to: " + VictimMail, 1),
        "victimTextQueue" => VictimQueue(a, 4, VictimText, true),
        "victimTextAudit" => Audit(a, 14, "Text sent to: " + VictimText, 1),
        "victimTextHistory" => History(a, "Text sent to: " + VictimText, 1),
        "clientEmailQueue" => VictimQueue(a, 3, "client@example.test", false),
        "clientEmailAudit" => Audit(a, 14, "Pages sent to: client@example.test", 1),
        "clientEmailHistory" => History(a, "Pages sent to: client@example.test", 1),
        "clientTextQueue" => VictimQueue(a, 4, "555", false),
        "clientTextAudit" => Audit(a, 14, "Pages sent to: 555", 1),
        "clientTextHistory" => History(a, "Pages sent to: 555", 1),
        _ => throw new InvalidDataException("Unknown expectation token: " + token)
    }).ToArray();

    private sealed class Fixture
    {
        public ActiveAlarm Alarm { get; }
        public RecordingOperations Operations { get; }
        public RecordingLogger Logger { get; } = new();
        public Dictionary<int, string> RoleActions { get; } = new() { [1] = "Call", [2] = "E-mail", [3] = "Text" };
        public Dictionary<string, string> Roles { get; } = new() { ["61"] = "Supervisor" };
        public Dictionary<string, List<Victim>> Victims { get; }
        private readonly WorkflowActionContext context;

        public Fixture(bool step, int priority, int role = 0)
        {
            Alarm = new ActiveAlarm
            {
                SystemID = 101, HistoryID = 211, ClientID = "client", AlarmID = "alarm", POGroupNum = "group",
                StateNo = 31, CurrentStateNo = 47, NextStateNo = 53, ProcessNextStep = InitialProcessNext,
                Priority = priority, RoleAction = role, RoleID = 61, Instruction = "Call officers",
                EmailAddresses = OfficerMail, EmailJoin = 1
            };
            Operations = new RecordingOperations(Alarm);
            Victims = new()
            {
                ["client"] =
                [
                    new Victim { Email = "victim@example.test", CellPhone = "123" },
                    new Victim { Email = "", CellPhone = "" },
                    new Victim { Email = null!, CellPhone = null! },
                    new Victim { Email = "victim@example.test", CellPhone = "123" }
                ]
            };
            context = new WorkflowActionContext(step ? WorkflowActionMode.Step : WorkflowActionMode.Normal,
                "offline", Logger, Operations, RoleActions, Roles, Victims);
        }

        public Task<WorkflowActionResult> ExecuteAsync() => WorkflowActionExecutor.ExecuteAsync(Alarm, context);
    }

    private sealed class RecordingOperations(ActiveAlarm alarm) : IWorkflowActionOperations
    {
        public List<Call> Calls { get; } = [];
        public bool BooleanResult { get; set; } = true;
        public int ThrowAt { get; set; } = -1;
        public InvalidOperationException Failure { get; } = new("Injected operation failure");
        public string InsertEmails { get; set; } = InsertMail;
        public string ClientEmail { get; set; } = "  client@example.test \t";
        public string ClientText { get; set; } = " 555 \r\n";

        private void Record(Call call)
        {
            Calls.Add(call with { ProcessNext = alarm.ProcessNextStep });
            if (Calls.Count - 1 == ThrowAt) throw Failure;
        }

        public Task SendNotificationsToOfficersInSameGroupAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        { Record(Operation("officers", a)); return Task.CompletedTask; }
        public Task AddNoteAsync(string template, string oid, CancellationToken cancellationToken = default)
        { Record(new Call("note", Text: template, Oid: oid)); return Task.CompletedTask; }
        public Task<bool> PushAlertToMcAppAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        { Record(Operation("mcapp", a)); return Task.FromResult(BooleanResult); }
        public Task<bool> AddToNotificationQueueAsync(ActiveAlarm a, int insertType, CancellationToken cancellationToken = default)
        { Record(Queue(a, insertType)); return Task.FromResult(BooleanResult); }
        public Task CreateAlarmAuditAsync(int type, string action, int historyID, int StepNo, CancellationToken cancellationToken = default)
        { Record(new Call("audit", Type: type, Text: action, HistoryID: historyID, StepNo: StepNo)); return Task.CompletedTask; }
        public Task AddActiveAlarmActionToActivityAsync(int historyID, string email, int type, CancellationToken cancellationToken = default)
        { Record(new Call("history", Type: type, Text: email, HistoryID: historyID)); return Task.CompletedTask; }
        public Task<bool> insertNotificationQueueVictimAsync(ActiveAlarm a, int insertType, string victimsEmails, bool isVictimNotification = false, CancellationToken cancellationToken = default)
        { Record(VictimQueue(a, insertType, victimsEmails, isVictimNotification)); return Task.FromResult(BooleanResult); }
        public Task<string> getInsertEmailsAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        { Record(Operation("insertEmails", a)); return Task.FromResult(InsertEmails); }
        public Task<string> getClientEmailAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        { Record(Operation("clientEmail", a)); return Task.FromResult(ClientEmail); }
        public Task<string> getClientTextAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        { Record(Operation("clientText", a)); return Task.FromResult(ClientText); }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(Exception? Exception, string Message)> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errors.Add((exception, formatter(state, exception)));
        }
    }
}