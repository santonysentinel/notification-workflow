using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed class TwilioSubmissionException(bool rejected, string errorCode) : Exception("Twilio submission did not return a validated acceptance.")
{
    public bool Rejected { get; } = rejected;
    public string ErrorCode { get; } = errorCode;
}

public sealed class TwilioCallClient(IHttpClientFactory factory, AutoCallTwilioOptions options,
    ILogger<TwilioCallClient>? logger = null) : ITwilioCallClient, ITwilioCallLookup
{
    internal const string ClientName = "AutoCallTwilio";
    private readonly ILogger<TwilioCallClient> diagnosticsLogger = logger ?? NullLogger<TwilioCallClient>.Instance;

    public async Task<string> CreateAsync(AutoCallSubmissionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled || request.AccountSid != options.AccountSid || !AutoCallTwilioOptions.ValidSid(request.AccountSid, "AC") ||
            !AutoCallTwilioOptions.ValidPhone(request.To) || request.From != options.FromNumber || !AutoCallTwilioOptions.ValidPhone(request.From) ||
            request.CallId <= 0 || string.IsNullOrWhiteSpace(options.AuthToken) || options.TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("Twilio client requires validated account, sender and call identity.");
        foreach (string url in new[] { request.Url, request.StatusCallback, request.RecordingStatusCallback })
        {
            var uri = new Uri(url, UriKind.Absolute);
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException("Twilio callbacks must use credential-free HTTPS.");
        }
        if (request.OpeningStep is { } opening && (opening.ExecutionId == Guid.Empty || string.IsNullOrWhiteSpace(opening.StepId) ||
            string.IsNullOrWhiteSpace(opening.ResponseTwiML) || opening.ResponseTwiML.Contains("{{", StringComparison.Ordinal)))
            throw new InvalidOperationException("Inline submission requires a resolved opening response and execution identity.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var client = factory.CreateClient(ClientName);
        client.MaxResponseContentBufferSize = 65536;
        using var message = new HttpRequestMessage(HttpMethod.Post, $"https://api.twilio.com/2010-04-01/Accounts/{options.AccountSid}/Calls.json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(options.AccountSid + ":" + options.AuthToken)));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var fields = new List<KeyValuePair<string, string>>
        {
            new("To", request.To), new("From", request.From),
            new("StatusCallback", request.StatusCallback), new("StatusCallbackMethod", "POST"),
            new("RecordingStatusCallback", request.RecordingStatusCallback), new("RecordingStatusCallbackMethod", "POST"),
            new("RecordingStatusCallbackEvent", "completed"), new("Record", request.Record ? "true" : "false")
        };
        if (request.OpeningStep is { } inline) fields.Add(new("Twiml", inline.ResponseTwiML));
        else
        {
            fields.Add(new("Url", request.Url));
            fields.Add(new("Method", "POST"));
        }
        foreach (string progress in new[] { "initiated", "ringing", "answered", "completed" }) fields.Add(new("StatusCallbackEvent", progress));
        message.Content = new FormUrlEncodedContent(fields);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                int status = (int)response.StatusCode;
                diagnosticsLogger.LogWarning("Auto-call Twilio create returned HTTP {HttpStatus} for call {CallId}", status, request.CallId);
                if (status is >= 400 and < 500 && status != 408)
                {
                    using var rejection = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                    if (rejection.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out int number) && number is >= 20000 and <= 99999 &&
                        rejection.RootElement.TryGetProperty("status", out var rejectedStatus) && rejectedStatus.TryGetInt32(out int httpStatus) && httpStatus == status &&
                        (!rejection.RootElement.TryGetProperty("sid", out var rejectedSid) || rejectedSid.ValueKind == JsonValueKind.Null) &&
                        (status != 429 || number == 20429) && (number != 20429 || status == 429))
                        throw new TwilioSubmissionException(true, "TWILIO_" + number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    throw new TwilioSubmissionException(false, "ACCEPTANCE_UNKNOWN");
                }
                throw new TwilioSubmissionException(false, "HTTP_" + status);
            }
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            string? sid = body.RootElement.GetProperty("sid").GetString();
            if (body.RootElement.GetProperty("account_sid").GetString() != request.AccountSid || sid is null || !AutoCallTwilioOptions.ValidSid(sid, "CA"))
                throw new TwilioSubmissionException(false, "INVALID_RESPONSE");
            return sid;
        }
        catch (TwilioSubmissionException exception)
        {
            AutoCallDiagnostics.Failure(diagnosticsLogger, "Twilio create", exception, callId: request.CallId);
            throw;
        }
        catch (Exception exception)
        {
            AutoCallDiagnostics.Failure(diagnosticsLogger, "Twilio create (acceptance unknown)", exception, callId: request.CallId,
                cancellationReason: exception is OperationCanceledException
                    ? cancellationToken.IsCancellationRequested ? "Caller canceled; acceptance unknown." : $"Twilio request deadline exceeded ({options.TimeoutSeconds} seconds)."
                    : null);
            throw new TwilioSubmissionException(false, "ACCEPTANCE_UNKNOWN");
        }
    }

    public async Task<TwilioCallObservation> GetAsync(AutoCallSubmissionRequest request, string sid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled || request.AccountSid != options.AccountSid || !AutoCallTwilioOptions.ValidSid(request.AccountSid, "AC") ||
            !AutoCallTwilioOptions.ValidSid(sid, "CA") || !AutoCallTwilioOptions.ValidPhone(request.To) ||
            !AutoCallTwilioOptions.ValidPhone(request.From) || string.IsNullOrWhiteSpace(options.AuthToken) || options.TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("Reconciliation requires the original account, SID and recipient.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var client = factory.CreateClient(ClientName);
        client.MaxResponseContentBufferSize = 65536;
        using var message = new HttpRequestMessage(HttpMethod.Get, $"https://api.twilio.com/2010-04-01/Accounts/{request.AccountSid}/Calls/{sid}.json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(options.AccountSid + ":" + options.AuthToken)));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                diagnosticsLogger.LogWarning("Auto-call Twilio lookup returned HTTP {HttpStatus} for call {CallId}", (int)response.StatusCode, request.CallId);
                throw new AutoCallReconciliationException();
            }
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            var root = body.RootElement;
            string? status = root.GetProperty("status").GetString();
            if (root.GetProperty("sid").GetString() != sid || root.GetProperty("account_sid").GetString() != request.AccountSid ||
                root.GetProperty("to").GetString() != request.To || root.GetProperty("from").GetString() != request.From || !TwilioCallObservation.ValidStatus(status))
                throw new AutoCallReconciliationException();
            int? duration = null;
            if (root.TryGetProperty("duration", out var value) && value.ValueKind != JsonValueKind.Null)
            {
                if (!int.TryParse(value.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds))
                    throw new AutoCallReconciliationException();
                duration = seconds;
            }
            return new(sid, status!, duration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            AutoCallDiagnostics.Failure(diagnosticsLogger, "Twilio reconciliation lookup", exception, callId: request.CallId,
                cancellationReason: exception is OperationCanceledException ? $"Twilio lookup deadline exceeded ({options.TimeoutSeconds} seconds)." : null);
            throw new AutoCallReconciliationException();
        }
    }
}