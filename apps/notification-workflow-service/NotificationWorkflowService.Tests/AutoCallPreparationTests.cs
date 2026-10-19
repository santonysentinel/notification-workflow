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
        TemplateJson = graph ?? "{\"schemaVersion\":1,\"initialStep\":\"hello\",\"steps\":[{\"id\":\"hello\",\"type\":\"gather\",\"transitions\":{\"noInput\":\"end\",\"fallback\":\"end\"}},{\"id\":\"end\",\"type\":\"terminal\"}]}",
        TemplateTwiML = xml ?? "<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say>{{OffenderName}} {{recipient}} {{callId}}</Say><Gather action=\"{{feedbackBaseUrl}}/old\"/><Redirect>old</Redirect><Record/></Response></Step><Step id=\"end\"><Response><Hangup/></Response></Step></CallFlow>",
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
    }
    private sealed class Routing : IAutoCallRoutingStore
    {
        public Task<string?> GetAsync(string key, CancellationToken token) => Task.FromResult<string?>("TENANT");
    }
    private sealed class Phones(params string[] numbers) : IAutoCallPhoneRepository
    {
        public Task<IReadOnlyList<string>> ReadAsync(string connection, string oid, CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(numbers);
    }
    private static AutoCallContactService Contacts(params string[] numbers) => new(new Routing(), new Phones(numbers),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TENANT"] = "unused" }).Build());

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
        Assert.Equal(next, xml.Descendants("Redirect").Single().Value);
        Assert.Equal("https://feedback.example/prefix/webhooks/twilio/recordings/status?callId=42", (string?)xml.Descendants("Record").Single().Attribute("recordingStatusCallback"));
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
        var alarm = new NotificationWorkflowService.Entity.ActiveAlarm { SystemID = 42, ClientID = "oid", POGroupNum = "group", FlowId = 12 };
        var template = Attempt(xml:
            "<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say>Hello, {{OffenderName}}. {{alarmname}} at {{LAT}},{{lon}}</Say></Response></Step><Step id=\"end\"><Response><Hangup/></Response></Step></CallFlow>");
        var request = AutoCallEnqueueService.CaptureRequest(alarm, "DEV1",
            new AutoCallFlow(12, template.TemplateJson, template.TemplateTwiML), details);
        using var command = AutoCallQueueRepository.BuildEnqueueCommand(request);
        var attempt = Attempt(variables: (string)command.Parameters["@VariablesJSON"].Value, xml: template.TemplateTwiML);

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
        var entry = Assert.Single(logger.Entries);
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
        Assert.Single(logger.Entries);
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