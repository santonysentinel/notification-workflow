using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Service.AutoCall;

namespace NotificationWorkflowService;

public sealed class AutoCallWorker(IAutoCallRepository repository, IAutoCallProcessingService processing, AutoCallWorkerOptions options, ILogger<AutoCallWorker> logger) : BackgroundService
{
    private readonly List<Task> inFlight = [];
    private readonly IAutoCallSubmissionService? submission;
    private readonly IAutoCallRecoveryService? recovery;

    public AutoCallWorker(IAutoCallRepository repository, IAutoCallProcessingService processing, AutoCallWorkerOptions options,
        ILogger<AutoCallWorker> logger, IAutoCallSubmissionService submission) : this(repository, processing, options, logger)
        => this.submission = submission;

    public AutoCallWorker(IAutoCallRepository repository, IAutoCallProcessingService processing, AutoCallWorkerOptions options,
        ILogger<AutoCallWorker> logger, IAutoCallSubmissionService submission, IAutoCallRecoveryService recovery)
        : this(repository, processing, options, logger, submission) => this.recovery = recovery;

    internal async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (int index = inFlight.Count - 1; index >= 0; index--)
        {
            if (!inFlight[index].IsCompleted) continue;
            await inFlight[index].ConfigureAwait(false);
            inFlight.RemoveAt(index);
        }
        int capacity = options.MaxConcurrency - inFlight.Count;
        if (capacity > 0)
        {
            var jobs = await repository.ClaimAsync(capacity, options.WorkerId, options.LeaseMinutes, cancellationToken).ConfigureAwait(false);
            if (jobs.Count > capacity) throw new InvalidOperationException("Claim procedure exceeded available preparation capacity.");
            foreach (var job in jobs) inFlight.Add(ProcessAsync(job, cancellationToken));
        }
        return inFlight.Count;
    }

    internal async Task WaitForCompletionAsync(CancellationToken cancellationToken)
        => await Task.WhenAny(inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunCycleAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogWarning("Auto-call claim failed with {FailureType}", exception.GetType().Name); }
                var poll = Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
                if (inFlight.Count == 0) await poll.ConfigureAwait(false);
                else await Task.WhenAny(poll, WaitForCompletionAsync(stoppingToken)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(inFlight).ConfigureAwait(false); }
    }

    private async Task ProcessAsync(AutoCallJob job, CancellationToken stoppingToken)
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
        catch (Exception exception)
        {
            logger.LogWarning("Auto-call preparation failed for queue {QueueId} with {FailureType}", job.SystemID, exception.GetType().Name);
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cleanup.CancelAfter(TimeSpan.FromSeconds(5));
            try { await repository.FailPreparationAsync(job, options.WorkerId, cleanup.Token).ConfigureAwait(false); }
            catch (Exception failure) { logger.LogWarning("Auto-call failure disposition failed for queue {QueueId} with {FailureType}", job.SystemID, failure.GetType().Name); }
            return;
        }
        if (submission is null) return;
        try
        {
            var outcome = await submission.SubmitAsync(attempt, options.WorkerId, stoppingToken).ConfigureAwait(false);
            logger.LogInformation("Auto-call attempt {CallId} submission {SubmissionState} for queue {QueueId}", attempt.CallId, outcome.SubmissionState, job.SystemID);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogWarning("Auto-call submission failed for attempt {CallId}, queue {QueueId} with {FailureType}", attempt.CallId, job.SystemID, exception.GetType().Name); }
    }
}