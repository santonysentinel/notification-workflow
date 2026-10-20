using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NotificationWorkflowService.Service.Transcription;

public sealed class TranscriptionQueueSnapshot
{
    public long ReadyJobs { get; init; }
    public long QueueAgeSeconds { get; init; }
    public long OutstandingSubmissions { get; init; }
    public long CallbackTimeoutSubmissions { get; init; }
    public long DeadLetterJobs { get; init; }
}

public interface ITranscriptionMetricsRepository
{
    Task<TranscriptionQueueSnapshot> GetMetricsAsync(CancellationToken cancellationToken);
}

public sealed class TranscriptionMetricsOptions
{
    public bool Enabled { get; init; } = true;
    public int RefreshSeconds { get; init; } = 60;
    public int MaxSampleAgeSeconds { get; init; } = 120;
    internal void Validate()
    {
        if (RefreshSeconds is < 1 or > 3600 || MaxSampleAgeSeconds < RefreshSeconds || MaxSampleAgeSeconds > 7200)
            throw new InvalidOperationException("Invalid Transcription:Metrics refresh/sample-age settings.");
    }
    internal static TranscriptionMetricsOptions Read(IConfiguration configuration)
        => configuration.GetSection("Transcription:Metrics").Get<TranscriptionMetricsOptions>() ?? new();
}

public sealed class TranscriptionMetrics : IDisposable
{
    public const string MeterName = "NotificationWorkflowService.Transcription";
    private readonly Meter meter = new(MeterName, "1.0.0");
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly ITranscriptionMetricsRepository repository;
    private readonly TranscriptionMetricsOptions options;
    private readonly TimeProvider clock;
    private readonly Counter<long> failures;
    private readonly ILogger<TranscriptionMetrics> logger;
    private Sample? sample;
    private DateTimeOffset? lastAttempt;
    private sealed record Sample(TranscriptionQueueSnapshot Snapshot, DateTimeOffset At);

    public TranscriptionMetrics(ITranscriptionMetricsRepository repository, TranscriptionMetricsOptions options, TimeProvider? timeProvider = null,
        ILogger<TranscriptionMetrics>? logger = null)
    {
        options.Validate();
        this.repository = repository;
        this.options = options;
        this.logger = logger ?? NullLogger<TranscriptionMetrics>.Instance;
        clock = timeProvider ?? TimeProvider.System;
        failures = meter.CreateCounter<long>("transcription.metrics.collection_failures", "{failure}");
        meter.CreateObservableGauge("transcription.queue.ready", () => Observe(snapshot => snapshot.ReadyJobs), "{job}");
        meter.CreateObservableGauge("transcription.queue.oldest_age", () => Observe(snapshot => snapshot.QueueAgeSeconds, true), "s");
        meter.CreateObservableGauge("transcription.submissions.outstanding", () => Observe(snapshot => snapshot.OutstandingSubmissions), "{job}");
        meter.CreateObservableGauge("transcription.callbacks.timed_out_unresolved", () => Observe(snapshot => snapshot.CallbackTimeoutSubmissions), "{submission}");
        meter.CreateObservableGauge("transcription.jobs.dead_letter", () => Observe(snapshot => snapshot.DeadLetterJobs), "{job}");
        meter.CreateObservableGauge("transcription.metrics.sample_age", () => ObserveAge(), "s");
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (lastAttempt is not null && now - lastAttempt.Value < TimeSpan.FromSeconds(options.RefreshSeconds)) return;
            lastAttempt = now;
            try
            {
                var snapshot = await repository.GetMetricsAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref sample, new Sample(snapshot, clock.GetUtcNow()));
                logger.LogInformation("Transcription queue metrics refreshed: ready {ReadyJobs}, oldest queue age {QueueAgeSeconds} seconds, outstanding submissions {OutstandingSubmissions}, callback timeouts {CallbackTimeoutSubmissions}, dead letters {DeadLetterJobs}",
                    snapshot.ReadyJobs, snapshot.QueueAgeSeconds, snapshot.OutstandingSubmissions, snapshot.CallbackTimeoutSubmissions, snapshot.DeadLetterJobs);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                failures.Add(1);
                TranscriptionDiagnostics.Failure(logger, "Refresh queue metrics", exception);
                throw new TranscriptionException(TranscriptionFailure.Persistence, "METRICS_UNAVAILABLE");
            }
        }
        finally { refreshLock.Release(); }
    }

    private IEnumerable<Measurement<long>> Observe(Func<TranscriptionQueueSnapshot, long> value, bool age = false)
    {
        var current = Volatile.Read(ref sample);
        if (!options.Enabled || current is null) return [];
        long elapsed = Math.Max(0, (long)(clock.GetUtcNow() - current.At).TotalSeconds);
        if (elapsed > options.MaxSampleAgeSeconds) return [];
        long result = Math.Max(0, value(current.Snapshot));
        if (age && current.Snapshot.ReadyJobs > 0) result += elapsed;
        return [new Measurement<long>(result)];
    }

    private IEnumerable<Measurement<long>> ObserveAge()
    {
        var current = Volatile.Read(ref sample);
        return !options.Enabled || current is null ? [] : [new Measurement<long>(Math.Max(0, (long)(clock.GetUtcNow() - current.At).TotalSeconds))];
    }

    public void Dispose() { meter.Dispose(); refreshLock.Dispose(); }
}