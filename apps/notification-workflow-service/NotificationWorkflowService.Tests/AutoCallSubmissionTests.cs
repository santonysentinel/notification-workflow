using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.AutoCall;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class AutoCallSubmissionTests
{
    private const string Account = "ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sid = "CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static AutoCallTwilioOptions Options(bool enabled = true, bool record = false, int seconds = 30) => new()
    { Enabled = enabled, AccountSid = Account, AuthToken = "test-secret", FromNumber = "+15557654321", RecordCalls = record, TimeoutSeconds = seconds };
    private static AutoCallWorkerOptions Worker() => new() { FeedbackBaseUrl = "https://feedback.example/prefix/", WorkerId = "worker" };
    private static AutoCallAttempt Attempt() => new()
    {
        CallId = 42, QueueId = 7, QueueLeaseToken = Guid.NewGuid(), PhoneE164 = "+15551234567",
        TwiML = "<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say>Alex &amp; Co</Say><Redirect>https://feedback.example/prefix/webhooks/twilio/voice/next?callId=42&amp;executionId={{executionId}}</Redirect></Response></Step><Step id=\"end\"><Response><Hangup/></Response></Step></CallFlow>",
        Parameters = "{\"callId\":42,\"recipient\":\"+15551234567\",\"feedbackBaseUrl\":\"https://feedback.example/prefix\"}"
    };
    private static AutoCallSubmissionRequest Request(bool record = false) => new(42, Account, "+15551234567", "+15557654321",
        "https://feedback.example/prefix/webhooks/twilio/voice/start?callId=42",
        "https://feedback.example/prefix/webhooks/twilio/voice/status?callId=42",
        "https://feedback.example/prefix/webhooks/twilio/recordings/status?callId=42", record);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Count++; return send(request, token); }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        { Assert.Equal("AutoCallTwilio", name); return new HttpClient(handler, false); }
    }
    private static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.Created, string? body = null)
        => new(status) { Content = new StringContent(body ?? $"{{\"sid\":\"{Sid}\",\"account_sid\":\"{Account}\",\"status\":\"completed\"}}", Encoding.UTF8, "application/json") };

    [Fact]
    public void OpeningResponseUsesInitialStepAndOneExecutionIdentityWithoutTheBundle()
    {
        var opening = AutoCallBundlePreparer.PrepareOpening("<CallFlow initialStep=\"hello\"><Step id=\"other\"><Response><Hangup/></Response></Step><Step id=\"hello\"><Response><Say>Alex &amp; Co</Say><Redirect>https://feedback.example/next?executionId={{executionId}}</Redirect></Response></Step></CallFlow>");
        Assert.Equal("hello", opening.StepId);
        Assert.NotEqual(Guid.Empty, opening.ExecutionId);
        Assert.Contains(opening.ExecutionId.ToString("D"), opening.ResponseTwiML);
        Assert.Contains("Alex &amp; Co", opening.ResponseTwiML);
        Assert.DoesNotContain("CallFlow", opening.ResponseTwiML);
        Assert.DoesNotContain("{{", opening.ResponseTwiML);
        Assert.DoesNotContain("Hangup", opening.ResponseTwiML);
    }

    [Theory]
    [InlineData("<Response><Hangup/></Response>")]
    [InlineData("<CallFlow initialStep=\"missing\"><Step id=\"hello\"><Response/></Step></CallFlow>")]
    [InlineData("<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response/></Step><Step id=\"hello\"><Response/></Step></CallFlow>")]
    [InlineData("<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say>{{name}}</Say></Response></Step></CallFlow>")]
    public void InvalidOpeningStepsFailBeforeSubmission(string xml)
        => Assert.Throws<AutoCallValidationException>(() => AutoCallBundlePreparer.PrepareOpening(xml));

    [Fact]
    public void OpeningResponseRejectsDtd()
        => Assert.Throws<System.Xml.XmlException>(() => AutoCallBundlePreparer.PrepareOpening("<!DOCTYPE CallFlow [<!ENTITY secret 'text'>]><CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say>&secret;</Say></Response></Step></CallFlow>"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendsPinnedAuthenticatedFormAndAllCallbacksWithoutUsingResponseLifecycle(bool record)
    {
        using var handler = new Handler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{Account}/Calls.json", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", message.Headers.Authorization!.Scheme);
            Assert.Equal(Account + ":test-secret", Encoding.UTF8.GetString(Convert.FromBase64String(message.Headers.Authorization.Parameter!)));
            Assert.Equal("application/x-www-form-urlencoded", message.Content!.Headers.ContentType!.MediaType);
            var pairs = (await message.Content.ReadAsStringAsync(token)).Split('&').Select(pair => pair.Split('=', 2))
                .Select(pair => new KeyValuePair<string, string>(WebUtility.UrlDecode(pair[0]), WebUtility.UrlDecode(pair[1]))).ToList();
            Assert.Contains(new("To", "+15551234567"), pairs);
            Assert.Contains(new("From", "+15557654321"), pairs);
            Assert.Contains(new("Url", Request().Url), pairs);
            Assert.Contains(new("StatusCallback", Request().StatusCallback), pairs);
            Assert.Contains(new("RecordingStatusCallback", Request().RecordingStatusCallback), pairs);
            foreach (string name in new[] { "Method", "StatusCallbackMethod", "RecordingStatusCallbackMethod" }) Assert.Contains(new(name, "POST"), pairs);
            Assert.Equal(new[] { "initiated", "ringing", "answered", "completed" }, pairs.Where(pair => pair.Key == "StatusCallbackEvent").Select(pair => pair.Value));
            Assert.Contains(new("Record", record ? "true" : "false"), pairs);
            return Response();
        });
        Assert.Equal(Sid, await new TwilioCallClient(new Factory(handler), Options(record: record)).CreateAsync(Request(record), default));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task InlineSubmissionSendsOnlyOpeningResponseAndNoStartUrl()
    {
        var opening = AutoCallBundlePreparer.PrepareOpening(Attempt().TwiML!);
        using var handler = new Handler(async (message, token) =>
        {
            var pairs = (await message.Content!.ReadAsStringAsync(token)).Split('&').Select(pair => pair.Split('=', 2))
                .Select(pair => new KeyValuePair<string, string>(WebUtility.UrlDecode(pair[0]), WebUtility.UrlDecode(pair[1]))).ToList();
            Assert.Contains(new("Twiml", opening.ResponseTwiML), pairs);
            Assert.DoesNotContain(pairs, pair => pair.Key is "Url" or "Method");
            Assert.Contains(new("StatusCallback", Request().StatusCallback), pairs);
            Assert.Contains(new("RecordingStatusCallback", Request().RecordingStatusCallback), pairs);
            Assert.DoesNotContain("{{", pairs.Single(pair => pair.Key == "Twiml").Value);
            return Response();
        });
        Assert.Equal(Sid, await new TwilioCallClient(new Factory(handler), Options()).CreateAsync(Request() with { OpeningStep = opening }, default));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(429, false)]
    [InlineData(408, false)]
    [InlineData(500, false)]
    [InlineData(302, false)]
    [InlineData(200, false)]
    public async Task ProviderFailureIsClassifiedWithoutRetryOrBodyLeak(int status, bool rejected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response((HttpStatusCode)status, "private-provider-error")));
        var exception = await Assert.ThrowsAsync<TwilioSubmissionException>(() => new TwilioCallClient(new Factory(handler), Options()).CreateAsync(Request(), default));
        Assert.Equal(rejected, exception.Rejected);
        Assert.Equal(status is 400 or 401 or 429 ? "ACCEPTANCE_UNKNOWN" : "HTTP_" + status, exception.ErrorCode);
        Assert.DoesNotContain("private-provider-error", exception.ToString());
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"sid\":\"CAinvalid\",\"account_sid\":\"ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData("{\"sid\":\"CAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"account_sid\":\"ACbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}")]
    public async Task InvalidAcceptanceIsUnknownAndNeverRetried(string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(body: body)));
        var exception = await Assert.ThrowsAsync<TwilioSubmissionException>(() => new TwilioCallClient(new Factory(handler), Options()).CreateAsync(Request(), default));
        Assert.False(exception.Rejected);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task NetworkFailureAndOversizedResponseAreUnknown()
    {
        foreach (bool oversized in new[] { false, true })
        {
            var logger = new AutoCallDiagnosticsTests.Logs<TwilioCallClient>();
            using var handler = new Handler((_, _) => oversized ? Task.FromResult(Response(body: new string('x', 65537))) : Task.FromException<HttpResponseMessage>(new HttpRequestException("private network details")));
            var exception = await Assert.ThrowsAsync<TwilioSubmissionException>(() => new TwilioCallClient(new Factory(handler), Options(), logger).CreateAsync(Request(), default));
            Assert.False(exception.Rejected);
            Assert.DoesNotContain("private network details", exception.ToString());
            Assert.Equal(1, handler.Count);
            Assert.NotEmpty(logger.Entries);
            Assert.All(logger.Entries, entry => Assert.DoesNotContain("private network details", entry.Message));
            Assert.Contains(logger.Entries, entry => entry.Properties.TryGetValue("FailureType", out var type) &&
                Equals(typeof(HttpRequestException).FullName, type));
        }
    }

    [Fact]
    public async Task HttpDeadlineAndHostCancellationAreUnknownOncePostBegins()
    {
        foreach (bool cancelHost in new[] { false, true })
        {
            var logger = new AutoCallDiagnosticsTests.Logs<TwilioCallClient>();
            using var stopping = new CancellationTokenSource();
            using var handler = new Handler(async (_, token) =>
            {
                if (cancelHost) stopping.Cancel();
                await new TaskCompletionSource().Task.WaitAsync(token);
                return Response();
            });
            var exception = await Assert.ThrowsAsync<TwilioSubmissionException>(() => new TwilioCallClient(new Factory(handler), Options(seconds: 1), logger).CreateAsync(Request(), stopping.Token));
            Assert.False(exception.Rejected);
            Assert.Equal(1, handler.Count);
            Assert.Contains(logger.Entries, entry => entry.Properties.TryGetValue("CancellationReason", out var reason) &&
                reason is string text && text.Contains(cancelHost ? "Caller canceled" : "deadline exceeded"));
        }
    }

    [Fact]
    public void RegisteredTransportDoesNotFollowRedirectsOrCarryCookies()
    {
        var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).AddAutoCallContacts();
        using var provider = services.BuildServiceProvider();
        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("AutoCallTwilio");
        while (handler is DelegatingHandler wrapper) handler = wrapper.InnerHandler!;
        var transport = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseCookies);
    }

    private sealed class Repository : IAutoCallSubmissionRepository
    {
        public List<string> Calls { get; } = [];
        public bool ShouldSubmit { get; set; } = true;
        public bool PersistOpening { get; set; } = true;
        public string? State { get; private set; }
        public string? SavedSid { get; private set; }
        public AutoCallSubmissionRequest? Request { get; private set; }
        public Guid Marker { get; private set; }
        public bool FailSave { get; set; }
        public string DurableState { get; private set; } = "READY";
        public Task<AutoCallSubmissionResult> BeginSubmissionAsync(AutoCallAttempt attempt, string workerId, Guid token, AutoCallSubmissionRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("marker");
            bool authorize = ShouldSubmit && DurableState == "READY";
            if (authorize) { Request = request; Marker = token; DurableState = "SUBMITTING"; }
            return Task.FromResult(new AutoCallSubmissionResult { ShouldSubmit = authorize, SubmissionToken = Marker,
                OpeningExecutionId = PersistOpening ? Request?.OpeningStep?.ExecutionId : null, SubmissionState = ShouldSubmit ? DurableState : "ACCEPTED" });
        }
        public Task<AutoCallSubmissionResult> SaveSubmissionAsync(AutoCallAttempt attempt, Guid token, string state, string? sid, string? errorCode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.Equal(Marker, token);
            Calls.Add("save");
            if (FailSave) throw new InvalidOperationException("private SQL failure");
            State = state; SavedSid = sid; DurableState = state;
            return Task.FromResult(new AutoCallSubmissionResult { SubmissionState = state, ProviderCallId = sid, SubmissionToken = token });
        }
    }
    private sealed class Client(Repository repository, Func<CancellationToken, Task<string>>? send = null) : ITwilioCallClient
    {
        public Task<string> CreateAsync(AutoCallSubmissionRequest request, CancellationToken cancellationToken)
        { repository.Calls.Add("http"); return send?.Invoke(cancellationToken) ?? Task.FromResult(Sid); }
    }

    [Fact]
    public async Task PersistsMarkerBeforePostAndSavesAcceptanceSeparately()
    {
        var repository = new Repository();
        var result = await new AutoCallSubmissionService(repository, new Client(repository), Options(), Worker()).SubmitAsync(Attempt(), "worker", default);
        Assert.Equal(new[] { "marker", "http", "save" }, repository.Calls);
        Assert.Equal("ACCEPTED", result.SubmissionState);
        Assert.Equal(Sid, repository.SavedSid);
        Assert.Equal(Request(), repository.Request! with { OpeningStep = null });
        Assert.Equal("hello", repository.Request!.OpeningStep!.StepId);
        Assert.NotEqual(Guid.Empty, repository.Request.OpeningStep.ExecutionId);
        Assert.Contains(repository.Request.OpeningStep.ExecutionId.ToString("D"), repository.Request.OpeningStep.ResponseTwiML);
    }

    [Fact]
    public async Task MissingPersistedOpeningIdentityPreventsPostWithAnOldDatabaseProcedure()
    {
        var repository = new Repository { PersistOpening = false };
        var service = new AutoCallSubmissionService(repository, new Client(repository), Options(), Worker());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(Attempt(), "worker", default));
        Assert.Equal(new[] { "marker" }, repository.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledAndAlreadyMarkedAttemptsNeverSend(bool disabled)
    {
        var repository = new Repository { ShouldSubmit = false };
        await new AutoCallSubmissionService(repository, new Client(repository), Options(!disabled), Worker()).SubmitAsync(Attempt(), "worker", default);
        Assert.Equal(disabled ? Array.Empty<string>() : new[] { "marker" }, repository.Calls);
    }

    [Theory]
    [InlineData(true, "REJECTED")]
    [InlineData(false, "UNKNOWN")]
    public async Task FailedPostPersistsSubmissionOutcomeOnly(bool rejected, string state)
    {
        var repository = new Repository();
        await new AutoCallSubmissionService(repository, new Client(repository, _ => Task.FromException<string>(new TwilioSubmissionException(rejected, "HTTP_500"))), Options(), Worker()).SubmitAsync(Attempt(), "worker", default);
        Assert.Equal(state, repository.State);
        Assert.Null(repository.SavedSid);
        Assert.Equal(new[] { "marker", "http", "save" }, repository.Calls);
    }

    [Fact]
    public async Task UnexpectedPostFailureIsLoggedBeforePersistingUnknownAcceptance()
    {
        var logger = new AutoCallDiagnosticsTests.Logs<AutoCallSubmissionService>();
        var repository = new Repository();
        var client = new Client(repository, _ => Task.FromException<string>(new HttpRequestException("secret provider request")));
        var result = await new AutoCallSubmissionService(repository, client, Options(), Worker(), logger)
            .SubmitAsync(Attempt(), "worker", default);
        var entry = Assert.Single(logger.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error));
        Assert.Equal("Twilio submission (acceptance unknown)", entry.Properties["Operation"]);
        Assert.DoesNotContain("secret provider request", entry.Message);
        Assert.Equal("UNKNOWN", result.SubmissionState);
        Assert.Equal(new[] { "marker", "http", "save" }, repository.Calls);
    }

    [Fact]
    public async Task CancellationAfterPostStillPersistsUncertainOutcomeWithIndependentBoundedToken()
    {
        var repository = new Repository();
        using var cancellation = new CancellationTokenSource();
        await new AutoCallSubmissionService(repository, new Client(repository, token => { cancellation.Cancel(); return Task.FromCanceled<string>(token); }), Options(), Worker())
            .SubmitAsync(Attempt(), "worker", cancellation.Token);
        Assert.Equal("UNKNOWN", repository.State);
        Assert.Equal(new[] { "marker", "http", "save" }, repository.Calls);
    }

    [Fact]
    public async Task AcceptedResponseAfterHostCancellationStillSavesSid()
    {
        var repository = new Repository();
        using var stopping = new CancellationTokenSource();
        await new AutoCallSubmissionService(repository, new Client(repository, _ => { stopping.Cancel(); return Task.FromResult(Sid); }), Options(), Worker())
            .SubmitAsync(Attempt(), "worker", stopping.Token);
        Assert.Equal("ACCEPTED", repository.State);
        Assert.Equal(Sid, repository.SavedSid);
    }

    private sealed class Preparation : IAutoCallRepository, IAutoCallProcessingService
    {
        private bool claimed;
        public bool FailPreparation { get; set; }
        public bool FailedDisposition { get; private set; }
        public Task<IReadOnlyList<AutoCallJob>> ClaimAsync(int capacity, string workerId, int minutes, CancellationToken token)
        {
            IReadOnlyList<AutoCallJob> jobs = claimed ? [] : [new() { SystemID = 7 }]; claimed = true;
            return Task.FromResult(jobs);
        }
        public Task<AutoCallAttempt> PrepareAsync(AutoCallJob job, string workerId, CancellationToken token)
            => FailPreparation ? Task.FromException<AutoCallAttempt>(new InvalidOperationException()) : Task.FromResult(Attempt());
        public Task<AutoCallAttempt> BeginAttemptAsync(AutoCallJob job, string workerId, string recipient, CancellationToken token) => throw new NotSupportedException();
        public Task<int> SavePreparationAsync(AutoCallAttempt attempt, string workerId, AutoCallPreparedBundle bundle, CancellationToken token) => throw new NotSupportedException();
        public Task FailPreparationAsync(AutoCallJob job, string workerId, CancellationToken token)
        { FailedDisposition = true; return Task.CompletedTask; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerSubmitsOnlyPreparedAttemptsAndNeverUsesPreparationCleanupForSubmissionFailure(bool preparationFails)
    {
        var preparation = new Preparation { FailPreparation = preparationFails };
        var repository = new Repository { FailSave = true };
        var service = new AutoCallSubmissionService(repository, new Client(repository), Options(), Worker());
        using var worker = new AutoCallWorker(preparation, preparation, Worker(), NullLogger<AutoCallWorker>.Instance, service);
        await worker.RunCycleAsync(default);
        await worker.WaitForCompletionAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(preparationFails, preparation.FailedDisposition);
        Assert.Equal(preparationFails ? Array.Empty<string>() : new[] { "marker", "http", "save" }, repository.Calls);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotTriggerAnotherPostAndPreCancellationDoesNotMark()
    {
        var repository = new Repository { FailSave = true };
        var service = new AutoCallSubmissionService(repository, new Client(repository), Options(), Worker());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(Attempt(), "worker", default));
        Assert.Equal(new[] { "marker", "http", "save" }, repository.Calls);
        Assert.Equal("SUBMITTING", repository.DurableState);
        Assert.Null(repository.SavedSid);
        Guid originalMarker = repository.Marker;
        repository.Calls.Clear();
        repository.FailSave = false;
        var replay = await service.SubmitAsync(Attempt(), "worker", default);
        Assert.False(replay.ShouldSubmit);
        Assert.Equal("SUBMITTING", replay.SubmissionState);
        Assert.Equal(originalMarker, replay.SubmissionToken);
        Assert.Equal(new[] { "marker" }, repository.Calls);
        repository.Calls.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SubmitAsync(Attempt(), "worker", new CancellationToken(true)));
        Assert.Empty(repository.Calls);
    }

    [Fact]
    public void RepositoryCommandsRetainImmutableSubmissionIdentityWithoutLiveLeaseGuardOnSave()
    {
        var attempt = Attempt(); Guid marker = Guid.NewGuid();
        var begin = AutoCallRepository.BeginSubmissionCommand(attempt, "worker", marker, Request(), default);
        var fields = (DynamicParameters)begin.Parameters!;
        Assert.Equal(attempt.QueueLeaseToken, fields.Get<Guid>("@LeaseToken"));
        Assert.Equal(marker, fields.Get<Guid>("@SubmissionToken"));
        Assert.DoesNotContain("test-secret", fields.Get<string>("@RequestJSON"));
        var save = AutoCallRepository.SaveSubmissionCommand(attempt, marker, "ACCEPTED", Sid, null, default);
        fields = (DynamicParameters)save.Parameters!;
        Assert.Equal(Sid, fields.Get<string>("@ProviderCallId"));
        Assert.DoesNotContain("WorkerId", fields.ParameterNames);
        Assert.Equal(30, save.CommandTimeout);
    }

    [Fact]
    public void OptInRequiresValidCredentialsAndWorkerConfiguration()
    {
        Options(false).Validate(new());
        Options().Validate(Worker());
        Assert.Throws<InvalidOperationException>(() => Options().Validate(new()));
        Assert.Throws<InvalidOperationException>(() => new AutoCallTwilioOptions { Enabled = true }.Validate(Worker()));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoCall:Worker:FeedbackBaseUrl"] = "https://feedback.example"
        }).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddAutoCallContacts();
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<AutoCallTwilioOptions>().Enabled);
        Assert.IsType<AutoCallSubmissionService>(provider.GetRequiredService<IAutoCallSubmissionService>());
    }
}