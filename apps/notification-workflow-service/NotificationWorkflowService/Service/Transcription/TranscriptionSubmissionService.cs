using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationWorkflowService.Repository;

namespace NotificationWorkflowService.Service.Transcription;

public sealed class TranscriptionSubmissionService(ITranscriptionSubmissionRepository repository, ICallbackTokenClient tokens,
    ITwilioRecordingClient media, IDeepgramClient provider, TranscriptionOptions options) : ITranscriptionSubmissionService
{
    public async Task<TranscriptionResult> SubmitAsync(TranscriptionWork work, string workerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled) return new("disabled");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.SubmissionTimeoutSeconds));
        var identity = new TranscriptionSubmissionIdentity(work.JobId, Guid.NewGuid(), Guid.NewGuid());
        try
        {
            options.Validate();
            if (work.JobId <= 0 || work.LeaseToken == Guid.Empty || string.IsNullOrWhiteSpace(workerId) || workerId.Length > 100 ||
                work.CallbackWaitMinutes is < 1 or > 1440)
                throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_WORK_CONTEXT");
            _ = DeepgramClient.SnapshotOptions(work.ConfigurationJSON, options.RequireModelImprovementOptOut);
            var credential = await tokens.AcquireAsync(TimeSpan.FromMinutes(work.CallbackWaitMinutes) + TimeSpan.FromSeconds(options.SubmissionTimeoutSeconds + 5), deadline.Token).ConfigureAwait(false);
            await using var audio = await media.OpenAsync(work, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var creation = await repository.CreateAsync(work, workerId, identity, credential, deadline.Token).ConfigureAwait(false);
            if (creation.Outcome is not ("created" or "duplicate")) return new(creation.Outcome);
            if (creation.SubmissionId is null or <= 0) return new("persistence-failed", Failure: TranscriptionFailure.Persistence, ErrorCode: "INVALID_SUBMISSION_IDENTITY");
            deadline.Token.ThrowIfCancellationRequested();
            if (!await repository.BeginAsync(work, workerId, identity, deadline.Token).ConfigureAwait(false)) return new("not-sent");
            string requestId;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                requestId = await provider.SubmitAsync(audio, work.ConfigurationJSON, identity, credential, deadline.Token).ConfigureAwait(false);
            }
            catch (TranscriptionException failure)
            {
                return new("submission-failed", Failure: failure.Failure, ErrorCode: failure.ErrorCode, Retryable: failure.Retryable);
            }
            catch (Exception)
            {
                return new("submission-failed", Failure: TranscriptionFailure.UncertainAcceptance, ErrorCode: "ACCEPTANCE_UNKNOWN");
            }
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                string outcome = await repository.SaveAcceptanceAsync(identity, requestId, persistence.Token).ConfigureAwait(false);
                return outcome is "saved" or "duplicate" ? new("accepted", requestId)
                    : new("acceptance-not-saved", requestId, TranscriptionFailure.Persistence, "ACCEPTANCE_NOT_SAVED");
            }
            catch (Exception)
            {
                return new("acceptance-not-saved", requestId, TranscriptionFailure.Persistence, "ACCEPTANCE_NOT_SAVED");
            }
        }
        catch (TranscriptionException failure)
        { return new("not-sent", Failure: failure.Failure, ErrorCode: failure.ErrorCode, Retryable: failure.Retryable); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException)
        { return new("not-sent", Failure: TranscriptionFailure.TransientPreparation, ErrorCode: "PREPARATION_TIMEOUT", Retryable: true); }
        catch (Exception)
        { return new("not-sent", Failure: TranscriptionFailure.Persistence, ErrorCode: "SUBMISSION_PREPARATION_FAILED"); }
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
        services.AddTransient<TranscriptionSubmissionService>();
        services.AddTransient<ITranscriptionSubmissionService>(provider => provider.GetRequiredService<TranscriptionSubmissionService>());
        services.AddSingleton(provider => TranscriptionWorkerOptions.Read(provider.GetRequiredService<IConfiguration>()));
        return services;
    }
}