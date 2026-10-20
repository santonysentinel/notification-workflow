using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationWorkflowService.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NotificationWorkflowService.Service.Transcription;

public sealed class TranscriptionSubmissionService(ITranscriptionSubmissionRepository repository, ICallbackTokenClient tokens,
    ITwilioRecordingClient media, IDeepgramClient provider, TranscriptionOptions options,
    ILogger<TranscriptionSubmissionService>? logger = null) : ITranscriptionSubmissionService
{
    private readonly ILogger<TranscriptionSubmissionService> diagnosticsLogger = logger ?? NullLogger<TranscriptionSubmissionService>.Instance;

    public async Task<TranscriptionResult> SubmitAsync(TranscriptionWork work, string workerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled) return new("disabled");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.SubmissionTimeoutSeconds));
        var identity = new TranscriptionSubmissionIdentity(work.JobId, Guid.NewGuid(), Guid.NewGuid());
        string stage = "Validate submission";
        try
        {
            options.Validate();
            if (work.JobId <= 0 || work.LeaseToken == Guid.Empty || string.IsNullOrWhiteSpace(workerId) || workerId.Length > 100 ||
                work.CallbackWaitMinutes is < 1 or > 1440)
                throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_WORK_CONTEXT");
            _ = DeepgramClient.SnapshotOptions(work.ConfigurationJSON, options.RequireModelImprovementOptOut, diagnosticsLogger);
            stage = "Acquire callback credential";
            var credential = await tokens.AcquireAsync(TimeSpan.FromMinutes(work.CallbackWaitMinutes) + TimeSpan.FromSeconds(options.SubmissionTimeoutSeconds + 5), deadline.Token).ConfigureAwait(false);
            stage = "Open recording media";
            await using var audio = await media.OpenAsync(work, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            stage = "Create submission";
            var creation = await repository.CreateAsync(work, workerId, identity, credential, deadline.Token).ConfigureAwait(false);
            if (creation.Outcome is not ("created" or "duplicate"))
            {
                diagnosticsLogger.LogWarning("Transcription submission creation for job {JobId}, recording {RecordingId} returned {Outcome}; no upload attempted",
                    work.JobId, work.RecordingId, TranscriptionDiagnostics.Code(creation.Outcome));
                return new(creation.Outcome);
            }
            if (creation.SubmissionId is null or <= 0)
            {
                diagnosticsLogger.LogError("Transcription submission identity invalid for job {JobId}, recording {RecordingId}; no upload attempted", work.JobId, work.RecordingId);
                return new("persistence-failed", Failure: TranscriptionFailure.Persistence, ErrorCode: "INVALID_SUBMISSION_IDENTITY");
            }
            deadline.Token.ThrowIfCancellationRequested();
            stage = "Begin submission";
            if (!await repository.BeginAsync(work, workerId, identity, deadline.Token).ConfigureAwait(false))
            {
                diagnosticsLogger.LogWarning("Transcription submission marker not acquired for job {JobId}, recording {RecordingId}; no upload attempted", work.JobId, work.RecordingId);
                return new("not-sent");
            }
            string requestId;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                requestId = await provider.SubmitAsync(audio, work.ConfigurationJSON, identity, credential, deadline.Token).ConfigureAwait(false);
            }
            catch (TranscriptionException failure)
            {
                TranscriptionDiagnostics.Failure(diagnosticsLogger, "Provider upload", failure, work.JobId, work.RecordingId);
                return new("submission-failed", Failure: failure.Failure, ErrorCode: failure.ErrorCode, Retryable: failure.Retryable);
            }
            catch (Exception exception)
            {
                TranscriptionDiagnostics.Failure(diagnosticsLogger, "Provider upload (acceptance uncertain)", exception, work.JobId, work.RecordingId,
                    exception is OperationCanceledException ? "Upload interrupted; provider acceptance is uncertain." : null);
                return new("submission-failed", Failure: TranscriptionFailure.UncertainAcceptance, ErrorCode: "ACCEPTANCE_UNKNOWN");
            }
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                string outcome = await repository.SaveAcceptanceAsync(identity, requestId, persistence.Token).ConfigureAwait(false);
                diagnosticsLogger.Log(outcome is "saved" or "duplicate" ? LogLevel.Information : LogLevel.Error,
                    "Transcription acceptance persistence for job {JobId}, recording {RecordingId}, submission {SubmissionId}: {Outcome}",
                    work.JobId, work.RecordingId, creation.SubmissionId, TranscriptionDiagnostics.Code(outcome));
                return outcome is "saved" or "duplicate" ? new("accepted", requestId)
                    : new("acceptance-not-saved", requestId, TranscriptionFailure.Persistence, "ACCEPTANCE_NOT_SAVED");
            }
            catch (Exception exception)
            {
                TranscriptionDiagnostics.Failure(diagnosticsLogger, "Persist provider acceptance", exception, work.JobId, work.RecordingId,
                    exception is OperationCanceledException ? "Acceptance persistence exceeded its 5-second deadline or dependency canceled." : null);
                return new("acceptance-not-saved", requestId, TranscriptionFailure.Persistence, "ACCEPTANCE_NOT_SAVED");
            }
        }
        catch (TranscriptionException failure)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, stage, failure, work.JobId, work.RecordingId);
            return new("not-sent", Failure: failure.Failure, ErrorCode: failure.ErrorCode, Retryable: failure.Retryable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, stage, exception, work.JobId, work.RecordingId,
                $"Preparation exceeded its {options.SubmissionTimeoutSeconds}-second deadline or dependency canceled.");
            return new("not-sent", Failure: TranscriptionFailure.TransientPreparation, ErrorCode: "PREPARATION_TIMEOUT", Retryable: true);
        }
        catch (Exception exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, stage, exception, work.JobId, work.RecordingId);
            return new("not-sent", Failure: TranscriptionFailure.Persistence, ErrorCode: "SUBMISSION_PREPARATION_FAILED");
        }
    }
}

public static class TranscriptionRegistration
{
    public static IServiceCollection AddTranscriptionClients(this IServiceCollection services)
    {
        services.AddSingleton(provider => TranscriptionOptions.Read(provider.GetRequiredService<IConfiguration>()));
        foreach (string name in new[] { CallbackTokenClient.ClientName, TwilioRecordingClient.ClientName, DeepgramClient.ClientName })
            services.AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan)
                .RemoveAllLoggers()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                    AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(30),
                    MaxResponseHeadersLength = 32
                });
        services.AddSingleton<ICallbackTokenClient, CallbackTokenClient>();
        services.AddSingleton<ITwilioRecordingClient, TwilioRecordingClient>();
        services.AddSingleton<IDeepgramClient, DeepgramClient>();
        services.AddSingleton<TranscriptionSubmissionRepository>();
        services.AddSingleton<ITranscriptionSubmissionRepository>(provider => provider.GetRequiredService<TranscriptionSubmissionRepository>());
        services.AddSingleton<ITranscriptionLifecycleRepository>(provider => provider.GetRequiredService<TranscriptionSubmissionRepository>());
        services.AddSingleton<ITranscriptionMetricsRepository>(provider => provider.GetRequiredService<TranscriptionSubmissionRepository>());
        services.AddSingleton(provider => TranscriptionMetricsOptions.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<TranscriptionMetrics>();
        services.AddTransient<TranscriptionSubmissionService>();
        services.AddTransient<ITranscriptionSubmissionService>(provider => provider.GetRequiredService<TranscriptionSubmissionService>());
        services.AddSingleton(provider => TranscriptionWorkerOptions.Read(provider.GetRequiredService<IConfiguration>()));
        return services;
    }
}