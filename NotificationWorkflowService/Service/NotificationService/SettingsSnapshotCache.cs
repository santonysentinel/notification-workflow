using Microsoft.Extensions.Logging;

namespace ActiveAlarmsParser.Service.NotificationService;

/// <summary>
/// Owns a complete cache snapshot. Loaders must build new snapshots rather than mutate
/// published ones. Refreshes are serialized; readers never observe partially mapped rows.
/// </summary>
internal sealed class SettingsSnapshotCache<T> where T : class
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly Func<T> load;
    private readonly ILogger logger;
    private readonly string name;
    private readonly TimeProvider clock;
    private T snapshot;
    private long lastSuccessfulLoad;
    private long lastFailure;
    private bool hasSuccessfulLoad;
    private bool failed;

    internal SettingsSnapshotCache(Func<T> load, T emptySnapshot, ILogger logger, string name,
        TimeProvider? timeProvider = null)
    {
        this.load = load;
        snapshot = emptySnapshot;
        this.logger = logger;
        this.name = name;
        clock = timeProvider ?? TimeProvider.System;
    }

    internal T GetSnapshot()
    {
        lock (gate)
        {
            long now = clock.GetTimestamp();
            if (failed && clock.GetElapsedTime(lastFailure, now) < FailureRetryInterval)
                return snapshot;
            if (!failed && hasSuccessfulLoad && clock.GetElapsedTime(lastSuccessfulLoad, now) < Lifetime)
                return snapshot;

            try
            {
                T next = load() ?? throw new InvalidOperationException("Settings loader returned a null snapshot.");
                // Publish only after every row has been mapped successfully, even if empty.
                snapshot = next;
                lastSuccessfulLoad = clock.GetTimestamp();
                hasSuccessfulLoad = true;
                failed = false;
            }
            catch (Exception exception)
            {
                lastFailure = clock.GetTimestamp();
                failed = true;
                logger.LogError(exception, "Settings cache {CacheName} refresh failed; retaining previous snapshot and retrying after {RetrySeconds} seconds",
                    name, FailureRetryInterval.TotalSeconds);
            }
            return snapshot;
        }
    }
}