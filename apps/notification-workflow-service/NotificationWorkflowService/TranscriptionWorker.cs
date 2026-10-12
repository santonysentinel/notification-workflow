using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Service.Transcription;

namespace NotificationWorkflowService;

public sealed class TranscriptionWorker(ITranscriptionLifecycleRepository repository, ITranscriptionSubmissionService submissions,
    TranscriptionWorkerOptions options, TranscriptionOptions providers, ILogger<TranscriptionWorker> logger, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly List<Task> inFlight = [];

    internal async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled) return 0;
        options.Validate();
        for (int index = inFlight.Count - 1; index >= 0; index--)
        {
            if (!inFlight[index].IsCompleted) continue;
            await inFlight[index].ConfigureAwait(false);
            inFlight.RemoveAt(index);
        }
        await DatabaseAsync(token => repository.RecoverAsync(options.RecoveryBatchSize, token), cancellationToken).ConfigureAwait(false);
        int capacity = options.UploadConcurrency - inFlight.Count;
        if (capacity <= 0) return inFlight.Count;
        var claims = await DatabaseAsync(token => repository.ClaimAsync(capacity, options.WorkerId, options.LeaseSeconds,
            options.MaxOutstandingSubmissions, token), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (claims.Count > capacity) throw new InvalidOperationException("Transcription claims exceeded upload capacity.");
        foreach (var claim in claims)
        {
            if (claim.SystemID <= 0 || claim.LeaseToken == Guid.Empty) throw new InvalidOperationException("Invalid transcription claim identity.");
            inFlight.Add(ProcessAsync(claim, cancellationToken));
        }
        return inFlight.Count;
    }

    internal Task DrainAsync() => Task.WhenAll(inFlight);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        options.Validate();
        providers.Validate();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunCycleAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Transcription recovery or claim failed; retrying on the next poll."); }
                await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await DrainAsync().ConfigureAwait(false); }
    }

    private async Task ProcessAsync(TranscriptionClaim claim, CancellationToken stoppingToken)
    {
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ownership.Token);
        await using var expiry = clock.CreateTimer(_ => ownership.Cancel(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Task renewal = Task.CompletedTask;
        try
        {
            if (!await RenewAsync(claim, ownership, expiry).ConfigureAwait(false)) return;
            renewal = RenewLoopAsync(claim, ownership, expiry, renewalStop.Token);
            var work = await DatabaseAsync(token => repository.GetWorkAsync(claim, options.WorkerId, token), ownership.Token).ConfigureAwait(false);
            if (work is null) return;
            ownership.Token.ThrowIfCancellationRequested();
            if (work.JobId != claim.SystemID || work.LeaseToken != claim.LeaseToken) throw new InvalidOperationException("Transcription work fence changed.");
            if (work.SubmissionStatus is "SUBMITTING" or "ACCEPTED")
            {
                await DispositionAsync(claim, work, "WAIT", "RECOVERED_PENDING_SUBMISSION", false, ownership.Token).ConfigureAwait(false);
                return;
            }
            if (work.DurationSeconds == 0)
            {
                await DatabaseAsync(token => repository.CompleteEmptyAsync(claim, options.WorkerId, token), ownership.Token).ConfigureAwait(false);
                return;
            }
            ownership.Token.ThrowIfCancellationRequested();
            var result = await submissions.SubmitAsync(work, options.WorkerId, ownership.Token).ConfigureAwait(false);
            if (result.Outcome == "accepted" || ownership.IsCancellationRequested) return;
            var current = await DatabaseAsync(token => repository.GetWorkAsync(claim, options.WorkerId, token), ownership.Token).ConfigureAwait(false);
            if (current is null) return;
            bool sent = current.SubmissionStatus is "SUBMITTING" or "ACCEPTED";
            bool rejected = result.Failure == TranscriptionFailure.DefiniteRejection && current.SubmissionStatus == "SUBMITTING";
            string disposition = sent && !rejected ? "WAIT"
                : result.Failure == TranscriptionFailure.Validation || (rejected && !result.Retryable) ? "FAIL" : "RETRY";
            string errorCode = disposition == "WAIT" ? "ACCEPTANCE_UNCERTAIN"
                : disposition == "FAIL" ? "TRANSCRIPTION_REJECTED" : "TRANSCRIPTION_RETRY";
            await DispositionAsync(claim, current, disposition, errorCode, rejected, ownership.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ownership.IsCancellationRequested) { }
        catch (Exception) { logger.LogWarning("Transcription processing failed for job {JobId}; persisted work will be recovered.", claim.SystemID); }
        finally
        {
            renewalStop.Cancel();
            await renewal.ConfigureAwait(false);
        }
    }

    private Task<bool> DispositionAsync(TranscriptionClaim claim, TranscriptionWork work, string disposition, string errorCode, bool rejected, CancellationToken cancellationToken)
        => DatabaseAsync(token => repository.DispositionAsync(claim, options.WorkerId, work.CurrentSubmissionId,
            disposition, errorCode, rejected, token), cancellationToken);

    private async Task RenewLoopAsync(TranscriptionClaim claim, CancellationTokenSource ownership, ITimer expiry, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.RenewalSeconds), clock, cancellationToken).ConfigureAwait(false);
                if (!await RenewAsync(claim, ownership, expiry).ConfigureAwait(false)) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            ownership.Cancel();
            logger.LogWarning("Transcription lease renewal failed for job {JobId}.", claim.SystemID);
        }
    }

    private async Task<bool> RenewAsync(TranscriptionClaim claim, CancellationTokenSource ownership, ITimer expiry)
    {
        try
        {
            bool renewed = await DatabaseAsync(token => repository.RenewAsync(claim, options.WorkerId, options.LeaseSeconds, token), ownership.Token).ConfigureAwait(false);
            if (renewed && !ownership.IsCancellationRequested)
            {
                expiry.Change(TimeSpan.FromSeconds(options.LeaseSeconds - options.DatabaseTimeoutSeconds), Timeout.InfiniteTimeSpan);
                return true;
            }
        }
        catch (Exception) { }
        bool cancelled = ownership.IsCancellationRequested;
        ownership.Cancel();
        if (!cancelled) logger.LogWarning("Transcription lease ownership is uncertain for job {JobId}.", claim.SystemID);
        return false;
    }

    private async Task<T> DatabaseAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.DatabaseTimeoutSeconds), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        deadline.Token.ThrowIfCancellationRequested();
        return await operation(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    private Task DatabaseAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        => DatabaseAsync(async token => { await operation(token).ConfigureAwait(false); return true; }, cancellationToken);
}