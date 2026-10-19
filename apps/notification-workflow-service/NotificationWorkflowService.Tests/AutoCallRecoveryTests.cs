using System.Net;
using System.Data;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.AutoCall;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class AutoCallRecoveryTests
{
    private const string Account = "AC11111111111111111111111111111111";
    private const string Sid = "CA22222222222222222222222222222222";
    private static AutoCallSubmissionRequest Request() => new(1, Account, "+15555555555", "+16666666666", "https://feedback.test/start", "https://feedback.test/status", "https://feedback.test/recording", false);
    private static TwilioCallClient Client(Handler handler) => new(new Factory(handler), new() { Enabled = true, AccountSid = Account, AuthToken = "secret", FromNumber = "+16666666666" });

    [Theory]
    [InlineData(0, 0, 15)]
    [InlineData(0, 1, 30)]
    [InlineData(1, 1, 60)]
    [InlineData(30, 1, 900)]
    public void BackoffIsJitteredAndCapped(int count, double jitter, int expected)
        => Assert.Equal(expected, new AutoCallRecoveryOptions().BackoffSeconds(count, jitter));

    [Theory]
    [InlineData("{\"status\":429,\"code\":20429}", true, "TWILIO_20429")]
    [InlineData("{\"status\":429,\"code\":999}", false, "ACCEPTANCE_UNKNOWN")]
    [InlineData("not-json", false, "ACCEPTANCE_UNKNOWN")]
    public async Task OnlyDocumentedRateLimitRejectionIsRetryable(string body, bool rejected, string code)
    {
        var handler = new Handler(HttpStatusCode.TooManyRequests, body);
        var error = await Assert.ThrowsAsync<TwilioSubmissionException>(() => Client(handler).CreateAsync(Request(), default));
        Assert.Equal(rejected, error.Rejected);
        Assert.Equal(code, error.ErrorCode);
        Assert.Single(handler.Methods);
    }

    [Theory]
    [InlineData(400, "{\"status\":400,\"code\":21211}", true, "TWILIO_21211")]
    [InlineData(401, "{\"status\":401,\"code\":20003}", true, "TWILIO_20003")]
    [InlineData(400, "{\"status\":401,\"code\":21211}", false, "ACCEPTANCE_UNKNOWN")]
    [InlineData(400, "{\"status\":400,\"code\":20429}", false, "ACCEPTANCE_UNKNOWN")]
    [InlineData(400, "private proxy response", false, "ACCEPTANCE_UNKNOWN")]
    public async Task DefiniteRejectionRequiresAValidatedTwilioError(int status, string body, bool rejected, string code)
    {
        var handler = new Handler((HttpStatusCode)status, body);
        var error = await Assert.ThrowsAsync<TwilioSubmissionException>(() => Client(handler).CreateAsync(Request(), default));
        Assert.Equal(rejected, error.Rejected);
        Assert.Equal(code, error.ErrorCode);
        Assert.DoesNotContain(body, error.ToString());
        Assert.Single(handler.Methods);
    }

    [Fact]
    public async Task KnownSidUsesGetAndValidatesTerminalIdentity()
    {
        var handler = new Handler(HttpStatusCode.OK, $"{{\"sid\":\"{Sid}\",\"account_sid\":\"{Account}\",\"to\":\"+15555555555\",\"from\":\"+16666666666\",\"status\":\"completed\",\"duration\":\"17\"}}");
        var observation = await Client(handler).GetAsync(Request(), Sid, default);
        Assert.True(observation.Terminal);
        Assert.Equal(17, observation.DurationSeconds);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Methods));
        Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{Account}/Calls/{Sid}.json", handler.Url);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task UnavailableOrMismatchedSidDoesNotRedial(HttpStatusCode status)
    {
        var handler = new Handler(status, "{}");
        await Assert.ThrowsAsync<AutoCallReconciliationException>(() => Client(handler).GetAsync(Request(), Sid, default));
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Methods));
    }

    [Theory]
    [InlineData("SUBMITTING")]
    [InlineData("UNKNOWN")]
    public async Task AmbiguousAcceptanceWaitsWithoutPreparationOrPost(string state)
    {
        var harness = new Harness { Attempt = Attempt(state) };
        await harness.Service().ProcessAsync(Job(), default);
        Assert.Equal("WAIT", harness.Disposition);
        Assert.Equal(0, harness.Posts);
        Assert.Equal(0, harness.Preparations);
        Assert.Equal(0, harness.Lookups);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("busy")]
    [InlineData("failed")]
    [InlineData("no-answer")]
    public async Task KnownSidRecoversMissingTerminalCallbackWithoutPost(string status)
    {
        var harness = new Harness { Attempt = Attempt("ACCEPTED", sid: Sid), Observation = new(Sid, status, 12) };
        await harness.Service().ProcessAsync(Job(), default);
        Assert.Equal(0, harness.Posts);
        Assert.Equal(1, harness.Lookups);
        Assert.Equal(status, harness.Observed?.Status);
        Assert.Null(harness.Disposition);
    }

    [Fact]
    public async Task KnownSidUnavailableStillNeverRedials()
    {
        var harness = new Harness { Attempt = Attempt("ACCEPTED", sid: Sid), LookupFailure = new AutoCallReconciliationException() };
        await harness.Service().ProcessAsync(Job(), default);
        Assert.Equal("WAIT", harness.Disposition);
        Assert.Equal("RECONCILIATION_UNAVAILABLE", harness.ErrorCode);
        Assert.Equal(0, harness.Posts);
    }

    [Fact]
    public async Task PermanentRejectionIsDeadLetteredWithoutPost()
    {
        var harness = new Harness { Attempt = Attempt("REJECTED", "HTTP_400") };
        await harness.Service().ProcessAsync(Job(), default);
        Assert.Equal("DEAD_LETTER", harness.Disposition);
        Assert.Equal("SUBMISSION_REJECTED", harness.ErrorCode);
        Assert.Equal(0, harness.Posts);
    }

    [Fact]
    public async Task ProvenRejectedAttemptCreatesFreshAttemptWithOriginalRecipient()
    {
        var harness = new Harness { Attempt = Attempt("REJECTED", "TWILIO_20429") };
        await harness.Service().ProcessAsync(Job(), default);
        Assert.Equal(1, harness.Begins);
        Assert.Equal(1, harness.Posts);
        Assert.Equal(Request().To, harness.RetryRecipient);
        Assert.Equal("WAIT", harness.Disposition);
    }

    [Fact]
    public async Task PreparedUnsubmittedAttemptReusesItsOriginalIdentity()
    {
        var harness = new Harness { Attempt = Attempt("READY") };
        await harness.Service().ProcessAsync(Job(), default);
        Assert.Equal(0, harness.Begins);
        Assert.Equal(0, harness.Preparations);
        Assert.Equal(1, harness.Posts);
        Assert.Same(harness.Attempt, harness.Submitted);
        Assert.NotEqual(harness.Submitted!.QueueLeaseToken, harness.Submitted.CurrentLeaseToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparationFailureDistinguishesValidationFromTransientUnavailable(bool validation)
    {
        var harness = new Harness { PreparationFailure = validation ? new InvalidOperationException("private") : new TimeoutException("private") };
        await harness.Service().ProcessAsync(Job(), default);
        Assert.Equal(validation ? "DEAD_LETTER" : "RETRY", harness.Disposition);
        Assert.Equal(validation ? "VALIDATION_FAILED" : "PREPARATION_UNAVAILABLE", harness.ErrorCode);
        Assert.Equal(0, harness.Posts);
        Assert.InRange(harness.DelaySeconds, 1, 900);
    }

    [Fact]
    public async Task ValidationFailureLogsActionableReasonAndDeadLetterDisposition()
    {
        var logger = new AutoCallDiagnosticsTests.Logs<AutoCallRecoveryService>();
        var harness = new Harness { PreparationFailure = new AutoCallValidationException("Required preparation variable 'OffenderName' is missing.") };
        await harness.Service(logger: logger).ProcessAsync(Job(), default);
        var entry = Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Error));
        Assert.Contains("OffenderName", entry.Message);
        Assert.Equal(1, entry.Properties["QueueId"]);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("DEAD_LETTER") && entry.Message.Contains("VALIDATION_FAILED"));
        Assert.Equal("DEAD_LETTER", harness.Disposition);
        Assert.Equal(0, harness.Posts);
    }

    [Fact]
    public async Task RenewalCancellationLogsDeadlineAndPreventsProcessing()
    {
        var logger = new AutoCallDiagnosticsTests.Logs<AutoCallRecoveryService>();
        var harness = new Harness { RenewalFailure = new TaskCanceledException("secret timeout details") };
        await harness.Service(logger: logger).ProcessAsync(Job(), default);
        var entry = Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Warning));
        Assert.Equal("Lease renewal (ownership uncertain)", entry.Properties["Operation"]);
        Assert.Contains("5-second deadline", entry.Message);
        Assert.DoesNotContain("secret timeout details", entry.Message);
        Assert.Equal(0, harness.Posts);
        Assert.Equal(0, harness.Reads);
        Assert.Null(harness.Disposition);
    }

    [Fact]
    public async Task ReconciliationFailureIsLoggedBeforeWaiting()
    {
        var logger = new AutoCallDiagnosticsTests.Logs<AutoCallRecoveryService>();
        var harness = new Harness { Attempt = Attempt("ACCEPTED", sid: Sid), LookupFailure = new AutoCallReconciliationException() };
        await harness.Service(logger: logger).ProcessAsync(Job(), default);
        var entry = Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Error));
        Assert.Equal("Reconciliation lookup", entry.Properties["Operation"]);
        Assert.Equal(1, entry.Properties["CallId"]);
        Assert.Equal("WAIT", harness.Disposition);
        Assert.Equal(0, harness.Posts);
    }

    [Fact]
    public async Task SubmissionBudgetExhaustionDoesNotSend()
    {
        var harness = new Harness { Attempt = Attempt("READY") };
        var job = Job();
        job.SubmissionCount = 3;
        await harness.Service().ProcessAsync(job, default);
        Assert.Equal("DEAD_LETTER", harness.Disposition);
        Assert.Equal("RETRY_EXHAUSTED", harness.ErrorCode);
        Assert.Equal(0, harness.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostOrUncertainOwnershipStopsWork(bool unavailable)
    {
        var harness = new Harness { Renewed = false, RenewalFailure = unavailable ? new TimeoutException("private") : null };
        await harness.Service().ProcessAsync(Job(), default);
        Assert.Equal(0, harness.Reads);
        Assert.Equal(0, harness.Posts);
        Assert.Null(harness.Disposition);
    }

    [Fact]
    public async Task HeartbeatLossCancelsPreparationAndPreventsPost()
    {
        var harness = new Harness { LoseOnSecondRenewal = true, BlockPreparation = true };
        await harness.Service(renewalSeconds: 1).ProcessAsync(Job(), default);
        Assert.Equal(2, harness.Renewals);
        Assert.Equal(1, harness.Preparations);
        Assert.Equal(0, harness.Posts);
        Assert.Null(harness.Disposition);
    }

    [Fact]
    public async Task OwnershipLossDuringPostStillRetainsLateAcceptance()
    {
        var harness = new Harness { Attempt = Attempt("READY"), LoseOnSecondRenewal = true, BlockSubmission = true };
        await harness.Service(renewalSeconds: 1).ProcessAsync(Job(), default);
        Assert.Equal(1, harness.Posts);
        Assert.True(harness.LateAcceptancePersisted);
        Assert.Null(harness.Disposition);
    }

    [Theory]
    [InlineData("sid", "CA99999999999999999999999999999999")]
    [InlineData("account_sid", "AC99999999999999999999999999999999")]
    [InlineData("to", "+17777777777")]
    [InlineData("from", "+17777777777")]
    [InlineData("status", "invalid")]
    [InlineData("duration", "-1")]
    public async Task ReconciliationRejectsIdentityOrStatusMismatch(string field, string value)
    {
        var body = new Dictionary<string, string> { ["sid"] = Sid, ["account_sid"] = Account, ["to"] = Request().To, ["from"] = Request().From, ["status"] = "completed", ["duration"] = "17" };
        body[field] = value;
        var handler = new Handler(HttpStatusCode.OK, JsonSerializer.Serialize(body));
        var error = await Assert.ThrowsAsync<AutoCallReconciliationException>(() => Client(handler).GetAsync(Request(), Sid, default));
        Assert.DoesNotContain(value, error.ToString());
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Methods));
    }

    [Fact]
    public void CurrentOwnershipIsSeparateFromLateSubmissionIdentity()
    {
        var attempt = Attempt("READY");
        var save = AutoCallRepository.SaveCommand(attempt, "worker", new("xml", "{}"), default);
        var marker = AutoCallRepository.BeginSubmissionCommand(attempt, "worker", Guid.NewGuid(), Request(), default, 4);
        var late = AutoCallRepository.SaveSubmissionCommand(attempt, Guid.NewGuid(), "ACCEPTED", Sid, null, default);
        Assert.Equal(attempt.CurrentLeaseToken, ((DynamicParameters)save.Parameters!).Get<Guid>("@LeaseToken"));
        Assert.Equal(attempt.QueueLeaseToken, ((DynamicParameters)marker.Parameters!).Get<Guid>("@LeaseToken"));
        Assert.Equal(attempt.CurrentLeaseToken, ((DynamicParameters)marker.Parameters!).Get<Guid>("@CurrentLeaseToken"));
        Assert.Equal(4, ((DynamicParameters)marker.Parameters!).Get<int>("@MaxSubmissionAttempts"));
        Assert.Equal(attempt.QueueLeaseToken, ((DynamicParameters)late.Parameters!).Get<Guid>("@LeaseToken"));
    }

    [Fact]
    public void InitialRolloutDefaultsToOneConcurrentRequest()
        => Assert.Equal(1, new AutoCallWorkerOptions().MaxConcurrency);

    [Fact]
    public async Task WorkerDependencyInjectionSelectsRecoveryConstructor()
    {
        var harness = new Harness();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoCall:Worker:FeedbackBaseUrl"] = "https://feedback.example"
        }).Build());
        services.AddAutoCallContacts();
        services.AddSingleton<IAutoCallRepository>(harness);
        services.AddSingleton<IAutoCallProcessingService>(harness);
        services.AddSingleton<IAutoCallSubmissionService>(harness);
        services.AddSingleton<IAutoCallRecoveryService>(harness);
        using var provider = services.BuildServiceProvider();
        using var worker = ActivatorUtilities.CreateInstance<AutoCallWorker>(provider);
        await worker.RunCycleAsync(default);
        Assert.Equal(1, harness.RecoveryCalls);
        Assert.Equal(0, harness.Preparations);
        Assert.Equal(0, harness.Posts);
    }

    private static AutoCallJob Job() => new() { SystemID = 1, FlowId = 1, OID = "test", MaxAttempts = 10, LeaseToken = Guid.NewGuid() };
    private static AutoCallAttempt Attempt(string state, string? error = null, string? sid = null) => new()
    {
        CallId = 1, QueueId = 1, QueueLeaseToken = Guid.NewGuid(), CurrentLeaseToken = Guid.NewGuid(), OID = "test", FlowId = 1,
        PhoneE164 = Request().To, TwiML = "prepared", Parameters = "{}", SubmissionState = state, SubmissionErrorCode = error,
        ProviderCallId = sid, SubmissionRequestJSON = JsonSerializer.Serialize(Request())
    };

    private sealed class Harness : IAutoCallRecoveryRepository, IAutoCallRepository, IAutoCallProcessingService, IAutoCallSubmissionService, ITwilioCallLookup, IAutoCallRecoveryService
    {
        public AutoCallAttempt? Attempt { get; init; }
        public AutoCallAttempt? Submitted { get; private set; }
        public TwilioCallObservation Observation { get; init; } = new(Sid, "in-progress", null);
        public TwilioCallObservation? Observed { get; private set; }
        public Exception? LookupFailure { get; init; }
        public Exception? PreparationFailure { get; init; }
        public Exception? RenewalFailure { get; init; }
        public bool Renewed { get; init; } = true;
        public bool LoseOnSecondRenewal { get; init; }
        public bool BlockPreparation { get; init; }
        public bool BlockSubmission { get; init; }
        public bool LateAcceptancePersisted { get; private set; }
        public int Renewals { get; private set; }
        public int RecoveryCalls { get; private set; }
        public int Reads { get; private set; }
        public int Posts { get; private set; }
        public int Preparations { get; private set; }
        public int Lookups { get; private set; }
        public int Begins { get; private set; }
        public string? RetryRecipient { get; private set; }
        public string? Disposition { get; private set; }
        public string? ErrorCode { get; private set; }
        public int DelaySeconds { get; private set; }
        public Task<AutoCallQueueStatus> ReadQueueStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AutoCallQueueStatus());
        public AutoCallRecoveryService Service(int renewalSeconds = 60, ILogger<AutoCallRecoveryService>? logger = null) => new(this, this, this, this, this,
            new() { RenewalSeconds = renewalSeconds }, new() { WorkerId = "worker" }, logger ?? NullLogger<AutoCallRecoveryService>.Instance);
        public Task<bool> RenewAsync(AutoCallJob job, string workerId, int leaseMinutes, CancellationToken cancellationToken)
        {
            Renewals++;
            if (RenewalFailure is not null) throw RenewalFailure;
            return Task.FromResult(Renewed && (!LoseOnSecondRenewal || Renewals < 2));
        }
        public Task<AutoCallAttempt?> ReadRecoveryAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Attempt);
        }
        public Task DispositionAsync(AutoCallJob job, string workerId, string disposition, string errorCode, int delaySeconds, AutoCallRecoveryOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Disposition = disposition;
            ErrorCode = errorCode;
            DelaySeconds = delaySeconds;
            return Task.CompletedTask;
        }
        public Task ObserveAsync(AutoCallJob job, string workerId, AutoCallAttempt attempt, TwilioCallObservation observation, int reconcileSeconds, CancellationToken cancellationToken)
        {
            Observed = observation;
            return Task.CompletedTask;
        }
        public Task<TwilioCallObservation> GetAsync(AutoCallSubmissionRequest request, string sid, CancellationToken cancellationToken)
        {
            Lookups++;
            if (LookupFailure is not null) throw LookupFailure;
            return Task.FromResult(Observation);
        }
        public async Task<AutoCallAttempt> PrepareAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken)
        {
            Preparations++;
            if (PreparationFailure is not null) throw PreparationFailure;
            if (BlockPreparation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return AutoCallRecoveryTests.Attempt("READY");
        }
        public async Task<AutoCallSubmissionResult> SubmitAsync(AutoCallAttempt attempt, string workerId, CancellationToken cancellationToken)
        {
            Posts++;
            Submitted = attempt;
            if (BlockSubmission)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { LateAcceptancePersisted = true; }
            }
            return new AutoCallSubmissionResult { SubmissionState = "ACCEPTED", ProviderCallId = Sid };
        }
        public Task<AutoCallAttempt> BeginAttemptAsync(AutoCallJob job, string workerId, string recipient, CancellationToken cancellationToken)
        {
            Begins++;
            RetryRecipient = recipient;
            return Task.FromResult(AutoCallRecoveryTests.Attempt("READY"));
        }
        public Task ProcessAsync(AutoCallJob job, CancellationToken cancellationToken)
        {
            RecoveryCalls++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AutoCallJob>> ClaimAsync(int capacity, string workerId, int leaseMinutes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AutoCallJob>>([Job()]);
        public Task<int> SavePreparationAsync(AutoCallAttempt attempt, string workerId, AutoCallPreparedBundle bundle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailPreparationAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public string? Url { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Url = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

[Collection("AutoCall SQL rollout")]
public sealed class AutoCallSqlConcurrencyTests
{
    internal const string ConnectionVariable = "AUTOCALL_TEST_SQL_CONNECTION";

    public sealed class SqlFactAttribute : FactAttribute
    {
        public SqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = "Set AUTOCALL_TEST_SQL_CONNECTION to the owned NotificationWorkflowVoiceTests database.";
        }
    }

    [CollectionDefinition("AutoCall SQL rollout", DisableParallelization = true)]
    public sealed class SqlCollection;

    [SqlFact]
    public Task ConcurrentReadyClaimsHaveExactlyOneOwner() => VerifyClaimsAsync(false);

    [SqlFact]
    public Task ConcurrentExpiredClaimsHaveExactlyOneReplacementOwner() => VerifyClaimsAsync(true);

    [SqlFact]
    public async Task ConcurrentDuplicateEnqueuesReturnOneImmutableJob()
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenAsync();
        Guid key = Guid.NewGuid();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> EnqueueAsync(SqlConnection connection)
        {
            await start.Task;
            return await connection.QuerySingleAsync<int>(new CommandDefinition("dbo.activealarms_call_InsertJob",
                new { ActiveAlarmID = 42, OID = fixture.Oid, FlowId = fixture.FlowId, VariablesJSON = "{\"name\":\"Alex & <Co>\"}", EnqueueIdempotencyKey = key },
                commandType: CommandType.StoredProcedure, commandTimeout: 10));
        }
        var requests = new[] { EnqueueAsync(first), EnqueueAsync(second) };
        start.SetResult();
        int[] ids = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(1, await first.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AutomatedCallQueue WHERE OID=@Oid", new { fixture.Oid }));
        Assert.Equal("{\"name\":\"Alex & <Co>\"}", await first.ExecuteScalarAsync<string>("SELECT VariablesJSON FROM dbo.AutomatedCallQueue WHERE SystemID=@Id", new { Id = ids[0] }));
    }

    private static async Task VerifyClaimsAsync(bool expired)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenAsync();
        Guid originalToken = Guid.NewGuid();
        int queueId = await first.QuerySingleAsync<int>(new CommandDefinition("""
            INSERT dbo.AutomatedCallQueue(ActiveAlarmID,OID,FlowId,VariablesJSON,EnqueueIdempotencyKey,Priority,Status,AttemptCount,LeaseToken,LockedBy,LockedUntil)
            OUTPUT inserted.SystemID
            VALUES(42,@Oid,@FlowId,N'{}',NEWID(),2147483647,@Status,@Attempts,@OriginalToken,'original-owner',DATEADD(second,-10,SYSUTCDATETIME()));
            """, new { fixture.Oid, fixture.FlowId, Status = expired ? "PROCESSING" : "READY", Attempts = expired ? 1 : 0, OriginalToken = originalToken }, commandTimeout: 10));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<AutoCallJob>> ClaimAsync(SqlConnection connection, string worker)
        {
            await start.Task;
            return (await connection.QueryAsync<AutoCallJob>(AutoCallRepository.ClaimCommand(1, worker, 5, default))).ToList();
        }
        var claims = new[] { ClaimAsync(first, "race-first"), ClaimAsync(second, "race-second") };
        start.SetResult();
        var results = await Task.WhenAll(claims).WaitAsync(TimeSpan.FromSeconds(20));
        var winner = Assert.Single(results.SelectMany(result => result));
        Assert.Equal(queueId, winner.SystemID);
        Assert.NotEqual(originalToken, winner.LeaseToken);
        Assert.Equal(1, winner.AttemptCount);
        string owner = await first.ExecuteScalarAsync<string>("SELECT LockedBy FROM dbo.AutomatedCallQueue WHERE SystemID=@queueId", new { queueId });
        Assert.Equal(results[0].Count == 1 ? "race-first" : "race-second", owner);
        Assert.Equal(winner.LeaseToken, await first.ExecuteScalarAsync<Guid>("SELECT LeaseToken FROM dbo.AutomatedCallQueue WHERE SystemID=@queueId", new { queueId }));
    }

    private sealed class SqlFixture(string connectionString, string oid) : IAsyncDisposable
    {
        public string Oid { get; } = oid;
        public int FlowId { get; private set; }

        public static async Task<SqlFixture> CreateAsync()
        {
            string connectionString = Environment.GetEnvironmentVariable(ConnectionVariable)
                ?? throw new InvalidOperationException("An owned scratch connection is required.");
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (builder.InitialCatalog != "NotificationWorkflowVoiceTests" ||
                !builder.DataSource.Equals(@"(localdb)\NotificationWorkflowVoiceTests", StringComparison.OrdinalIgnoreCase) || !builder.IntegratedSecurity)
                throw new InvalidOperationException("SQL race tests only permit the owned LocalDB instance/database with integrated security.");
            var fixture = new SqlFixture(connectionString, "rollout-" + Guid.NewGuid().ToString("N")[..12]);
            try
            {
                await using var connection = await fixture.OpenAsync();
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AutomatedCallQueue WHERE Status IN('READY','PROCESSING')"));
                await connection.ExecuteAsync("INSERT dbo.Client(OID) VALUES(@Oid)", new { fixture.Oid });
                fixture.FlowId = await connection.QuerySingleAsync<int>("INSERT dbo.CallFlowTemplates(Name,TemplateJSON,TwiML,CreatedBy) OUTPUT inserted.SystemID VALUES(@Oid,N'{}',N'<CallFlow/>',N'rollout-test')", new { fixture.Oid });
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<SqlConnection> OpenAsync()
        {
            var connection = new SqlConnection(connectionString);
            try { await connection.OpenAsync().WaitAsync(TimeSpan.FromSeconds(10)); return connection; }
            catch { await connection.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = await OpenAsync();
            await connection.ExecuteAsync("""
                DELETE dbo.AutomatedCallEvents WHERE CallId IN(SELECT SystemID FROM dbo.AutomatedCalls WHERE OID=@Oid);
                DELETE dbo.AutomatedCalls WHERE OID=@Oid;
                DELETE dbo.AutomatedCallQueue WHERE OID=@Oid;
                DELETE dbo.CallFlowTemplates WHERE SystemID=@FlowId;
                DELETE dbo.Client WHERE OID=@Oid;
                """, new { Oid, FlowId }, commandTimeout: 10);
        }
    }
}