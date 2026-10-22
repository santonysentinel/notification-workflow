using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using NotificationWorkflowService.Service.AutoCall;

namespace NotificationWorkflowService;

public sealed class AutoCallWorker(IAutoCallRepository repository, IAutoCallProcessingService processing, AutoCallWorkerOptions options, ILogger<AutoCallWorker> logger) : BackgroundService
{
    private readonly List<Task> inFlight = [];
    private readonly IAutoCallSubmissionService? submission;
    private readonly IAutoCallRecoveryService? recovery;
    private long passNumber;

    public AutoCallWorker(IAutoCallRepository repository, IAutoCallProcessingService processing, AutoCallWorkerOptions options,
        ILogger<AutoCallWorker> logger, IAutoCallSubmissionService submission) : this(repository, processing, options, logger)
        => this.submission = submission;

    public AutoCallWorker(IAutoCallRepository repository, IAutoCallProcessingService processing, AutoCallWorkerOptions options,
        ILogger<AutoCallWorker> logger, IAutoCallSubmissionService submission, IAutoCallRecoveryService recovery)
        : this(repository, processing, options, logger, submission) => this.recovery = recovery;

    internal async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long pass = ++passNumber;
        var elapsed = Stopwatch.StartNew();
        for (int index = inFlight.Count - 1; index >= 0; index--)
        {
            if (!inFlight[index].IsCompleted) continue;
            await inFlight[index].ConfigureAwait(false);
            inFlight.RemoveAt(index);
        }
        int capacity = options.MaxConcurrency - inFlight.Count;
        AutoCallQueueStatus? status = null;
        try { status = await repository.ReadQueueStatusAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            AutoCallDiagnostics.Failure(logger, "Queue status (counts unavailable; continuing claim)", exception);
        }
        int claimed = 0;
        if (capacity > 0)
        {
            var jobs = await repository.ClaimAsync(capacity, options.WorkerId, options.LeaseMinutes, cancellationToken).ConfigureAwait(false);
            if (jobs.Count > capacity) throw new InvalidOperationException("Claim procedure exceeded available preparation capacity.");
            claimed = jobs.Count;
            foreach (var job in jobs) inFlight.Add(ProcessAsync(job, cancellationToken));
        }
        int active = inFlight.Count(task => !task.IsCompleted);
        logger.LogInformation(
            "Auto-call worker {WorkerId} pass {PassNumber}: queue status available {QueueStatusAvailable}; queue before claim waiting {WaitingJobs}, due {DueJobs}, scheduled {ScheduledJobs}, processing {ProcessingJobs}, expired leases {ExpiredLeaseJobs}; claimed {ClaimedJobs}, in flight {InFlightJobs}, available slots {AvailableSlots}, duration {ElapsedMilliseconds} ms",
            options.WorkerId, pass, status is not null, status?.WaitingJobs, status?.DueJobs, status?.ScheduledJobs,
            status?.ProcessingJobs, status?.ExpiredLeaseJobs, claimed, active, options.MaxConcurrency - active, elapsed.ElapsedMilliseconds);
        return inFlight.Count;
    }

    internal async Task WaitForCompletionAsync(CancellationToken cancellationToken)
        => await Task.WhenAny(inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        logger.LogInformation(
            "Auto-call worker {WorkerId} started; max concurrency {MaxConcurrency}, poll interval {PollSeconds} seconds, lease {LeaseMinutes} minutes, preparation timeout {PreparationTimeoutSeconds} seconds, recovery {RecoveryEnabled}",
            options.WorkerId, options.MaxConcurrency, options.PollSeconds, options.LeaseMinutes, options.PreparationTimeoutSeconds, recovery is not null);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunCycleAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { AutoCallDiagnostics.Failure(logger, "Claim", exception); }
                var poll = Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
                if (inFlight.Count == 0) await poll.ConfigureAwait(false);
                else await Task.WhenAny(poll, WaitForCompletionAsync(stoppingToken)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            logger.LogInformation("Auto-call worker {WorkerId} stopping; waiting for {InFlightJobs} in-flight jobs",
                options.WorkerId, inFlight.Count(task => !task.IsCompleted));
            try { await Task.WhenAll(inFlight).ConfigureAwait(false); }
            finally { logger.LogInformation("Auto-call worker {WorkerId} stopped", options.WorkerId); }
        }
    }

    private async Task ProcessAsync(AutoCallJob job, CancellationToken stoppingToken)
    {
        logger.LogInformation("Auto-call worker {WorkerId} processing queue {QueueId}", options.WorkerId, job.SystemID);
        try { await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false); }
        finally { logger.LogInformation("Auto-call worker {WorkerId} finished processing pass for queue {QueueId}", options.WorkerId, job.SystemID); }
    }

    private async Task ProcessJobAsync(AutoCallJob job, CancellationToken stoppingToken)
    {
        if (recovery is not null)
        {
            await recovery.ProcessAsync(job, stoppingToken).ConfigureAwait(false);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.PreparationTimeoutSeconds));
        AutoCallAttempt attempt;
        try
        {
            attempt = await processing.PrepareAsync(job, options.WorkerId, deadline.Token).ConfigureAwait(false);
            logger.LogInformation("Auto-call attempt {CallId} prepared for queue {QueueId}", attempt.CallId, job.SystemID);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (AutoCallDeviceSkippedException skipped)
        {
            logger.LogInformation("Auto-call queue {QueueId} skipped call device {PhoneType}: no phone number on file", job.SystemID, skipped.PhoneType);
            return;
        }
        catch (Exception exception)
        {
            AutoCallDiagnostics.Failure(logger, "Preparation", exception, job.SystemID,
                cancellationReason: deadline.IsCancellationRequested ? "Preparation deadline exceeded." : null);
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cleanup.CancelAfter(TimeSpan.FromSeconds(5));
            try { await repository.FailPreparationAsync(job, options.WorkerId, cleanup.Token).ConfigureAwait(false); }
            catch (Exception failure) { AutoCallDiagnostics.Failure(logger, "Preparation failure disposition", failure, job.SystemID); }
            return;
        }
        if (submission is null) return;
        try
        {
            var outcome = await submission.SubmitAsync(attempt, options.WorkerId, stoppingToken).ConfigureAwait(false);
            logger.LogInformation("Auto-call attempt {CallId} submission {SubmissionState} for queue {QueueId}", attempt.CallId, outcome.SubmissionState, job.SystemID);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { AutoCallDiagnostics.Failure(logger, "Submission", exception, job.SystemID, attempt.CallId); }
    }
}