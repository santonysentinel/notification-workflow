using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NotificationWorkflowService.Service.Transcription;

public sealed class DeepgramClient(IHttpClientFactory factory, TranscriptionOptions options, ILogger<DeepgramClient>? logger = null) : IDeepgramClient
{
    internal const string ClientName = "TranscriptionDeepgram";
    private readonly ILogger<DeepgramClient> diagnosticsLogger = logger ?? NullLogger<DeepgramClient>.Instance;

    internal static IReadOnlyList<KeyValuePair<string, string>> SnapshotOptions(string configurationJSON, bool requireOptOut, ILogger? logger = null)
    {
        try
        {
            using var document = JsonDocument.Parse(configurationJSON);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var output = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException();
                if (property.Name is "model" or "version" or "language")
                {
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: > 0 and <= 100 } text ||
                        text.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
                        throw new InvalidDataException();
                    output.Add(new(property.Name, text));
                }
                else if (property.Name is "detect_language" or "utterances" or "multichannel" or "diarize" or "punctuate" or "smart_format" or "mip_opt_out")
                {
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException();
                    output.Add(new(property.Name, property.Value.ValueKind == JsonValueKind.True ? "true" : "false"));
                }
                else throw new InvalidDataException();
            }
            if (!seen.Contains("model") || !seen.Contains("version") ||
                (!seen.Contains("language") && !output.Contains(new("detect_language", "true"))) ||
                !output.Contains(new("utterances", "true")) || !output.Contains(new("multichannel", "true")) ||
                !seen.Contains("diarize") || !seen.Contains("punctuate") || !seen.Contains("smart_format") ||
                (requireOptOut && !output.Contains(new("mip_opt_out", "true")))) throw new InvalidDataException();
            return output;
        }
        catch (Exception exception)
        {
            if (logger is not null) TranscriptionDiagnostics.Failure(logger, "Parse configuration snapshot", exception);
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_CONFIGURATION_SNAPSHOT");
        }
    }

    public async Task<string> SubmitAsync(TranscriptionAudio audio, string configurationJSON, TranscriptionSubmissionIdentity identity, CallbackCredential credential, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        var fields = SnapshotOptions(configurationJSON, options.RequireModelImprovementOptOut, diagnosticsLogger).ToList();
        if (identity.JobId <= 0 || identity.CorrelationId == Guid.Empty || identity.SubmissionToken == Guid.Empty ||
            audio.Length is <= 0 || audio.Length > options.MaxAudioBytes || credential.ExpiresAt <= DateTime.UtcNow.AddSeconds(options.SubmissionTimeoutSeconds))
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_SUBMISSION");
        var callback = new UriBuilder(TranscriptionOptions.HttpsUrl(options.CallbackUrl))
        { UserName = identity.CorrelationId.ToString("D"), Password = credential.Token };
        fields.Add(new("callback", callback.Uri.AbsoluteUri));
        fields.Add(new("tag", identity.CorrelationId.ToString("D")));
        string query = string.Join('&', fields.Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.SubmissionTimeoutSeconds));
        using var client = factory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(TranscriptionOptions.HttpsUrl(options.DeepgramBaseUrl), "/v1/listen?" + query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", options.DeepgramApiKey);
        request.Content = new StreamContent(audio.Content, 65536);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        request.Content.Headers.ContentLength = audio.Length;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                int status = (int)response.StatusCode;
                diagnosticsLogger.LogWarning("Transcription Deepgram upload returned HTTP {HttpStatus} for job {JobId}", status, identity.JobId);
                if (status is >= 400 and < 500 && status != 408)
                {
                    using var rejected = await TranscriptionHttp.ReadJsonAsync(response.Content, deadline.Token).ConfigureAwait(false);
                    var error = rejected.RootElement;
                    string? providerCode = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("err_code", out var errorCode) &&
                        errorCode.ValueKind == JsonValueKind.String ? errorCode.GetString() : null;
                    string? providerMessage = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("err_msg", out var message) &&
                        message.ValueKind == JsonValueKind.String ? message.GetString() : null;
                    string safeCode = providerCode switch
                    {
                        "Bad Request" => "BAD_REQUEST",
                        "INVALID_AUDIO" or "INVALID_AUTH" or "INSUFFICIENT_PERMISSIONS" or "INVALID_QUERY_PARAMETER" or
                        "TOO_MANY_REQUESTS" or "ASR_PAYMENT_REQUIRED" or "ASR_UNPROCESSABLE_ENTITY" or
                        "PROJECT_NOT_FOUND" or "PAYLOAD_ERROR" or "REMOTE_CONTENT_ERROR" => providerCode,
                        _ => "UNRECOGNIZED"
                    };
                    string safeReason = providerMessage switch
                    {
                        "Invalid callback url." => "INVALID_CALLBACK_URL",
                        "Bad Request: No such model/language/tier combination found." => "UNSUPPORTED_MODEL_LANGUAGE_TIER",
                        "Bad Request: failed to process audio: corrupt or unsupported data" => "CORRUPT_OR_UNSUPPORTED_AUDIO",
                        "Content-type was application/json, but we could not process the JSON payload." => "INVALID_JSON_BODY",
                        "Invalid query string." => "INVALID_QUERY_STRING",
                        not null when providerMessage.StartsWith("Failed to deserialize query parameters:", StringComparison.Ordinal) => "INVALID_QUERY_PARAMETERS",
                        _ => "UNSPECIFIED"
                    };
                    string? sanitizedMessage = options.LogSanitizedProviderErrorMessages
                        ? providerMessage is null ? "[NO_STRING_ERR_MSG]" : SanitizeProviderMessage(providerMessage, identity, credential)
                        : null;
                    string? sanitizedRequest = null;
                    if (options.LogSanitizedProviderErrorMessages)
                    {
                        string safeQuery = string.Join('&', fields.Select(field => Uri.EscapeDataString(field.Key) + "=" +
                            (field.Key is "callback" or "tag" ? "[REDACTED]" : Uri.EscapeDataString(field.Value))));
                        string providerRequestId = error.ValueKind == JsonValueKind.Object &&
                            error.TryGetProperty("request_id", out var requestIdValue) && requestIdValue.ValueKind == JsonValueKind.String &&
                            Guid.TryParseExact(requestIdValue.GetString(), "D", out var rejectedRequestId)
                                ? rejectedRequestId.ToString("D") : "[UNAVAILABLE]";
                        sanitizedRequest = SanitizeProviderMessage(
                            $"POST /v1/listen?{safeQuery}; request target bytes={Encoding.UTF8.GetByteCount(request.RequestUri!.PathAndQuery)}; callback URL characters={callback.Uri.AbsoluteUri.Length}; callback credential characters={credential.Token.Length}; provider request ID={providerRequestId}",
                            identity, credential);
                    }
                    if (options.LogSanitizedProviderErrorMessages)
                        diagnosticsLogger.LogError(
                            "Transcription Deepgram error response for job {JobId}: HTTP {HttpStatus}, provider code {ProviderErrorCode}, reason {ProviderErrorReason}, sanitized message logging enabled {ProviderMessageLoggingEnabled}; sanitized provider message {SanitizedProviderMessage}",
                            identity.JobId, status, safeCode, safeReason, options.LogSanitizedProviderErrorMessages,
                            sanitizedMessage);
                    else
                        diagnosticsLogger.LogError(
                            "Transcription Deepgram error response for job {JobId}: HTTP {HttpStatus}, provider code {ProviderErrorCode}, reason {ProviderErrorReason}, sanitized message logging enabled {ProviderMessageLoggingEnabled}",
                            identity.JobId, status, safeCode, safeReason, options.LogSanitizedProviderErrorMessages);
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("err_code", out var code) && code.ValueKind == JsonValueKind.String &&
                        code.GetString() is { Length: > 0 and <= 100 } text &&
                        (text == "Bad Request" || text.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_')) &&
                        !error.TryGetProperty("results", out _) &&
                        (!error.TryGetProperty("request_id", out var errorRequest) || (errorRequest.ValueKind == JsonValueKind.String && Guid.TryParseExact(errorRequest.GetString(), "D", out _))))
                        throw new TranscriptionException(TranscriptionFailure.DefiniteRejection, status == 429 ? "PROVIDER_RATE_LIMIT" : "PROVIDER_REJECTED", status == 429)
                        {
                            ProviderHttpStatus = status,
                            ProviderMessageLoggingEnabled = options.LogSanitizedProviderErrorMessages,
                            SanitizedProviderMessage = sanitizedMessage,
                            SanitizedProviderRequest = sanitizedRequest
                        };
                    throw new TranscriptionException(TranscriptionFailure.UncertainAcceptance, "ACCEPTANCE_UNKNOWN")
                    {
                        ProviderHttpStatus = status,
                        ProviderMessageLoggingEnabled = options.LogSanitizedProviderErrorMessages,
                        SanitizedProviderMessage = sanitizedMessage,
                        SanitizedProviderRequest = sanitizedRequest
                    };
                }
                throw new TranscriptionException(TranscriptionFailure.UncertainAcceptance, "ACCEPTANCE_UNKNOWN");
            }
            using var accepted = await TranscriptionHttp.ReadJsonAsync(response.Content, deadline.Token).ConfigureAwait(false);
            var root = accepted.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("request_id", out var value) || value.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(value.GetString(), "D", out var requestId) || requestId == Guid.Empty || root.TryGetProperty("err_code", out _) || root.TryGetProperty("results", out _))
                throw new TranscriptionException(TranscriptionFailure.UncertainAcceptance, "INVALID_ACCEPTANCE_RESPONSE");
            return requestId.ToString("D");
        }
        catch (TranscriptionException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Deepgram upload", exception, identity.JobId);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Deepgram upload", exception, identity.JobId,
                cancellationReason: cancellationToken.IsCancellationRequested ? "Caller canceled; acceptance uncertain." : $"Upload exceeded its {options.SubmissionTimeoutSeconds}-second deadline.");
            throw new TranscriptionException(TranscriptionFailure.UncertainAcceptance, "UPLOAD_INTERRUPTED");
        }
        catch (Exception exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Deepgram upload", exception, identity.JobId);
            throw new TranscriptionException(TranscriptionFailure.UncertainAcceptance, "ACCEPTANCE_UNKNOWN");
        }
    }

    private string SanitizeProviderMessage(string message, TranscriptionSubmissionIdentity identity, CallbackCredential credential)
    {
        string[] secrets =
        [
            credential.Token, options.DeepgramApiKey, options.Identity.SignInName, options.Identity.Password,
            options.Twilio.ApiKeySecret, options.Twilio.AuthToken,
            identity.SubmissionToken.ToString("D"), identity.SubmissionToken.ToString("N"),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(identity.CorrelationId.ToString("D") + ":" + credential.Token))
        ];
        foreach (string secret in secrets.Where(value => value.Length > 0)
            .SelectMany(value => new[] { value, Uri.EscapeDataString(value), WebUtility.HtmlEncode(value) })
            .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
            message = message.Replace(secret, "[REDACTED]", StringComparison.Ordinal);

        try
        {
            // Provider messages can echo the credential-bearing callback or another JWT.
            message = Regex.Replace(message, @"https?(?:://|%3a%2f%2f)[^\s""'<>]+", "[REDACTED_URL]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
            message = Regex.Replace(message, @"[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", "[REDACTED_JWT]",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Redact provider error message", exception, identity.JobId);
            return "[MESSAGE_WITHHELD_REDACTION_TIMEOUT]";
        }
        return string.Concat(message.Select(character => char.IsControl(character) ? $"\\u{(int)character:x4}" : character.ToString()));
    }
}