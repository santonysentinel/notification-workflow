using Microsoft.Extensions.Configuration;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed class AutoCallJob
{
    public int SystemID { get; init; }
    public int ActiveAlarmID { get; init; }
    public string OID { get; init; } = "";
    public int FlowId { get; init; }
    public string VariablesJSON { get; init; } = "";
    public Guid EnqueueIdempotencyKey { get; init; }
    public int AttemptCount { get; init; }
    public int MaxAttempts { get; init; }
    public int SubmissionCount { get; set; }
    public int RecoveryCount { get; set; }
    public Guid LeaseToken { get; init; }
    public DateTime LockedUntil { get; init; }
}

public sealed class AutoCallAttempt
{
    public int CallId { get; init; }
    public int QueueId { get; init; }
    public Guid QueueLeaseToken { get; init; }
    public Guid CurrentLeaseToken { get; set; }
    public Guid OwnershipToken => CurrentLeaseToken == Guid.Empty ? QueueLeaseToken : CurrentLeaseToken;
    public string OID { get; init; } = "";
    public string PhoneE164 { get; init; } = "";
    public int FlowId { get; init; }
    public string? TwiML { get; init; }
    public string? Parameters { get; init; }
    public string TemplateJson { get; init; } = "";
    public string TemplateTwiML { get; init; } = "";
    public string VariablesJson { get; init; } = "";
    public string? SubmissionState { get; init; }
    public string? SubmissionErrorCode { get; init; }
    public string? ProviderCallId { get; init; }
    public string? SubmissionRequestJSON { get; init; }
    public string? CallStatus { get; init; }
    public int SubmissionCount { get; init; }
    public int RecoveryCount { get; init; }
    public bool IsPrepared => TwiML is not null && Parameters is not null;
}

public sealed record AutoCallPreparedBundle(string TwiML, string Parameters);

public interface IAutoCallRepository
{
    Task<IReadOnlyList<AutoCallJob>> ClaimAsync(int capacity, string workerId, int leaseMinutes, CancellationToken cancellationToken);
    Task<AutoCallAttempt> BeginAttemptAsync(AutoCallJob job, string workerId, string recipient, CancellationToken cancellationToken);
    Task<int> SavePreparationAsync(AutoCallAttempt attempt, string workerId, AutoCallPreparedBundle bundle, CancellationToken cancellationToken);
    Task FailPreparationAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken);
}

public interface IAutoCallProcessingService
{
    Task<AutoCallAttempt> PrepareAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken);
}

public sealed class AutoCallWorkerOptions
{
    public int MaxConcurrency { get; init; } = 1;
    public int LeaseMinutes { get; init; } = 5;
    public int PollSeconds { get; init; } = 5;
    public int PreparationTimeoutSeconds { get; init; } = 60;
    public string FeedbackBaseUrl { get; init; } = "";
    public string WorkerId { get; init; } = "autocall-" + Guid.NewGuid().ToString("N");

    public void Validate()
    {
        if (MaxConcurrency is < 1 or > 100 || LeaseMinutes is < 1 or > 60 || PollSeconds is < 1 or > 300 ||
            PreparationTimeoutSeconds < 1 || PreparationTimeoutSeconds >= LeaseMinutes * 60 ||
            string.IsNullOrWhiteSpace(WorkerId) || WorkerId.Length > 100 || WorkerId != WorkerId.Trim())
            throw new InvalidOperationException("Invalid AutoCall:Worker capacity, timing or identity configuration.");
        ValidateBaseUrl(FeedbackBaseUrl);
    }

    internal static string ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || value != value.Trim())
            throw new InvalidOperationException("AutoCall:Worker:FeedbackBaseUrl must be an absolute HTTPS URL without credentials, query or fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    internal static AutoCallWorkerOptions Read(IConfiguration configuration)
    {
        var options = configuration.GetSection("AutoCall:Worker").Get<AutoCallWorkerOptions>() ?? new AutoCallWorkerOptions();
        options.Validate();
        return options;
    }
}