using ActiveAlarmsParser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NotificationWorkflowService.Service.Transcription;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class TranscriptionWorkerTests
{
    private const string Snapshot = "{\"model\":\"nova-3\",\"version\":\"latest\",\"language\":\"en\",\"utterances\":true,\"multichannel\":true,\"diarize\":false,\"punctuate\":false,\"smart_format\":false,\"mip_opt_out\":true}";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Finish(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    private static TranscriptionWorkerOptions Options(int concurrency = 1, int outstanding = 10) => new()
    { Enabled = true, UploadConcurrency = concurrency, MaxOutstandingSubmissions = outstanding, LeaseSeconds = 10, RenewalSeconds = 2, DatabaseTimeoutSeconds = 1, PollSeconds = 1 };
    private static TranscriptionOptions Providers() => new()
    {
        Enabled = true, DeepgramApiKey = "test-key",
        Identity = new() { BaseUrl = "https://identity.example", SignInName = "test", Password = "test", PermissionId = "1188" },
        Twilio = new() { AccountSid = "AC" + new string('1', 32), AuthToken = "test" }
    };
    private static TranscriptionWork Work(TranscriptionClaim claim, string? status = null, int duration = 1) => new()
    { JobId = claim.SystemID, LeaseToken = claim.LeaseToken, RecordingId = claim.SystemID, ConfigurationJSON = Snapshot, DurationSeconds = duration, CurrentSubmissionId = status is null ? null : 1, SubmissionStatus = status };

    [Fact]
    public async Task DisabledWorkerHasNoDatabaseOrProviderActivity()
    {
        var repository = new Lifecycle(1);
        var submission = new Submission(repository);
        using var worker = new TranscriptionWorker(repository, submission, new(), new(), new Logs());
        await worker.StartAsync(default);
        Assert.Equal(0, await worker.RunCycleAsync(default));
        Assert.Equal(0, repository.Recoveries);
        Assert.Equal(0, submission.Calls);
        Assert.False(new TranscriptionWorkerOptions().Enabled);
        Assert.Equal(1, new TranscriptionWorkerOptions().UploadConcurrency);
        Assert.Equal(10, new TranscriptionWorkerOptions().MaxOutstandingSubmissions);
        await worker.StopAsync(default);
    }

    [Theory]
    [InlineData("Normal", false, false)]
    [InlineData("Normal", true, false)]
    [InlineData("Normal", true, true)]
    [InlineData("Step", false, true)]
    [InlineData("Step", true, false)]
    [InlineData("Step", true, true)]
    public void RegistrationIsIndependentOfExistingWorkers(string mode, bool transcription, bool autoCall)
    {
        Type[] registered = [];
        using var host = Program.CreateHostBuilder(["--WorkerMode", mode, "--Transcription:Worker:Enabled", transcription.ToString(),
            "--AutoCall:Worker:Enabled", autoCall.ToString(), "--AutoCall:Worker:FeedbackBaseUrl", "https://feedback.example"])
            .ConfigureServices((_, services) => registered = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                .Select(descriptor => descriptor.ImplementationType!).ToArray()).Build();
        Assert.Equal(transcription, registered.Contains(typeof(TranscriptionWorker)));
        Assert.Equal(autoCall, registered.Contains(typeof(AutoCallWorker)));
        Assert.Contains(mode == "Normal" ? typeof(Worker) : typeof(StepWorker), registered);
    }

    [Theory]
    [InlineData(0, 10, 2)]
    [InlineData(101, 200, 2)]
    [InlineData(2, 1, 2)]
    [InlineData(1, 10001, 2)]
    [InlineData(1, 10, 8)]
    public void RejectsInvalidWorkerLimits(int concurrency, int outstanding, int renewal)
        => Assert.Throws<InvalidOperationException>(() => new TranscriptionWorkerOptions
        { UploadConcurrency = concurrency, MaxOutstandingSubmissions = outstanding, LeaseSeconds = 10, DatabaseTimeoutSeconds = 1, RenewalSeconds = renewal }.Validate());

    [Fact]
    public async Task BoundsUploadsAndReleasesSlotsWithoutWaitingForCallbacks()
    {
        var repository = new Lifecycle(1, 2, 3);
        var gate = Signal();
        var submission = new Submission(repository) { Handler = (_, token) => gate.Task.WaitAsync(token) };
        using var worker = new TranscriptionWorker(repository, submission, Options(2), Providers(), new Logs(), new FakeTimeProvider());
        Assert.Equal(2, await worker.RunCycleAsync(default));
        Assert.Equal(2, submission.Active);
        Assert.Equal(2, await worker.RunCycleAsync(default));
        Assert.Equal(2, submission.Calls);
        gate.SetResult();
        await Finish(worker.DrainAsync());
        Assert.Equal(2, repository.Outstanding);
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        Assert.Equal(3, submission.Calls);
        Assert.Equal(2, submission.Peak);
        Assert.All(repository.Capacities, capacity => Assert.InRange(capacity, 1, 2));
    }

    [Fact]
    public async Task BoundsOutstandingSeparatelyAndCallbackCompletionReleasesCapacity()
    {
        var repository = new Lifecycle(1, 2, 3);
        var submission = new Submission(repository);
        using var worker = new TranscriptionWorker(repository, submission, Options(outstanding: 2), Providers(), new Logs(), new FakeTimeProvider());
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        Assert.Equal(0, await worker.RunCycleAsync(default));
        Assert.Equal(2, submission.Calls);
        Assert.Equal(1, submission.Peak);
        repository.CompleteCallback(1);
        await worker.RunCycleAsync(default);
        Assert.Equal(3, submission.Calls);
        Assert.Equal(2, repository.Outstanding);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("exception")]
    [InlineData("timeout")]
    public async Task CancelsUploadAndAvoidsDispositionWhenRenewalBecomesUncertain(string failure)
    {
        var clock = new FakeTimeProvider();
        var renewing = Signal();
        var cancelled = Signal();
        var repository = new Lifecycle(1)
        {
            Renewal = (count, _) =>
            {
                if (count == 1) return Task.FromResult(true);
                renewing.TrySetResult();
                return failure == "false" ? Task.FromResult(false)
                    : failure == "exception" ? Task.FromException<bool>(new Exception("TOKEN credential transcript"))
                    : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }
        };
        var submission = new Submission(repository)
        {
            Handler = async (_, token) =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
        };
        var logs = new Logs();
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), logs, clock);
        await worker.RunCycleAsync(default);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Finish(renewing.Task);
        if (failure == "timeout") clock.Advance(TimeSpan.FromSeconds(1));
        await Finish(cancelled.Task);
        await Finish(worker.DrainAsync());
        Assert.Empty(repository.Dispositions);
        Assert.Equal(1, submission.Calls);
        Assert.DoesNotContain("TOKEN", string.Join('\n', logs.Messages));
    }

    [Fact]
    public async Task RenewsThroughoutUploadAndCancelsOnShutdown()
    {
        var clock = new FakeTimeProvider();
        var repository = new Lifecycle(1);
        var renewed = new[] { Signal(), Signal(), Signal() };
        repository.Renewal = (count, _) => { if (count > 1) renewed[count - 2].TrySetResult(); return Task.FromResult(true); };
        var cancelled = Signal();
        var submission = new Submission(repository)
        { Handler = async (_, token) => { try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { cancelled.TrySetResult(); throw; } } };
        using var stop = new CancellationTokenSource();
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs(), clock);
        await worker.RunCycleAsync(stop.Token);
        foreach (var signal in renewed)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await Finish(signal.Task);
            await Task.Yield();
        }
        Assert.Equal(1, submission.Active);
        stop.Cancel();
        await Finish(cancelled.Task);
        await Finish(worker.DrainAsync());
        Assert.Empty(repository.Dispositions);
    }

    [Fact]
    public async Task LeaseWatchdogStopsUploadAfterAnUnrenewedClockJump()
    {
        var clock = new FakeTimeProvider();
        var cancelled = Signal();
        var repository = new Lifecycle(1)
        { Renewal = (count, _) => count == 1 ? Task.FromResult(true) : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task };
        var submission = new Submission(repository)
        { Handler = async (_, token) => { try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { cancelled.TrySetResult(); throw; } } };
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs(), clock);
        await worker.RunCycleAsync(default);
        clock.Advance(TimeSpan.FromSeconds(9));
        await Finish(cancelled.Task);
        await Finish(worker.DrainAsync());
        Assert.Empty(repository.Dispositions);
        Assert.Equal(1, submission.Calls);
    }

    [Fact]
    public async Task InitialLeaseLossPreventsPreparationAndSubmission()
    {
        var repository = new Lifecycle(1) { Renewal = (_, _) => Task.FromResult(false) };
        var submission = new Submission(repository);
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs());
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        Assert.Equal(0, repository.WorkReads);
        Assert.Equal(0, submission.Calls);
    }

    [Fact]
    public async Task CancellationDuringClaimStartsNoPreparation()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new Lifecycle(1) { Claimed = () => cancellation.Cancel() };
        var submission = new Submission(repository);
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunCycleAsync(cancellation.Token));
        Assert.Equal(0, submission.Calls);
        Assert.Equal(0, repository.Renewals);
    }

    [Theory]
    [InlineData("SUBMITTING")]
    [InlineData("ACCEPTED")]
    public async Task RestartWaitsForPersistedPendingSubmissionInsteadOfResending(string status)
    {
        var repository = new Lifecycle(1) { ReadWork = (claim, _) => Work(claim, status) };
        var submission = new Submission(repository);
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs());
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        Assert.Equal("recover", repository.Events[0]);
        Assert.Equal("claim", repository.Events[1]);
        Assert.Equal(0, submission.Calls);
        Assert.Equal("WAIT", Assert.Single(repository.Dispositions).Disposition);
        Assert.Equal(1, repository.Dispositions[0].SubmissionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PREPARED")]
    public async Task RestartSubmitsOnlyUnsentWork(string? status)
    {
        var repository = new Lifecycle(1) { ReadWork = (claim, _) => Work(claim, status) };
        var submission = new Submission(repository);
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs());
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        Assert.Equal(1, submission.Calls);
        Assert.Empty(repository.Dispositions);
    }

    [Fact]
    public async Task CompletesZeroDurationLocallyWithoutProviderOrAttempt()
    {
        var repository = new Lifecycle(1) { ReadWork = (claim, _) => Work(claim, duration: 0) };
        var submission = new Submission(repository);
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs());
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        Assert.Equal(1, repository.EmptyCompletions);
        Assert.Equal(0, submission.Calls);
        Assert.Empty(repository.Dispositions);
    }

    [Theory]
    [InlineData(TranscriptionFailure.Validation, false, null, "FAIL", false)]
    [InlineData(TranscriptionFailure.TransientPreparation, true, null, "RETRY", false)]
    [InlineData(TranscriptionFailure.DefiniteRejection, true, "SUBMITTING", "RETRY", true)]
    [InlineData(TranscriptionFailure.DefiniteRejection, false, "SUBMITTING", "FAIL", true)]
    [InlineData(TranscriptionFailure.UncertainAcceptance, false, "SUBMITTING", "WAIT", false)]
    [InlineData(TranscriptionFailure.Persistence, false, "SUBMITTING", "WAIT", false)]
    [InlineData(TranscriptionFailure.Validation, false, "SUBMITTING", "WAIT", false)]
    [InlineData(TranscriptionFailure.DefiniteRejection, false, "ACCEPTED", "WAIT", false)]
    public async Task AppliesFencedDispositionWithoutRetryingUncertainSends(TranscriptionFailure failure, bool retryable, string? status, string disposition, bool definite)
    {
        var repository = new Lifecycle(1) { ReadWork = (claim, read) => Work(claim, read == 1 ? null : status) };
        var submission = new Submission(repository) { Result = new("submission-failed", Failure: failure, Retryable: retryable) };
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs());
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        var saved = Assert.Single(repository.Dispositions);
        Assert.Equal(disposition, saved.Disposition);
        Assert.Equal(definite, saved.Definite);
        Assert.Equal(status is null ? null : 1, saved.SubmissionId);
    }

    [Fact]
    public async Task EarlierCallbackOrLostFencePreventsWorkerDisposition()
    {
        var repository = new Lifecycle(1) { ReadWork = (claim, read) => read == 1 ? Work(claim) : null };
        var submission = new Submission(repository) { Result = new("acceptance-not-saved", Failure: TranscriptionFailure.Persistence) };
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs());
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        Assert.Empty(repository.Dispositions);
    }

    [Fact]
    public async Task HostedShutdownDrainsAcceptancePersistenceWithoutStartingMoreUploads()
    {
        var repository = new Lifecycle(1, 2);
        var started = Signal();
        var cancelled = Signal();
        var acceptanceSaved = Signal();
        var submission = new Submission(repository)
        {
            Handler = async (_, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                started.TrySetResult();
                await acceptanceSaved.Task;
            }
        };
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), new Logs(), new FakeTimeProvider());
        await worker.StartAsync(default);
        await Finish(started.Task);
        var stopping = worker.StopAsync(default);
        await Finish(cancelled.Task);
        Assert.False(stopping.IsCompleted);
        acceptanceSaved.SetResult();
        await Finish(stopping);
        Assert.Equal(1, submission.Calls);
        Assert.Equal(0, submission.Active);
        Assert.Empty(repository.Dispositions);
    }

    [Theory]
    [InlineData("recover")]
    [InlineData("claim")]
    public async Task HostedLoopContainsRoutinePollingFailures(string stage)
    {
        var recovered = Signal();
        var repository = new Lifecycle { Failure = stage, Recovered = recovered };
        var logs = new Logs();
        using var worker = new TranscriptionWorker(repository, new Submission(repository), Options(), Providers(), logs);
        await worker.StartAsync(default);
        await Finish(recovered.Task);
        await worker.StopAsync(default);
        Assert.True(repository.Recoveries >= 2);
        Assert.DoesNotContain("TOKEN", string.Join('\n', logs.Messages));
        Assert.Contains(logs.Messages, text => text.Contains("next poll", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("work")]
    [InlineData("disposition")]
    [InlineData("submission")]
    public async Task RoutineJobFailuresDoNotFaultWorkerTasks(string stage)
    {
        var repository = new Lifecycle(1) { Failure = stage };
        var submission = new Submission(repository)
        {
            Result = new("not-sent", Failure: TranscriptionFailure.TransientPreparation),
            Handler = (_, _) => stage == "submission" ? Task.FromException(new Exception("TOKEN transcript")) : Task.CompletedTask
        };
        var logs = new Logs();
        using var worker = new TranscriptionWorker(repository, submission, Options(), Providers(), logs);
        await worker.RunCycleAsync(default);
        await Finish(worker.DrainAsync());
        Assert.DoesNotContain("TOKEN", string.Join('\n', logs.Messages));
    }

    [Theory]
    [InlineData("media", 0, 0)]
    [InlineData("create", 1, 0)]
    [InlineData("begin", 1, 1)]
    public async Task CoordinatorStopsNewSideEffectsWhenOwnershipIsCancelled(string stage, int creates, int begins)
    {
        using var cancellation = new CancellationTokenSource();
        var steps = new CoordinatorStages(cancellation, stage);
        var service = new TranscriptionSubmissionService(steps, steps, steps, steps, Providers());
        try { await service.SubmitAsync(Work(new() { SystemID = 1, LeaseToken = Guid.NewGuid() }), "worker", cancellation.Token); }
        catch (OperationCanceledException) { }
        Assert.Equal(creates, steps.Creates);
        Assert.Equal(begins, steps.Begins);
        Assert.Equal(0, steps.Posts);
    }

    private sealed record DispositionRow(string Disposition, int? SubmissionId, bool Definite);

    private sealed class Lifecycle(params int[] jobs) : ITranscriptionLifecycleRepository
    {
        private readonly Queue<int> ready = new(jobs);
        private readonly HashSet<int> outstanding = [];
        public int Recoveries { get; private set; }
        public int Renewals { get; private set; }
        public int WorkReads { get; private set; }
        public int EmptyCompletions { get; private set; }
        public int Outstanding => outstanding.Count;
        public List<int> Capacities { get; } = [];
        public List<string> Events { get; } = [];
        public List<DispositionRow> Dispositions { get; } = [];
        public string Failure { get; set; } = "";
        public TaskCompletionSource? Recovered { get; init; }
        public Func<int, CancellationToken, Task<bool>>? Renewal { get; set; }
        public Func<TranscriptionClaim, int, TranscriptionWork?>? ReadWork { get; init; }
        public Action? Claimed { get; init; }
        public void CompleteCallback(int jobId) => outstanding.Remove(jobId);
        public Task RecoverAsync(int batchSize, CancellationToken cancellationToken)
        {
            Recoveries++; Events.Add("recover");
            if (Failure == "recover") { Failure = ""; throw new Exception("TOKEN transcript"); }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TranscriptionClaim>> ClaimAsync(int capacity, string workerId, int leaseSeconds, int maxOutstanding, CancellationToken cancellationToken)
        {
            Events.Add("claim"); Capacities.Add(capacity);
            if (Failure == "claim") { Failure = ""; throw new Exception("TOKEN transcript"); }
            Recovered?.TrySetResult();
            var claims = new List<TranscriptionClaim>();
            while (ready.Count > 0 && claims.Count < capacity && outstanding.Count < maxOutstanding)
            { int job = ready.Dequeue(); outstanding.Add(job); claims.Add(new() { SystemID = job, LeaseToken = Guid.NewGuid() }); }
            Claimed?.Invoke();
            return Task.FromResult<IReadOnlyList<TranscriptionClaim>>(claims);
        }
        public Task<bool> RenewAsync(TranscriptionClaim claim, string workerId, int leaseSeconds, CancellationToken cancellationToken)
        { Renewals++; return Renewal?.Invoke(Renewals, cancellationToken) ?? Task.FromResult(true); }
        public Task<TranscriptionWork?> GetWorkAsync(TranscriptionClaim claim, string workerId, CancellationToken cancellationToken)
        { WorkReads++; if (Failure == "work") throw new Exception("TOKEN transcript"); return Task.FromResult(ReadWork is null ? Work(claim) : ReadWork(claim, WorkReads)); }
        public Task<bool> CompleteEmptyAsync(TranscriptionClaim claim, string workerId, CancellationToken cancellationToken)
        { EmptyCompletions++; outstanding.Remove(claim.SystemID); return Task.FromResult(true); }
        public Task<bool> DispositionAsync(TranscriptionClaim claim, string workerId, int? submissionId, string disposition, string errorCode, bool definiteRejection, CancellationToken cancellationToken)
        {
            if (Failure == "disposition") throw new Exception("TOKEN transcript");
            Dispositions.Add(new(disposition, submissionId, definiteRejection));
            if (disposition != "WAIT") outstanding.Remove(claim.SystemID);
            return Task.FromResult(true);
        }
    }

    private sealed class Submission(Lifecycle repository) : ITranscriptionSubmissionService
    {
        private readonly object gate = new();
        public int Calls { get; private set; }
        public int Active { get; private set; }
        public int Peak { get; private set; }
        public TranscriptionResult Result { get; init; } = new("accepted");
        public Func<TranscriptionWork, CancellationToken, Task>? Handler { get; init; }
        public async Task<TranscriptionResult> SubmitAsync(TranscriptionWork work, string workerId, CancellationToken cancellationToken)
        {
            lock (gate) { Calls++; Active++; Peak = Math.Max(Peak, Active); }
            try { if (Handler is not null) await Handler(work, cancellationToken); return Result; }
            finally { lock (gate) Active--; }
        }
    }

    private sealed class CoordinatorStages(CancellationTokenSource ownership, string stage) : ICallbackTokenClient, ITwilioRecordingClient, IDeepgramClient, ITranscriptionSubmissionRepository
    {
        public int Creates { get; private set; }
        public int Begins { get; private set; }
        public int Posts { get; private set; }
        public Task<CallbackCredential> AcquireAsync(TimeSpan minimumValidity, CancellationToken cancellationToken) => Task.FromResult(new CallbackCredential("token", DateTime.UtcNow.AddHours(2)));
        public Task<TranscriptionAudio> OpenAsync(TranscriptionWork work, CancellationToken cancellationToken)
        { if (stage == "media") ownership.Cancel(); return Task.FromResult(new TranscriptionAudio(new MemoryStream([1]), 1)); }
        public Task<TranscriptionCreation> CreateAsync(TranscriptionWork work, string workerId, TranscriptionSubmissionIdentity identity, CallbackCredential credential, CancellationToken cancellationToken)
        { Creates++; if (stage == "create") ownership.Cancel(); return Task.FromResult(new TranscriptionCreation("created", 1)); }
        public Task<bool> BeginAsync(TranscriptionWork work, string workerId, TranscriptionSubmissionIdentity identity, CancellationToken cancellationToken)
        { Begins++; if (stage == "begin") ownership.Cancel(); return Task.FromResult(true); }
        public Task<string> SubmitAsync(TranscriptionAudio audio, string configurationJSON, TranscriptionSubmissionIdentity identity, CallbackCredential credential, CancellationToken cancellationToken)
        { Posts++; return Task.FromResult(Guid.NewGuid().ToString()); }
        public Task<string> SaveAcceptanceAsync(TranscriptionSubmissionIdentity identity, string requestId, CancellationToken cancellationToken) => Task.FromResult("saved");
    }

    private sealed class Logs : ILogger<TranscriptionWorker>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Messages) Messages.Add(formatter(state, exception)); }
    }
}