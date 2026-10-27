using System.Data;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using ActiveAlarmsParser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.AutoCall;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class AutoCallPreparationTests
{
    private static readonly Guid Token = Guid.NewGuid();
    private static AutoCallJob Job(int id = 1) => new() { SystemID = id, OID = "oid", FlowId = 12, LeaseToken = Token };
    private static AutoCallAttempt Attempt(bool prepared = false, string? variables = null, string? xml = null, string? graph = null) => new()
    {
        CallId = 42, QueueId = 1, QueueLeaseToken = Token, OID = "oid", PhoneE164 = "+15551234567", FlowId = 12,
        VariablesJson = variables ?? "{\"OffenderName\":\"Alex & <Co>\"}",
        TemplateJson = graph ?? "{\"schemaVersion\":1,\"initialStep\":\"hello\",\"steps\":[{\"id\":\"hello\",\"type\":\"gather\",\"transitions\":{\"noInput\":\"message\",\"fallback\":\"end\"}}," +
            "{\"id\":\"message\",\"type\":\"record\",\"transitions\":{\"recorded\":\"end\",\"noRecording\":\"end\"}},{\"id\":\"end\",\"type\":\"terminal\"}]}",
        TemplateTwiML = xml ?? "<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say>{{OffenderName}} {{recipient}} {{callId}}</Say><Gather action=\"{{feedbackBaseUrl}}/old\"/><Redirect>old</Redirect></Response></Step>" +
            "<Step id=\"message\"><Response><Pause length=\"1\"/><Record action=\"old\"/><Redirect>old</Redirect></Response></Step><Step id=\"end\"><Response><Hangup/></Response></Step></CallFlow>",
        TwiML = prepared ? "original-xml" : null, Parameters = prepared ? "original-parameters" : null
    };
    private static AutoCallWorkerOptions Options(int capacity = 2) => new() { MaxConcurrency = capacity, WorkerId = "worker", FeedbackBaseUrl = "https://feedback.example/prefix/" };

    private sealed class Repository : IAutoCallRepository
    {
        public Queue<AutoCallJob> Jobs { get; } = new();
        public List<int> Capacities { get; } = [];
        public AutoCallAttempt Result { get; set; } = Attempt();
        public List<string> Calls { get; } = [];
        public AutoCallPreparedBundle? Saved { get; private set; }
        public string? Recipient { get; private set; }
        public AutoCallQueueStatus QueueStatus { get; set; } = new();
        public Exception? StatusFailure { get; set; }
        public int StatusReads { get; private set; }
        public TaskCompletionSource StatusRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AutoCallQueueStatus> ReadQueueStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            StatusReads++;
            StatusRead.TrySetResult();
            return StatusFailure is null ? Task.FromResult(QueueStatus) : Task.FromException<AutoCallQueueStatus>(StatusFailure);
        }
        public Task<IReadOnlyList<AutoCallJob>> ClaimAsync(int capacity, string workerId, int leaseMinutes, CancellationToken token)
        {
            Capacities.Add(capacity);
            var jobs = new List<AutoCallJob>();
            while (jobs.Count < capacity && Jobs.TryDequeue(out var job)) jobs.Add(job);
            return Task.FromResult<IReadOnlyList<AutoCallJob>>(jobs);
        }
        public Task<AutoCallAttempt> BeginAttemptAsync(AutoCallJob job, string workerId, string recipient, CancellationToken token)
        { Calls.Add("begin"); Recipient = recipient; return Task.FromResult(Result); }
        public Task<int> SavePreparationAsync(AutoCallAttempt attempt, string workerId, AutoCallPreparedBundle bundle, CancellationToken token)
        { Calls.Add("save"); Saved = bundle; return Task.FromResult(attempt.CallId); }
        public Task FailPreparationAsync(AutoCallJob job, string workerId, CancellationToken token)
        { Calls.Add("fail"); return Task.CompletedTask; }
        public Task SkipDeviceAsync(AutoCallJob job, string workerId, CancellationToken token)
        { Calls.Add("skip"); return Task.CompletedTask; }
    }
    private sealed class Routing : IAutoCallRoutingStore
    {
        public Task<string?> GetAsync(string key, CancellationToken token) => Task.FromResult<string?>("TENANT");
    }
    private sealed class Phones(IReadOnlyList<AutoCallPhone> phones) : IAutoCallPhoneRepository
    {
        public Task<IReadOnlyList<AutoCallPhone>> ReadAsync(string connection, string oid, CancellationToken token) => Task.FromResult(phones);
    }
    private static AutoCallContactService Contacts(params string[] numbers) => TypedContacts(numbers.Select(number => new AutoCallPhone("CellPhone", number)).ToArray());
    private static AutoCallContactService TypedContacts(params AutoCallPhone[] phones) => new(new Routing(), new Phones(phones),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TENANT"] = "unused" }).Build());

    [Theory]
    [InlineData("CellPhone", 0)]
    [InlineData("OfficePhone", 0)]
    [InlineData("OfficePhone", 1)]
    public async Task OfficerPreparationUsesPinnedIdentityAndSkipsMissingPhone(string device, int index)
    {
        foreach (bool missing in new[] { false, true })
        {
            var officers = new OfficerRoboCallTests.Officers
            {
                Phones = missing ? [] : [new AutoCallPhone(device, "+15551234567")]
            };
            var contacts = new AutoCallContactService(new Routing(), new Phones([new("CellPhone", "+19999999999")]),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TENANT"] = "routed" }).Build(),
                officerRepository: officers);
            string json = JsonSerializer.Serialize(new
            {
                officer = JsonSerializer.SerializeToElement(new RoboCallOfficer(new("Primary", null, null, null), 91, "Officer"), RoboCallOfficer.QueueJson),
                callDevices = Enumerable.Range(0, index + 1).Select(_ => new { type = device, phoneNumber = (string?)null }).ToArray()
            });
            var job = new AutoCallJob { SystemID = 1, OID = "oid", FlowId = 12, LeaseToken = Token, OptionsJSON = json, CallDeviceIndex = index };
            var repository = new Repository();
            var service = new AutoCallProcessingService(repository, contacts, Options());
            if (missing)
            {
                await Assert.ThrowsAsync<AutoCallDeviceSkippedException>(() => service.PrepareAsync(job, "worker", default));
                Assert.Equal(["skip"], repository.Calls);
            }
            else
            {
                await service.PrepareAsync(job, "worker", default);
                Assert.Equal("+15551234567", repository.Recipient);
            }
            Assert.Equal(("routed", 91), Assert.Single(officers.PhoneCalls));
            Assert.Equal(0, officers.Resolutions);
        }
    }

    [Fact]
    public async Task CreatesIdentityThenPersonalizesAndPersistsBeforeReturning()
    {
        var repository = new Repository();
        var result = await new AutoCallProcessingService(repository, Contacts("bad", "+15551234567", "+15557654321"), Options()).PrepareAsync(Job(), "worker", default);
        Assert.Equal(new[] { "begin", "save" }, repository.Calls);
        Assert.Equal("+15551234567", repository.Recipient);
        Assert.True(result.IsPrepared);
        var xml = XDocument.Parse(result.TwiML!);
        Assert.Equal("Alex & <Co> +15551234567 42", xml.Descendants("Say").Single().Value);
        string next = "https://feedback.example/prefix/webhooks/twilio/voice/next?callId=42&executionId={{executionId}}";
        Assert.Equal(next, (string?)xml.Descendants("Gather").Single().Attribute("action"));
        Assert.All(xml.Descendants("Redirect"), redirect => Assert.Equal(next, redirect.Value));
        var record = xml.Descendants("Record").Single();
        Assert.Equal(next, (string?)record.Attribute("action"));
        Assert.Equal("https://feedback.example/prefix/webhooks/twilio/recordings/status?callId=42", (string?)record.Attribute("recordingStatusCallback"));
        Assert.Equal("completed", (string?)record.Attribute("recordingStatusCallbackEvent"));
        Assert.Equal(("120", "5", "#", "true", "trim-silence"), ((string?)record.Attribute("maxLength"), (string?)record.Attribute("timeout"),
            (string?)record.Attribute("finishOnKey"), (string?)record.Attribute("playBeep"), (string?)record.Attribute("trim")));
        using var parameters = JsonDocument.Parse(result.Parameters!);
        Assert.Equal(42, parameters.RootElement.GetProperty("callId").GetInt32());
        Assert.Equal("Alex & <Co>", parameters.RootElement.GetProperty("OffenderName").GetString());
        Assert.False(parameters.RootElement.TryGetProperty("executionId", out _));
    }

    [Theory]
    [InlineData("5551234567")]
    [InlineData("15551234567")]
    [InlineData("+1 (555) 123-4567")]
    [InlineData(" +15551234567")]
    public async Task NormalizedRecipientReachesTheAttemptAndPreparedBundle(string phone)
    {
        var repository = new Repository();
        var result = await new AutoCallProcessingService(repository, Contacts("invalid", phone, "+15557654321"), Options())
            .PrepareAsync(Job(), "worker", default);
        Assert.Equal("+15551234567", repository.Recipient);
        using var parameters = JsonDocument.Parse(result.Parameters!);
        Assert.Equal("+15551234567", parameters.RootElement.GetProperty("recipient").GetString());
    }

    [Fact]
    public async Task UnrecognizableNumbersFailExplicitlyBeforeCreatingAnAttempt()
    {
        var repository = new Repository();
        var failure = await Assert.ThrowsAsync<AutoCallValidationException>(() =>
            new AutoCallProcessingService(repository, Contacts("5551234", "5551234567x123"), Options())
                .PrepareAsync(Job(), "worker", default));
        Assert.Equal("No E.164 recipient is available for this OID.", failure.Message);
        Assert.Empty(repository.Calls);
    }

    private static AutoCallJob DeviceJob(int index, string devices) => new()
    {
        SystemID = 1, OID = "oid", FlowId = 12, LeaseToken = Token, CallDeviceIndex = index,
        OptionsJSON = "{\"historyId\":7,\"postRecordingTranscript\":true,\"callDevices\":" + devices + "}"
    };

    [Theory]
    [InlineData(0, "+15551234567")]
    [InlineData(1, "+15557654321")]
    public async Task DialsTheNumberOfTheClaimedCallDevice(int index, string expected)
    {
        var repository = new Repository();
        var contacts = TypedContacts(new("HomePhone", "+15557654321"), new("CellPhone", "5551234567"), new("WorkPhone", "+15550000000"));
        await new AutoCallProcessingService(repository, contacts, Options())
            .PrepareAsync(DeviceJob(index, "[{\"type\":\"CellPhone\",\"phoneNumber\":null},{\"type\":\"homephone\",\"phoneNumber\":null}]"), "worker", default);
        Assert.Equal(expected, repository.Recipient);
    }

    [Fact]
    public async Task CustomDeviceDialsItsOwnNumber()
    {
        var repository = new Repository();
        await new AutoCallProcessingService(repository, Contacts("+15551234567"), Options())
            .PrepareAsync(DeviceJob(1, "[{\"type\":\"CellPhone\",\"phoneNumber\":null},{\"type\":\"Custom\",\"phoneNumber\":\"+19499299458\"}]"), "worker", default);
        Assert.Equal("+19499299458", repository.Recipient);
    }

    [Fact]
    public async Task DeviceWithoutANumberIsSkippedWithoutCreatingAnAttempt()
    {
        var repository = new Repository();
        var skipped = await Assert.ThrowsAsync<AutoCallDeviceSkippedException>(() =>
            new AutoCallProcessingService(repository, TypedContacts(new AutoCallPhone("HomePhone", "+15557654321")), Options())
                .PrepareAsync(DeviceJob(0, "[{\"type\":\"WorkPhone\",\"phoneNumber\":null}]"), "worker", default));
        Assert.Equal("WorkPhone", skipped.PhoneType);
        Assert.Equal(new[] { "skip" }, repository.Calls);
    }

    [Fact]
    public async Task ClaimedDeviceOutsideTheQueuedOptionsFailsValidation()
    {
        var repository = new Repository();
        await Assert.ThrowsAsync<AutoCallValidationException>(() =>
            new AutoCallProcessingService(repository, Contacts("+15551234567"), Options())
                .PrepareAsync(DeviceJob(2, "[{\"type\":\"CellPhone\",\"phoneNumber\":null}]"), "worker", default));
        Assert.Empty(repository.Calls);
    }

    [Fact]
    public async Task PreparedReplayReturnsOriginalWithoutOverwritingItsRecipientOrBundle()
    {
        var repository = new Repository { Result = Attempt(true) };
        var result = await new AutoCallProcessingService(repository, Contacts("+15557654321"), Options()).PrepareAsync(Job(), "worker", default);
        Assert.Same(repository.Result, result);
        Assert.Equal("+15551234567", result.PhoneE164);
        Assert.Equal(new[] { "begin" }, repository.Calls);
    }

    [Fact]
    public void QueuedParticipantSnapshotResolvesCaseInsensitiveNamesAndIgnoresUnusedNulls()
    {
        var details = ParticipantCurrentDetailsTests.Values();
        details["last_location_time"] = JsonSerializer.SerializeToElement<object?>(null);
        var alarm = new NotificationWorkflowService.Entity.ActiveAlarm { SystemID = 42, HistoryID = 81, ClientID = "oid", POGroupNum = "group", OptionsJson = AutoCallEnqueueTests.RoboCallOptions() };
        var template = Attempt(graph: "{\"schemaVersion\":1,\"initialStep\":\"hello\",\"steps\":[{\"id\":\"hello\",\"type\":\"gather\",\"transitions\":{\"noInput\":\"end\",\"fallback\":\"end\"}},{\"id\":\"end\",\"type\":\"terminal\"}]}", xml:
            "<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say>Hello, {{OffenderName}}. {{alarmname}} at {{LAT}},{{lon}}</Say></Response></Step><Step id=\"end\"><Response><Hangup/></Response></Step></CallFlow>");
        var request = AutoCallEnqueueService.CaptureRequest(alarm, "DEV1",
            new AutoCallFlow(12, template.TemplateJson, template.TemplateTwiML), details);
        using var command = AutoCallQueueRepository.BuildEnqueueCommand(request);
        var attempt = Attempt(variables: (string)command.Parameters["@VariablesJSON"].Value, xml: template.TemplateTwiML, graph: template.TemplateJson);

        var bundle = AutoCallBundlePreparer.Prepare(attempt, Options().FeedbackBaseUrl);

        Assert.Equal("Hello, Alex & Co. Low battery at 12.50,-45.125",
            XDocument.Parse(bundle.TwiML).Descendants("Say").Single().Value);
        using var parameters = JsonDocument.Parse(bundle.Parameters);
        Assert.Equal(JsonValueKind.Null, parameters.RootElement.GetProperty("last_location_time").ValueKind);
        Assert.Equal("Alex & Co", parameters.RootElement.GetProperty("offenderName").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"offenderName\":null}")]
    [InlineData("{\"offenderName\":\" \"}")]
    public async Task RequiredMissingParticipantValuesFailAtPreparationWithoutSaving(string variables)
    {
        var repository = new Repository { Result = Attempt(variables: variables) };
        var failure = await Assert.ThrowsAsync<AutoCallValidationException>(() =>
            new AutoCallProcessingService(repository, Contacts("+15551234567"), Options()).PrepareAsync(Job(), "worker", default));
        Assert.Contains("OffenderName", failure.Message);
        Assert.Equal(new[] { "begin" }, repository.Calls);
        Assert.Null(repository.Saved);
    }

    [Fact]
    public void RuntimeValuesOverrideCaseInsensitiveSnapshotKeysWithCanonicalNames()
    {
        var bundle = AutoCallBundlePreparer.Prepare(Attempt(variables:
            "{\"offenderName\":\"Alex\",\"CALLID\":99,\"RECIPIENT\":\"invalid\",\"FEEDBACKBASEURL\":\"https://invalid.example\",\"EXECUTIONID\":\"untrusted\"}"),
            Options().FeedbackBaseUrl);
        using var parameters = JsonDocument.Parse(bundle.Parameters);
        Assert.Equal(42, parameters.RootElement.GetProperty("callId").GetInt32());
        Assert.Equal("+15551234567", parameters.RootElement.GetProperty("recipient").GetString());
        Assert.Equal("https://feedback.example/prefix", parameters.RootElement.GetProperty("feedbackBaseUrl").GetString());
        Assert.False(parameters.RootElement.TryGetProperty("CALLID", out _));
        Assert.False(parameters.RootElement.TryGetProperty("EXECUTIONID", out _));
    }

    [Fact]
    public void CaseInsensitiveDuplicateVariablesAreRejected()
        => Assert.Throws<AutoCallValidationException>(() => AutoCallBundlePreparer.Prepare(
            Attempt(variables: "{\"offenderName\":\"Alex\",\"OffenderName\":\"conflict\"}"), Options().FeedbackBaseUrl));

    [Fact]
    public async Task InvalidRecipientsAndCancellationNeverAllocateAnAttempt()
    {
        var repository = new Repository();
        var service = new AutoCallProcessingService(repository, Contacts("5551234", "+0123", "+15551234567x123"), Options());
        await Assert.ThrowsAsync<AutoCallValidationException>(() => service.PrepareAsync(Job(), "worker", default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(Job(), "worker", new CancellationToken(true)));
        Assert.Empty(repository.Calls);
    }

    [Theory]
    [InlineData("http://feedback.example")]
    [InlineData("https://user:password@feedback.example")]
    [InlineData("https://feedback.example?x=1")]
    [InlineData("https://feedback.example#fragment")]
    public void UnsafeCallbackConfigurationFailsBeforePersonalization(string baseUrl)
        => Assert.Throws<InvalidOperationException>(() => AutoCallBundlePreparer.Prepare(Attempt(), baseUrl));

    [Fact]
    public void DapperCommandsCarryClaimIdentityAndUnboundedSnapshots()
    {
        using var token = new CancellationTokenSource();
        var claim = AutoCallRepository.ClaimCommand(2, "worker", 5, token.Token);
        Assert.Equal(2, ((DynamicParameters)claim.Parameters!).Get<int>("@BatchSize"));
        Assert.Equal(CommandType.StoredProcedure, claim.CommandType);
        Assert.Equal(token.Token, claim.CancellationToken);
        var begin = AutoCallRepository.BeginCommand(Job(), "worker", "+15551234567", token.Token);
        Assert.Equal(Token, ((DynamicParameters)begin.Parameters!).Get<Guid>("@LeaseToken"));
        var bundle = AutoCallBundlePreparer.Prepare(Attempt(), Options().FeedbackBaseUrl);
        var save = AutoCallRepository.SaveCommand(Attempt(), "worker", bundle, token.Token);
        Assert.Equal(bundle.TwiML, ((DynamicParameters)save.Parameters!).Get<string>("@TwiML"));
        Assert.Equal(bundle.Parameters, ((DynamicParameters)save.Parameters!).Get<string>("@Parameters"));
    }

    private sealed class Processing : IAutoCallProcessingService
    {
        public List<TaskCompletionSource<AutoCallAttempt>> Pending { get; } = [];
        public Task<AutoCallAttempt> PrepareAsync(AutoCallJob job, string workerId, CancellationToken token)
        {
            var pending = new TaskCompletionSource<AutoCallAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(pending);
            return pending.Task.WaitAsync(token);
        }
    }

    [Fact]
    public async Task WorkerLogsQueueCountsOnEveryPassIncludingWhenAtCapacity()
    {
        var logger = new AutoCallDiagnosticsTests.Logs<AutoCallWorker>();
        var repository = new Repository
        {
            QueueStatus = new() { WaitingJobs = 7, DueJobs = 3, ScheduledJobs = 4, ProcessingJobs = 2, ExpiredLeaseJobs = 1 }
        };
        repository.Jobs.Enqueue(Job());
        repository.Jobs.Enqueue(Job(2));
        var processing = new Processing();
        using var worker = new AutoCallWorker(repository, processing, Options(), logger);

        await worker.RunCycleAsync(default);
        await worker.RunCycleAsync(default);

        Assert.Equal(2, repository.StatusReads);
        Assert.Equal(new[] { 2 }, repository.Capacities);
        var passes = logger.Entries.Where(entry => entry.Properties.ContainsKey("PassNumber")).ToArray();
        Assert.Equal(2, passes.Length);
        Assert.Equal(1L, passes[0].Properties["PassNumber"]);
        Assert.Equal(2L, passes[1].Properties["PassNumber"]);
        Assert.Equal(2, passes[0].Properties["ClaimedJobs"]);
        Assert.Equal(0, passes[1].Properties["ClaimedJobs"]);
        Assert.All(passes, entry =>
        {
            Assert.Equal(true, entry.Properties["QueueStatusAvailable"]);
            Assert.Equal(7L, entry.Properties["WaitingJobs"]);
            Assert.Equal(3L, entry.Properties["DueJobs"]);
            Assert.Equal(4L, entry.Properties["ScheduledJobs"]);
            Assert.Equal(2L, entry.Properties["ProcessingJobs"]);
            Assert.Equal(1L, entry.Properties["ExpiredLeaseJobs"]);
            Assert.Equal(2, entry.Properties["InFlightJobs"]);
            Assert.Equal(0, entry.Properties["AvailableSlots"]);
        });
        foreach (var pending in processing.Pending) pending.SetResult(Attempt(true));
        await worker.WaitForCompletionAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IdleWorkerStillLogsAPassWithZeroCounts()
    {
        var logger = new AutoCallDiagnosticsTests.Logs<AutoCallWorker>();
        using var worker = new AutoCallWorker(new Repository(), new Processing(), Options(), logger);
        Assert.Equal(0, await worker.RunCycleAsync(default));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(0L, entry.Properties["WaitingJobs"]);
        Assert.Equal(0, entry.Properties["ClaimedJobs"]);
        Assert.Equal(0, entry.Properties["InFlightJobs"]);
        Assert.Equal(2, entry.Properties["AvailableSlots"]);
        Assert.Equal(true, entry.Properties["QueueStatusAvailable"]);
    }

    [Fact]
    public async Task MonitoringFailureIsLoggedAsUnavailableAndDoesNotStopClaims()
    {
        var logger = new AutoCallDiagnosticsTests.Logs<AutoCallWorker>();
        var repository = new Repository { StatusFailure = new TimeoutException("private database details") };
        repository.Jobs.Enqueue(Job());
        var processing = new Processing();
        using var worker = new AutoCallWorker(repository, processing, Options(), logger);
        Assert.Equal(1, await worker.RunCycleAsync(default));
        var failure = Assert.Single(logger.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error));
        Assert.DoesNotContain("private database details", failure.Message);
        var summary = Assert.Single(logger.Entries.Where(entry => entry.Properties.ContainsKey("PassNumber")));
        Assert.Equal(false, summary.Properties["QueueStatusAvailable"]);
        Assert.Null(summary.Properties["WaitingJobs"]);
        Assert.Equal(1, summary.Properties["ClaimedJobs"]);
        processing.Pending[0].SetResult(Attempt(true));
        await worker.WaitForCompletionAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HostedWorkerLogsStartupAndShutdown()
    {
        var logger = new AutoCallDiagnosticsTests.Logs<AutoCallWorker>();
        var repository = new Repository();
        using var worker = new AutoCallWorker(repository, new Processing(), Options(), logger);
        await worker.StartAsync(default);
        await repository.StatusRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(stopping.Token);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains(" started;") &&
            Equals(2, entry.Properties["MaxConcurrency"]));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains(" stopping;"));
        Assert.Contains(logger.Entries, entry => entry.Message.EndsWith(" stopped", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public void QueueStatusQueryHasABoundedTimeoutAndPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var command = AutoCallRepository.QueueStatusCommand(cancellation.Token);
        Assert.Equal(CommandType.Text, command.CommandType);
        Assert.Equal(5, command.CommandTimeout);
        Assert.Equal(cancellation.Token, command.CancellationToken);
        Assert.Contains("CAST(1 AS bigint)", command.CommandText);
    }

    [Fact]
    public async Task WorkerClaimsOnlyFreeSlotsAndReplenishesOnlyCompletedWork()
    {
        var repository = new Repository();
        foreach (int id in new[] { 1, 2, 3 }) repository.Jobs.Enqueue(Job(id));
        var processing = new Processing();
        using var worker = new AutoCallWorker(repository, processing, Options(), NullLogger<AutoCallWorker>.Instance);
        Assert.Equal(2, await worker.RunCycleAsync(default));
        Assert.Equal(2, await worker.RunCycleAsync(default));
        Assert.Equal(new[] { 2 }, repository.Capacities);
        processing.Pending[0].SetResult(Attempt(true));
        await worker.WaitForCompletionAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await worker.RunCycleAsync(default);
        Assert.Equal(new[] { 2, 1 }, repository.Capacities);
        Assert.Equal(3, processing.Pending.Count);
        foreach (var pending in processing.Pending) pending.TrySetResult(Attempt(true));
    }

    [Fact]
    public async Task PreparationFailureGetsDispositionButHostCancellationDoesNot()
    {
        var logger = new AutoCallDiagnosticsTests.Logs<AutoCallWorker>();
        var repository = new Repository();
        repository.Jobs.Enqueue(Job());
        var processing = new Processing();
        using var worker = new AutoCallWorker(repository, processing, Options(), logger);
        await worker.RunCycleAsync(default);
        processing.Pending[0].SetException(new InvalidOperationException("private payload"));
        await worker.WaitForCompletionAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "fail" }, repository.Calls);
        var entry = Assert.Single(logger.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error));
        Assert.Equal("Preparation", entry.Properties["Operation"]);
        Assert.Equal(1, entry.Properties["QueueId"]);
        Assert.DoesNotContain("private payload", entry.Message);

        repository = new Repository();
        repository.Jobs.Enqueue(Job());
        processing = new Processing();
        using var stopping = new CancellationTokenSource();
        using var canceledWorker = new AutoCallWorker(repository, processing, Options(), logger);
        await canceledWorker.RunCycleAsync(stopping.Token);
        stopping.Cancel();
        await canceledWorker.WaitForCompletionAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(repository.Calls);
        Assert.Single(logger.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error));
    }

    [Fact]
    public async Task MissingVariablesAndMismatchedGraphNeverSaveAPartialBundle()
    {
        var repository = new Repository { Result = Attempt(variables: "{}") };
        var service = new AutoCallProcessingService(repository, Contacts("+15551234567"), Options());
        await Assert.ThrowsAsync<AutoCallValidationException>(() => service.PrepareAsync(Job(), "worker", default));
        Assert.Equal(new[] { "begin" }, repository.Calls);
        Assert.Throws<AutoCallValidationException>(() => AutoCallBundlePreparer.Prepare(Attempt(graph: "{\"schemaVersion\":1,\"initialStep\":\"other\",\"steps\":[{\"id\":\"other\"}]}"), Options().FeedbackBaseUrl));
        Assert.Throws<System.Xml.XmlException>(() => AutoCallBundlePreparer.Prepare(Attempt(xml: "<!DOCTYPE CallFlow [<!ENTITY value 'text'>]><CallFlow>&value;</CallFlow>"), Options().FeedbackBaseUrl));
    }

    private static AutoCallAttempt MachineAttempt(string? graphMachine, string? xmlMachine, string vmType = "terminal", string vmResponse = "<Say>Call back.</Say><Hangup/>", string endTarget = "end")
        => Attempt(
            graph: "{\"schemaVersion\":1,\"initialStep\":\"hello\"" + (graphMachine is null ? "" : $",\"machineStep\":\"{graphMachine}\"") +
                ",\"steps\":[{\"id\":\"hello\",\"type\":\"gather\",\"transitions\":{\"digits\":{\"1\":\"" + endTarget + "\"},\"noInput\":\"end\",\"fallback\":\"end\"}}," +
                "{\"id\":\"end\",\"type\":\"terminal\"},{\"id\":\"vm\",\"type\":\"" + vmType + "\"}]}",
            xml: "<CallFlow initialStep=\"hello\"" + (xmlMachine is null ? "" : $" machineStep=\"{xmlMachine}\"") +
                "><Step id=\"hello\"><Response><Say>{{OffenderName}}</Say><Gather/></Response></Step><Step id=\"end\"><Response><Hangup/></Response></Step>" +
                "<Step id=\"vm\"><Response>" + vmResponse + "</Response></Step></CallFlow>");

    [Fact]
    public void MachineStepPresentInBothColumnsPrepares()
    {
        var bundle = AutoCallBundlePreparer.Prepare(MachineAttempt("vm", "vm"), Options().FeedbackBaseUrl);
        Assert.Equal("vm", (string?)XDocument.Parse(bundle.TwiML).Root!.Attribute("machineStep"));
        AutoCallBundlePreparer.Prepare(MachineAttempt(null, null), Options().FeedbackBaseUrl);
    }

    [Theory]
    [InlineData("vm", null, "terminal", "<Say>Call back.</Say><Hangup/>", "end")]
    [InlineData(null, "vm", "terminal", "<Say>Call back.</Say><Hangup/>", "end")]
    [InlineData("vm", "end", "terminal", "<Say>Call back.</Say><Hangup/>", "end")]
    [InlineData("missing", "missing", "terminal", "<Say>Call back.</Say><Hangup/>", "end")]
    [InlineData("hello", "hello", "terminal", "<Say>Call back.</Say><Hangup/>", "end")]
    [InlineData("vm", "vm", "gather", "<Say>Call back.</Say><Hangup/>", "end")]
    [InlineData("vm", "vm", "terminal", "<Say>Call back.</Say><Hangup/>", "vm")]
    [InlineData("vm", "vm", "terminal", "<Say>Call back.</Say>", "end")]
    [InlineData("vm", "vm", "terminal", "<Gather/><Hangup/>", "end")]
    public void InvalidMachineStepIsRejected(string? graphMachine, string? xmlMachine, string vmType, string vmResponse, string endTarget)
        => Assert.Throws<AutoCallValidationException>(() => AutoCallBundlePreparer.Prepare(
            MachineAttempt(graphMachine, xmlMachine, vmType, vmResponse, endTarget), Options().FeedbackBaseUrl));

    private static AutoCallAttempt RecordAttempt(string transitions = "{\"recorded\":\"end\",\"noRecording\":\"end\"}", string response = "<Say>Leave a message.</Say><Record/><Redirect/>",
        string helloResponse = "<Say>Hi</Say><Redirect/>", string? machine = null)
        => Attempt(
            graph: "{\"schemaVersion\":1,\"initialStep\":\"hello\"" + (machine is null ? "" : $",\"machineStep\":\"{machine}\"") +
                ",\"steps\":[{\"id\":\"hello\",\"type\":\"redirect\",\"transitions\":{\"next\":\"message\"}}," +
                "{\"id\":\"message\",\"type\":\"record\",\"transitions\":" + transitions + "},{\"id\":\"end\",\"type\":\"terminal\"},{\"id\":\"vm\",\"type\":\"terminal\"}]}",
            xml: "<CallFlow initialStep=\"hello\"" + (machine is null ? "" : $" machineStep=\"{machine}\"") + "><Step id=\"hello\"><Response>" + helloResponse + "</Response></Step>" +
                "<Step id=\"message\"><Response>" + response + "</Response></Step><Step id=\"end\"><Response><Hangup/></Response></Step>" +
                "<Step id=\"vm\"><Response><Say>Call back.</Say><Hangup/></Response></Step></CallFlow>");

    [Fact]
    public void RecordStepKeepsBuilderAttributesAndFillsDefaults()
    {
        var bundle = AutoCallBundlePreparer.Prepare(RecordAttempt(response:
            "<Say>Leave a message.</Say><Pause length=\"2\"/><Record maxLength=\"300\" timeout=\"30\" finishOnKey=\"*1\" playBeep=\"false\" trim=\"do-not-trim\" recordingStatusCallback=\"old\"/><Redirect/>"),
            Options().FeedbackBaseUrl);
        var record = XDocument.Parse(bundle.TwiML).Descendants("Record").Single();
        Assert.Equal(("300", "30", "*1", "false", "do-not-trim"), ((string?)record.Attribute("maxLength"), (string?)record.Attribute("timeout"),
            (string?)record.Attribute("finishOnKey"), (string?)record.Attribute("playBeep"), (string?)record.Attribute("trim")));
        Assert.Equal("https://feedback.example/prefix/webhooks/twilio/recordings/status?callId=42", (string?)record.Attribute("recordingStatusCallback"));
        Assert.Equal("completed", (string?)record.Attribute("recordingStatusCallbackEvent"));
    }

    [Theory]
    [InlineData("{\"recorded\":\"end\"}", null, null)]
    [InlineData("{\"recorded\":\"end\",\"noRecording\":\"end\",\"next\":\"end\"}", null, null)]
    [InlineData("{\"recorded\":\"end\",\"noRecording\":\"missing\"}", null, null)]
    [InlineData("{\"recorded\":\"end\",\"noRecording\":{\"x\":\"end\"}}", null, null)]
    [InlineData("{\"recorded\":\"vm\",\"noRecording\":\"end\"}", null, "vm")]
    [InlineData(null, "<Say>Hi</Say><Record/>", null)]
    [InlineData(null, "<Record/><Say>Hi</Say><Redirect/>", null)]
    [InlineData(null, "<Say>Hi</Say><Redirect/>", null)]
    [InlineData(null, "<Say>Hi</Say><Record/><Record/><Redirect/>", null)]
    [InlineData(null, "<Gather/><Record/><Redirect/>", null)]
    [InlineData(null, "<Play>x</Play><Record/><Redirect/>", null)]
    [InlineData(null, "<Record><Say>x</Say></Record><Redirect/>", null)]
    [InlineData(null, "<Record>text</Record><Redirect/>", null)]
    [InlineData(null, "<Record maxLength=\"0\"/><Redirect/>", null)]
    [InlineData(null, "<Record maxLength=\"301\"/><Redirect/>", null)]
    [InlineData(null, "<Record maxLength=\"1.5\"/><Redirect/>", null)]
    [InlineData(null, "<Record timeout=\"31\"/><Redirect/>", null)]
    [InlineData(null, "<Record timeout=\"0\"/><Redirect/>", null)]
    [InlineData(null, "<Record finishOnKey=\"\"/><Redirect/>", null)]
    [InlineData(null, "<Record finishOnKey=\"a\"/><Redirect/>", null)]
    [InlineData(null, "<Record playBeep=\"yes\"/><Redirect/>", null)]
    [InlineData(null, "<Record trim=\"none\"/><Redirect/>", null)]
    [InlineData(null, "<Record transcribe=\"true\"/><Redirect/>", null)]
    [InlineData(null, "<Record transcribeCallback=\"x\"/><Redirect/>", null)]
    [InlineData(null, "<Record unknown=\"1\"/><Redirect/>", null)]
    [InlineData(null, "<Record maxLength=\"{{OffenderName}}\"/><Redirect/>", null)]
    public void InvalidRecordStepIsRejected(string? transitions, string? response, string? machine)
        => Assert.Throws<AutoCallValidationException>(() => AutoCallBundlePreparer.Prepare(
            RecordAttempt(transitions ?? "{\"recorded\":\"end\",\"noRecording\":\"end\"}", response ?? "<Say>Leave a message.</Say><Record/><Redirect/>", machine: machine),
            Options().FeedbackBaseUrl));

    [Fact]
    public void RecordOutsideARecordStepIsRejected()
        => Assert.Throws<AutoCallValidationException>(() => AutoCallBundlePreparer.Prepare(
            RecordAttempt(helloResponse: "<Say>Hi</Say><Record/><Redirect/>"), Options().FeedbackBaseUrl));

    [Theory]
    [InlineData("Normal", false)]
    [InlineData("Normal", true)]
    [InlineData("Step", true)]
    [InlineData("AutoCall", false)]
    [InlineData("AutoCall", true)]
    [InlineData("Transcription", true)]
    public void PreparationWorkerIsSelectedOnlyByWorkerMode(string mode, bool legacyEnabled)
    {
        bool registered = false;
        using var host = Program.CreateHostBuilder(["--WorkerMode", mode, "--AutoCall:Worker:Enabled", legacyEnabled.ToString(), "--AutoCall:Worker:FeedbackBaseUrl", "https://feedback.example"])
            .ConfigureServices((_, services) => registered = services.Any(descriptor => descriptor.ImplementationType == typeof(AutoCallWorker)))
            .Build();
        Assert.Equal(mode == "AutoCall", registered);
        Assert.IsType<AutoCallRepository>(host.Services.GetRequiredService<IAutoCallRepository>());
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(101, 60)]
    [InlineData(2, 300)]
    public void InvalidCapacityAndPreparationDeadlineAreRejected(int capacity, int seconds)
        => Assert.Throws<InvalidOperationException>(() => new AutoCallWorkerOptions { MaxConcurrency = capacity, PreparationTimeoutSeconds = seconds }.Validate());
}