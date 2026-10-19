using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed class AutoCallTwilioOptions
{
    public bool Enabled { get; init; }
    public string AccountSid { get; init; } = "";
    public string AuthToken { get; init; } = "";
    public string FromNumber { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 30;
    public bool RecordCalls { get; init; }

    public void Validate(AutoCallWorkerOptions worker)
    {
        if (!Enabled) return;
        worker.Validate();
        if (!ValidSid(AccountSid, "AC") || string.IsNullOrWhiteSpace(AuthToken) || AuthToken != AuthToken.Trim() ||
            !ValidPhone(FromNumber) || TimeoutSeconds is < 1 or > 120 || TimeoutSeconds + worker.PreparationTimeoutSeconds + 5 >= worker.LeaseMinutes * 60)
            throw new InvalidOperationException("Invalid AutoCall:Twilio credentials, sender or timeout; valid worker settings and sufficient lease duration are required.");
    }

    internal static bool ValidSid(string value, string prefix)
        => Regex.IsMatch(value, @"\A" + prefix + @"[0-9a-fA-F]{32}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool ValidPhone(string value)
        => Regex.IsMatch(value, @"\A\+[1-9][0-9]{1,14}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static AutoCallTwilioOptions Read(IConfiguration configuration, AutoCallWorkerOptions worker)
    {
        var options = configuration.GetSection("AutoCall:Twilio").Get<AutoCallTwilioOptions>() ?? new();
        options.Validate(worker);
        return options;
    }
}

public sealed record AutoCallOpeningStep(string StepId, Guid ExecutionId, string ResponseTwiML);

public sealed record AutoCallSubmissionRequest(int CallId, string AccountSid, string To, string From, string Url,
    string StatusCallback, string RecordingStatusCallback, bool Record, AutoCallOpeningStep? OpeningStep = null);

public sealed class AutoCallSubmissionResult
{
    public bool ShouldSubmit { get; init; }
    public string SubmissionState { get; init; } = "";
    public Guid? SubmissionToken { get; init; }
    public Guid? OpeningExecutionId { get; init; }
    public string? ProviderCallId { get; init; }
}

public interface IAutoCallSubmissionRepository
{
    Task<AutoCallSubmissionResult> BeginSubmissionAsync(AutoCallAttempt attempt, string workerId, Guid token, AutoCallSubmissionRequest request, CancellationToken cancellationToken);
    Task<AutoCallSubmissionResult> SaveSubmissionAsync(AutoCallAttempt attempt, Guid token, string state, string? sid, string? errorCode, CancellationToken cancellationToken);
}

public interface ITwilioCallClient
{
    Task<string> CreateAsync(AutoCallSubmissionRequest request, CancellationToken cancellationToken);
}

public interface IAutoCallSubmissionService
{
    Task<AutoCallSubmissionResult> SubmitAsync(AutoCallAttempt attempt, string workerId, CancellationToken cancellationToken);
}

public sealed class AutoCallSubmissionService(IAutoCallSubmissionRepository repository, ITwilioCallClient client,
    AutoCallTwilioOptions options, AutoCallWorkerOptions worker, ILogger<AutoCallSubmissionService>? logger = null) : IAutoCallSubmissionService
{
    private readonly ILogger<AutoCallSubmissionService> logger = logger ?? NullLogger<AutoCallSubmissionService>.Instance;

    public async Task<AutoCallSubmissionResult> SubmitAsync(AutoCallAttempt attempt, string workerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled) return new() { SubmissionState = "DISABLED" };
        options.Validate(worker);
        if (!attempt.IsPrepared || attempt.CallId <= 0 || attempt.QueueId <= 0 || attempt.QueueLeaseToken == Guid.Empty || !AutoCallTwilioOptions.ValidPhone(attempt.PhoneE164))
            throw new InvalidOperationException("Submission requires a persisted prepared attempt and recipient.");
        string baseUrl = AutoCallWorkerOptions.ValidateBaseUrl(worker.FeedbackBaseUrl);
        using var parameters = JsonDocument.Parse(attempt.Parameters!);
        if (parameters.RootElement.GetProperty("callId").GetInt32() != attempt.CallId ||
            parameters.RootElement.GetProperty("recipient").GetString() != attempt.PhoneE164 ||
            AutoCallWorkerOptions.ValidateBaseUrl(parameters.RootElement.GetProperty("feedbackBaseUrl").GetString() ?? "") != baseUrl)
            throw new InvalidOperationException("Submission callbacks and recipient must match the prepared bundle.");
        string suffix = "?callId=" + attempt.CallId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var opening = AutoCallBundlePreparer.PrepareOpening(attempt.TwiML!);
        var request = new AutoCallSubmissionRequest(attempt.CallId, options.AccountSid, attempt.PhoneE164, options.FromNumber,
            baseUrl + "/webhooks/twilio/voice/start" + suffix,
            baseUrl + "/webhooks/twilio/voice/status" + suffix,
            baseUrl + "/webhooks/twilio/recordings/status" + suffix, options.RecordCalls, opening);
        Guid token = Guid.NewGuid();
        var marker = await repository.BeginSubmissionAsync(attempt, workerId, token, request, cancellationToken).ConfigureAwait(false);
        if (!marker.ShouldSubmit) return marker;
        if (marker.SubmissionToken != token || marker.SubmissionState != "SUBMITTING" || marker.OpeningExecutionId != opening.ExecutionId)
            throw new InvalidOperationException("Submission marker identity is invalid.");
        string state = "ACCEPTED";
        string? sid = null, error = null;
        try
        {
            sid = await client.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            if (!AutoCallTwilioOptions.ValidSid(sid, "CA")) throw new InvalidOperationException("Invalid accepted SID.");
        }
        catch (TwilioSubmissionException exception)
        {
            AutoCallDiagnostics.Failure(this.logger, "Twilio submission", exception, attempt.QueueId, attempt.CallId);
            state = exception.Rejected ? "REJECTED" : "UNKNOWN";
            error = exception.ErrorCode;
        }
        catch (Exception exception)
        {
            AutoCallDiagnostics.Failure(this.logger, "Twilio submission (acceptance unknown)", exception, attempt.QueueId, attempt.CallId,
                cancellationReason: exception is OperationCanceledException ? "Submission canceled; provider acceptance is unknown." : null);
            state = "UNKNOWN";
            error = "ACCEPTANCE_UNKNOWN";
        }
        using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await repository.SaveSubmissionAsync(attempt, token, state, sid, error, persistence.Token).ConfigureAwait(false);
        this.logger.LogInformation("Auto-call submission for queue {QueueId}, call {CallId} persisted as {SubmissionState}",
            attempt.QueueId, attempt.CallId, state);
        return result;
    }
}