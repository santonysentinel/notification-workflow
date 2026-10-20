using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using NotificationWorkflowService.Service.Transcription;

namespace NotificationWorkflowService;

public sealed class TranscriptionWorker(ITranscriptionLifecycleRepository repository, ITranscriptionSubmissionService submissions,
    TranscriptionWorkerOptions options, TranscriptionOptions providers, ILogger<TranscriptionWorker> logger, TimeProvider? timeProvider = null,
    TranscriptionMetrics? metrics = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly List<Task> inFlight = [];
    private long passNumber;

    internal async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        long pass = ++passNumber;
        var elapsed = Stopwatch.StartNew();
        logger.LogInformation("Transcription worker {WorkerId} starting pass {PassNumber}; in flight {InFlightJobs}",
            options.WorkerId, pass, inFlight.Count(task => !task.IsCompleted));
        for (int index = inFlight.Count - 1; index >= 0; index--)
        {
            if (!inFlight[index].IsCompleted) continue;
            await inFlight[index].ConfigureAwait(false);
            inFlight.RemoveAt(index);
        }
        if (metrics is not null)
        {
            try { await DatabaseAsync(metrics.RefreshAsync, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { TranscriptionDiagnostics.Failure(logger, "Queue metrics collection", exception); }
        }
        await DatabaseAsync(token => repository.RecoverAsync(options.RecoveryBatchSize, token), cancellationToken).ConfigureAwait(false);
        int capacity = options.UploadConcurrency - inFlight.Count;
        if (capacity <= 0)
        {
            logger.LogInformation("Transcription worker {WorkerId} pass {PassNumber}: read {ClaimedJobs} jobs; claim skipped because upload slots are full, in flight {InFlightJobs}, duration {ElapsedMilliseconds} ms",
                options.WorkerId, pass, 0, inFlight.Count(task => !task.IsCompleted), elapsed.ElapsedMilliseconds);
            return inFlight.Count;
        }
        var claims = await DatabaseAsync(token => repository.ClaimAsync(capacity, options.WorkerId, options.LeaseSeconds,
            options.MaxOutstandingSubmissions, token), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (claims.Count > capacity) throw new InvalidOperationException("Transcription claims exceeded upload capacity.");
        logger.LogInformation("Transcription worker {WorkerId} pass {PassNumber}: read {ClaimedJobs} jobs; claim capacity {ClaimCapacity}, in flight before processing {InFlightJobs}, duration {ElapsedMilliseconds} ms",
            options.WorkerId, pass, claims.Count, capacity, inFlight.Count(task => !task.IsCompleted), elapsed.ElapsedMilliseconds);
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
        options.Validate();
        providers.Validate();
        logger.LogInformation("Transcription worker {WorkerId} started; upload concurrency {UploadConcurrency}, outstanding limit {MaxOutstandingSubmissions}, poll {PollSeconds} seconds, lease {LeaseSeconds} seconds, renewal {RenewalSeconds} seconds, database timeout {DatabaseTimeoutSeconds} seconds",
            options.WorkerId, options.UploadConcurrency, options.MaxOutstandingSubmissions, options.PollSeconds,
            options.LeaseSeconds, options.RenewalSeconds, options.DatabaseTimeoutSeconds);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunCycleAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    TranscriptionDiagnostics.Failure(logger, "Recovery or claim (retrying on the next poll)", exception,
                        cancellationReason: exception is OperationCanceledException ? $"Database operation exceeded its {options.DatabaseTimeoutSeconds}-second deadline or dependency canceled." : null);
                }
                await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            logger.LogInformation("Transcription worker {WorkerId} stopping; draining {InFlightJobs} in-flight jobs", options.WorkerId, inFlight.Count(task => !task.IsCompleted));
            try { await DrainAsync().ConfigureAwait(false); }
            finally { logger.LogInformation("Transcription worker {WorkerId} stopped", options.WorkerId); }
        }
    }

    private async Task ProcessAsync(TranscriptionClaim claim, CancellationToken stoppingToken)
    {
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ownership.Token);
        await using var expiry = clock.CreateTimer(_ => ownership.Cancel(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Task renewal = Task.CompletedTask;
        int? recordingId = null;
        string stage = "Initial lease renewal";
        logger.LogInformation("Transcription worker {WorkerId} processing job {JobId}", options.WorkerId, claim.SystemID);
        try
        {
            if (!await RenewAsync(claim, ownership, expiry).ConfigureAwait(false)) return;
            renewal = RenewLoopAsync(claim, ownership, expiry, renewalStop.Token);
            stage = "Read work context";
            var work = await DatabaseAsync(token => repository.GetWorkAsync(claim, options.WorkerId, token), ownership.Token).ConfigureAwait(false);
            if (work is null)
            {
                logger.LogWarning("Transcription work context unavailable for job {JobId}; no submission attempted", claim.SystemID);
                return;
            }
            recordingId = work.RecordingId;
            logger.LogInformation(
                "Transcription work read for job {JobId}: recording {RecordingId}, submission {SubmissionId}, submission status {SubmissionStatus}, recording completed {RecordingCompleted}, Twilio source {TwilioSource}, duration {DurationSeconds} seconds, channels {Channels}, callback wait {CallbackWaitMinutes} minutes",
                work.JobId, work.RecordingId, work.CurrentSubmissionId,
                work.SubmissionStatus is "PREPARED" or "SUBMITTING" or "ACCEPTED" or "COMPLETED" or "FAILED" or "TIMED_OUT"
                    ? work.SubmissionStatus : work.SubmissionStatus is null ? null : "UNRECOGNIZED",
                work.RecordingStatus == "completed", string.Equals(work.Provider, "Twilio", StringComparison.OrdinalIgnoreCase),
                work.DurationSeconds, work.Channels, work.CallbackWaitMinutes);
            ownership.Token.ThrowIfCancellationRequested();
            if (work.JobId != claim.SystemID || work.LeaseToken != claim.LeaseToken) throw new InvalidOperationException("Transcription work fence changed.");
            if (work.SubmissionStatus is "SUBMITTING" or "ACCEPTED")
            {
                stage = "Disposition of pending submission";
                await DispositionAsync(claim, work, "WAIT", "RECOVERED_PENDING_SUBMISSION", false, ownership.Token).ConfigureAwait(false);
                return;
            }
            if (work.DurationSeconds == 0)
            {
                stage = "Complete empty recording";
                bool completed = await DatabaseAsync(token => repository.CompleteEmptyAsync(claim, options.WorkerId, token), ownership.Token).ConfigureAwait(false);
                logger.Log(completed ? LogLevel.Information : LogLevel.Warning,
                    "Transcription job {JobId}, recording {RecordingId}: empty-recording completion applied {Applied}", work.JobId, work.RecordingId, completed);
                return;
            }
            ownership.Token.ThrowIfCancellationRequested();
            stage = "Submission";
            var result = await submissions.SubmitAsync(work, options.WorkerId, ownership.Token).ConfigureAwait(false);
            logger.Log(result.Failure is null ? LogLevel.Information : LogLevel.Warning,
                "Transcription job {JobId}, recording {RecordingId} submission outcome {Outcome}, category {FailureCategory}, code {ErrorCode}, retryable {Retryable}",
                work.JobId, work.RecordingId, TranscriptionDiagnostics.Code(result.Outcome), result.Failure,
                TranscriptionDiagnostics.Code(result.ErrorCode), result.Retryable);
            if (result.Outcome == "accepted" || ownership.IsCancellationRequested) return;
            stage = "Read work context after submission";
            var current = await DatabaseAsync(token => repository.GetWorkAsync(claim, options.WorkerId, token), ownership.Token).ConfigureAwait(false);
            if (current is null)
            {
                logger.LogWarning("Transcription work context unavailable after submission for job {JobId}; persisted work will be recovered", claim.SystemID);
                return;
            }
            bool sent = current.SubmissionStatus is "SUBMITTING" or "ACCEPTED";
            bool rejected = result.Failure == TranscriptionFailure.DefiniteRejection && current.SubmissionStatus == "SUBMITTING";
            string disposition = sent && !rejected ? "WAIT"
                : result.Failure == TranscriptionFailure.Validation || (rejected && !result.Retryable) ? "FAIL" : "RETRY";
            string errorCode = disposition == "WAIT" ? "ACCEPTANCE_UNCERTAIN"
                : disposition == "FAIL" ? "TRANSCRIPTION_REJECTED" : "TRANSCRIPTION_RETRY";
            stage = "Persist disposition";
            await DispositionAsync(claim, current, disposition, errorCode, rejected, ownership.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ownership.IsCancellationRequested)
        {
            logger.LogInformation("Transcription job {JobId} canceled during {Operation}; reason {CancellationReason}",
                claim.SystemID, stage, stoppingToken.IsCancellationRequested ? "Worker stopping" : "Lease ownership lost or uncertain");
        }
        catch (Exception exception)
        {
            TranscriptionDiagnostics.Failure(logger, stage, exception, claim.SystemID, recordingId,
                exception is OperationCanceledException ? $"Database operation exceeded its {options.DatabaseTimeoutSeconds}-second deadline or dependency canceled." : null);
        }
        finally
        {
            renewalStop.Cancel();
            await renewal.ConfigureAwait(false);
            logger.LogInformation("Transcription worker {WorkerId} finished processing pass for job {JobId}", options.WorkerId, claim.SystemID);
        }
    }

    private async Task<bool> DispositionAsync(TranscriptionClaim claim, TranscriptionWork work, string disposition, string errorCode, bool rejected, CancellationToken cancellationToken)
    {
        bool applied = await DatabaseAsync(token => repository.DispositionAsync(claim, options.WorkerId, work.CurrentSubmissionId,
            disposition, errorCode, rejected, token), cancellationToken).ConfigureAwait(false);
        logger.Log(applied ? LogLevel.Information : LogLevel.Warning,
            "Transcription job {JobId}, recording {RecordingId} disposition {Disposition}, code {ErrorCode}, applied {Applied}",
            claim.SystemID, work.RecordingId, disposition, errorCode, applied);
        return applied;
    }

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
        catch (Exception exception)
        {
            ownership.Cancel();
            TranscriptionDiagnostics.Failure(logger, "Lease renewal loop", exception, claim.SystemID);
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
        catch (OperationCanceledException) when (ownership.IsCancellationRequested) { }
        catch (Exception exception)
        {
            TranscriptionDiagnostics.Failure(logger, "Lease renewal", exception, claim.SystemID,
                cancellationReason: exception is OperationCanceledException ? $"Lease renewal exceeded its {options.DatabaseTimeoutSeconds}-second database deadline or dependency canceled." : null);
        }
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