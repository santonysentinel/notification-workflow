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
        catch (OperationCanceledException) when (ownership.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Auto-call recovery failed for queue {QueueId} with {FailureType}", job.SystemID, exception.GetType().Name);
            if (!ownership.IsCancellationRequested)
            {
                bool invalid = exception is ArgumentException or InvalidOperationException or JsonException or XmlException ||
                    exception is SqlException sql && sql.Number is 51000 or 51001 or 51003 ||
                    exception is AutoCallRoutingException routing && routing.Failure != AutoCallRoutingFailure.Unavailable;
                using var persistence = CancellationTokenSource.CreateLinkedTokenSource(ownership.Token);
                persistence.CancelAfter(TimeSpan.FromSeconds(5));
                try { await DisposeAsync(job, invalid ? "DEAD_LETTER" : "RETRY", invalid ? "VALIDATION_FAILED" : "PREPARATION_UNAVAILABLE", persistence.Token).ConfigureAwait(false); }
                catch (Exception failure) { logger.LogWarning("Auto-call disposition failed for queue {QueueId} with {FailureType}", job.SystemID, failure.GetType().Name); }
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
                    await ownership.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(options.RenewalSeconds), stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Auto-call ownership uncertain for queue {QueueId} with {FailureType}", job.SystemID, exception.GetType().Name);
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
                if (attempt.TwiML is not null || attempt.Parameters is not null) throw new InvalidOperationException("Incomplete preparation requires operator disposition.");
                var bundle = AutoCallBundlePreparer.Prepare(attempt, worker.FeedbackBaseUrl);
                int saved = await repository.SavePreparationAsync(attempt, worker.WorkerId, bundle, preparation.Token).ConfigureAwait(false);
                if (saved != attempt.CallId) throw new InvalidOperationException("Preparation identity changed.");
                attempt = await recovery.ReadRecoveryAsync(job, worker.WorkerId, preparation.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Prepared attempt was not retained.");
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
        catch (JsonException) { request = null; }
        if (request is null || request.CallId != attempt.CallId || request.To != attempt.PhoneE164)
        {
            await DisposeAsync(job, "DEAD_LETTER", "RECONCILIATION_IDENTITY_MISSING", cancellationToken).ConfigureAwait(false);
            return;
        }
        TwilioCallObservation observation;
        try { observation = await lookup.GetAsync(request, attempt.ProviderCallId, cancellationToken).ConfigureAwait(false); }
        catch (AutoCallReconciliationException)
        {
            await DisposeAsync(job, "WAIT", "RECONCILIATION_UNAVAILABLE", cancellationToken).ConfigureAwait(false);
            return;
        }
        await recovery.ObserveAsync(job, worker.WorkerId, attempt, observation, options.ReconcileSeconds, cancellationToken).ConfigureAwait(false);
    }

    private Task DisposeAsync(AutoCallJob job, string disposition, string errorCode, CancellationToken cancellationToken)
        => recovery.DispositionAsync(job, worker.WorkerId, disposition, errorCode,
            disposition == "RETRY" ? options.BackoffSeconds(job.RecoveryCount, Random.Shared.NextDouble()) : options.ReconcileSeconds, options, cancellationToken);
}