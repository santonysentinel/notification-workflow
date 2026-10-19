using System.Text.Json;
using System.Xml;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace NotificationWorkflowService.Service.AutoCall;

public interface IAutoCallRecoveryService
{
    Task ProcessAsync(AutoCallJob job, CancellationToken cancellationToken);
}

public sealed class AutoCallRecoveryService(IAutoCallRecoveryRepository recovery, IAutoCallRepository repository,
    IAutoCallProcessingService processing, IAutoCallSubmissionService submission, ITwilioCallLookup lookup,
    AutoCallRecoveryOptions options, AutoCallWorkerOptions worker, ILogger<AutoCallRecoveryService> logger) : IAutoCallRecoveryService
{
    public async Task ProcessAsync(AutoCallJob job, CancellationToken cancellationToken)
    {
        options.Validate(worker);
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = RenewAsync(job, ownership, heartbeatStop.Token);
        try
        {
            await ProcessOwnedAsync(job, ownership.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ownership.IsCancellationRequested)
        {
            logger.LogInformation("Auto-call processing canceled for queue {QueueId}; reason {CancellationReason}",
                job.SystemID, cancellationToken.IsCancellationRequested ? "Worker stopping." : "Lease ownership lost or uncertain.");
        }
        catch (Exception exception)
        {
            AutoCallDiagnostics.Failure(logger, "Recovery", exception, job.SystemID,
                cancellationReason: exception is OperationCanceledException
                    ? $"Dependency canceled or preparation deadline exceeded ({worker.PreparationTimeoutSeconds} seconds)." : null);
            if (!ownership.IsCancellationRequested)
            {
                bool invalid = exception is ArgumentException or InvalidOperationException or JsonException or XmlException ||
                    exception is SqlException sql && sql.Number is 51000 or 51001 or 51003 ||
                    exception is AutoCallRoutingException routing && routing.Failure != AutoCallRoutingFailure.Unavailable;
                using var persistence = CancellationTokenSource.CreateLinkedTokenSource(ownership.Token);
                persistence.CancelAfter(TimeSpan.FromSeconds(5));
                try { await DisposeAsync(job, invalid ? "DEAD_LETTER" : "RETRY", invalid ? "VALIDATION_FAILED" : "PREPARATION_UNAVAILABLE", persistence.Token).ConfigureAwait(false); }
                catch (Exception failure) { AutoCallDiagnostics.Failure(logger, "Recovery disposition", failure, job.SystemID); }
            }
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
    }

    private async Task RenewAsync(AutoCallJob job, CancellationTokenSource ownership, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                if (!await recovery.RenewAsync(job, worker.WorkerId, worker.LeaseMinutes, deadline.Token).ConfigureAwait(false))
                {
                    logger.LogWarning("Auto-call lease renewal refused for queue {QueueId}; ownership lost, canceling processing", job.SystemID);
                    await ownership.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(options.RenewalSeconds), stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AutoCallDiagnostics.Failure(logger, "Lease renewal (ownership uncertain)", exception, job.SystemID,
                cancellationReason: exception is OperationCanceledException ? "Lease renewal canceled or exceeded its 5-second deadline." : null,
                level: LogLevel.Warning);
            await ownership.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessOwnedAsync(AutoCallJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = await recovery.ReadRecoveryAsync(job, worker.WorkerId, cancellationToken).ConfigureAwait(false);
        if (attempt is not null && (attempt.ProviderCallId is not null || attempt.SubmissionState is not ("READY" or "REJECTED")))
        {
            await ReconcileAsync(job, attempt, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (attempt?.SubmissionState == "REJECTED" && attempt.SubmissionErrorCode != "TWILIO_20429")
        {
            await DisposeAsync(job, "DEAD_LETTER", "SUBMISSION_REJECTED", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (job.SubmissionCount >= Math.Min(job.MaxAttempts, options.MaxSubmissionAttempts))
        {
            await DisposeAsync(job, "DEAD_LETTER", "RETRY_EXHAUSTED", cancellationToken).ConfigureAwait(false);
            return;
        }
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        preparation.CancelAfter(TimeSpan.FromSeconds(worker.PreparationTimeoutSeconds));
        if (attempt is null) attempt = await processing.PrepareAsync(job, worker.WorkerId, preparation.Token).ConfigureAwait(false);
        else
        {
            if (attempt.SubmissionState == "REJECTED")
                attempt = await repository.BeginAttemptAsync(job, worker.WorkerId, attempt.PhoneE164, preparation.Token).ConfigureAwait(false);
            if (!attempt.IsPrepared)
            {
                if (attempt.TwiML is not null || attempt.Parameters is not null) throw new AutoCallValidationException("Incomplete preparation requires operator disposition.");
                var bundle = AutoCallBundlePreparer.Prepare(attempt, worker.FeedbackBaseUrl);
                int saved = await repository.SavePreparationAsync(attempt, worker.WorkerId, bundle, preparation.Token).ConfigureAwait(false);
                if (saved != attempt.CallId) throw new AutoCallValidationException("Preparation identity changed.");
                attempt = await recovery.ReadRecoveryAsync(job, worker.WorkerId, preparation.Token).ConfigureAwait(false)
                    ?? throw new AutoCallValidationException("Prepared attempt was not retained.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = await submission.SubmitAsync(attempt, worker.WorkerId, cancellationToken).ConfigureAwait(false);
        if (outcome.SubmissionState == "REJECTED")
        {
            var rejected = await recovery.ReadRecoveryAsync(job, worker.WorkerId, cancellationToken).ConfigureAwait(false);
            bool retry = rejected?.SubmissionErrorCode == "TWILIO_20429";
            await DisposeAsync(job, retry ? "RETRY" : "DEAD_LETTER", retry ? "TWILIO_20429" : "SUBMISSION_REJECTED", cancellationToken).ConfigureAwait(false);
        }
        else await DisposeAsync(job, "WAIT", outcome.SubmissionState == "ACCEPTED" ? "AWAITING_TERMINAL_STATUS" : "SUBMISSION_UNRESOLVED", cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileAsync(AutoCallJob job, AutoCallAttempt attempt, CancellationToken cancellationToken)
    {
        if (attempt.ProviderCallId is null)
        {
            await DisposeAsync(job, attempt.SubmissionState is "SUBMITTING" or "UNKNOWN" ? "WAIT" : "DEAD_LETTER", "SUBMISSION_UNRESOLVED", cancellationToken).ConfigureAwait(false);
            return;
        }
        AutoCallSubmissionRequest? request;
        try { request = JsonSerializer.Deserialize<AutoCallSubmissionRequest>(attempt.SubmissionRequestJSON ?? "null"); }
        catch (JsonException exception)
        {
            AutoCallDiagnostics.Failure(logger, "Read persisted reconciliation request", exception, job.SystemID, attempt.CallId);
            request = null;
        }
        if (request is null || request.CallId != attempt.CallId || request.To != attempt.PhoneE164)
        {
            logger.LogError("Auto-call reconciliation identity missing or mismatched for queue {QueueId}, call {CallId}", job.SystemID, attempt.CallId);
            await DisposeAsync(job, "DEAD_LETTER", "RECONCILIATION_IDENTITY_MISSING", cancellationToken).ConfigureAwait(false);
            return;
        }
        TwilioCallObservation observation;
        try { observation = await lookup.GetAsync(request, attempt.ProviderCallId, cancellationToken).ConfigureAwait(false); }
        catch (AutoCallReconciliationException exception)
        {
            AutoCallDiagnostics.Failure(logger, "Reconciliation lookup", exception, job.SystemID, attempt.CallId);
            await DisposeAsync(job, "WAIT", "RECONCILIATION_UNAVAILABLE", cancellationToken).ConfigureAwait(false);
            return;
        }
        await recovery.ObserveAsync(job, worker.WorkerId, attempt, observation, options.ReconcileSeconds, cancellationToken).ConfigureAwait(false);
    }

    private async Task DisposeAsync(AutoCallJob job, string disposition, string errorCode, CancellationToken cancellationToken)
    {
        await recovery.DispositionAsync(job, worker.WorkerId, disposition, errorCode,
            disposition == "RETRY" ? options.BackoffSeconds(job.RecoveryCount, Random.Shared.NextDouble()) : options.ReconcileSeconds, options, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Auto-call queue {QueueId} disposition {Disposition}, reason {ErrorCode}", job.SystemID, disposition, errorCode);
    }
}