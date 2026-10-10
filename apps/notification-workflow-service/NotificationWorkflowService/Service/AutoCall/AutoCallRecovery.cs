using Microsoft.Extensions.Configuration;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed class AutoCallRecoveryOptions
{
    public int RenewalSeconds { get; init; } = 60;
    public int MaxSubmissionAttempts { get; init; } = 3;
    public int MaxPreparationAttempts { get; init; } = 3;
    public int InitialBackoffSeconds { get; init; } = 30;
    public int MaxBackoffSeconds { get; init; } = 900;
    public int ReconcileSeconds { get; init; } = 120;
    public int MaxRecoveryHours { get; init; } = 24;

    public void Validate(AutoCallWorkerOptions worker)
    {
        if (RenewalSeconds < 1 || RenewalSeconds >= worker.LeaseMinutes * 60 / 2 || MaxSubmissionAttempts is < 1 or > 10 ||
            MaxPreparationAttempts is < 1 or > 10 || InitialBackoffSeconds is < 1 or > 900 || MaxBackoffSeconds < InitialBackoffSeconds ||
            MaxBackoffSeconds > 3600 || ReconcileSeconds is < 1 or > 3600 || MaxRecoveryHours is < 1 or > 168)
            throw new InvalidOperationException("Invalid AutoCall:Recovery timing or retry bounds.");
    }

    internal int BackoffSeconds(int count, double jitter)
    {
        if (count < 0 || jitter is < 0 or > 1 || double.IsNaN(jitter)) throw new ArgumentOutOfRangeException(nameof(count));
        double ceiling = Math.Min(MaxBackoffSeconds, InitialBackoffSeconds * Math.Pow(2, Math.Min(count, 30)));
        return Math.Max(1, (int)Math.Ceiling(ceiling * (0.5 + jitter * 0.5)));
    }

    internal static AutoCallRecoveryOptions Read(IConfiguration configuration, AutoCallWorkerOptions worker)
    {
        var options = configuration.GetSection("AutoCall:Recovery").Get<AutoCallRecoveryOptions>() ?? new();
        options.Validate(worker);
        return options;
    }
}

public sealed record TwilioCallObservation(string Sid, string Status, int? DurationSeconds)
{
    public bool Terminal => Status is "completed" or "busy" or "failed" or "no-answer" or "canceled";
    internal static bool ValidStatus(string? status) => status is "queued" or "initiated" or "ringing" or "in-progress" or "completed" or "busy" or "failed" or "no-answer" or "canceled";
}

public sealed class AutoCallReconciliationException() : Exception("Twilio call reconciliation did not return a validated observation.");

public interface ITwilioCallLookup
{
    Task<TwilioCallObservation> GetAsync(AutoCallSubmissionRequest request, string sid, CancellationToken cancellationToken);
}

public interface IAutoCallRecoveryRepository
{
    Task<bool> RenewAsync(AutoCallJob job, string workerId, int leaseMinutes, CancellationToken cancellationToken);
    Task<AutoCallAttempt?> ReadRecoveryAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken);
    Task DispositionAsync(AutoCallJob job, string workerId, string disposition, string errorCode, int delaySeconds, AutoCallRecoveryOptions options, CancellationToken cancellationToken);
    Task ObserveAsync(AutoCallJob job, string workerId, AutoCallAttempt attempt, TwilioCallObservation observation, int reconcileSeconds, CancellationToken cancellationToken);
}