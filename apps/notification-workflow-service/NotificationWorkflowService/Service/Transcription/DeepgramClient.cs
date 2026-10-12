using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NotificationWorkflowService.Service.Transcription;

public sealed class DeepgramClient(IHttpClientFactory factory, TranscriptionOptions options) : IDeepgramClient
{
    internal const string ClientName = "TranscriptionDeepgram";

    internal static IReadOnlyList<KeyValuePair<string, string>> SnapshotOptions(string configurationJSON, bool requireOptOut)
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
        catch (Exception) { throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_CONFIGURATION_SNAPSHOT"); }
    }

    public async Task<string> SubmitAsync(TranscriptionAudio audio, string configurationJSON, TranscriptionSubmissionIdentity identity, CallbackCredential credential, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        var fields = SnapshotOptions(configurationJSON, options.RequireModelImprovementOptOut).ToList();
        if (identity.JobId <= 0 || identity.CorrelationId == Guid.Empty || identity.SubmissionToken == Guid.Empty ||
            audio.Length is <= 0 || audio.Length > options.MaxAudioBytes || credential.ExpiresAt <= DateTime.UtcNow.AddSeconds(options.SubmissionTimeoutSeconds))
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_SUBMISSION");
        var callback = new UriBuilder(TranscriptionOptions.HttpsUrl(options.CallbackUrl))
        { UserName = identity.CorrelationId.ToString("D"), Password = credential.Token };
        fields.Add(new("callback", callback.Uri.AbsoluteUri));
        fields.Add(new("callback_method", "POST"));
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
                if (status is >= 400 and < 500 && status != 408)
                {
                    using var rejected = await TranscriptionHttp.ReadJsonAsync(response.Content, deadline.Token).ConfigureAwait(false);
                    var error = rejected.RootElement;
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("err_code", out var code) && code.ValueKind == JsonValueKind.String &&
                        code.GetString() is { Length: > 0 and <= 100 } text && text.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_') &&
                        !error.TryGetProperty("results", out _) &&
                        (!error.TryGetProperty("request_id", out var errorRequest) || (errorRequest.ValueKind == JsonValueKind.String && Guid.TryParseExact(errorRequest.GetString(), "D", out _))))
                        throw new TranscriptionException(TranscriptionFailure.DefiniteRejection, status == 429 ? "PROVIDER_RATE_LIMIT" : "PROVIDER_REJECTED", status == 429);
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
        catch (TranscriptionException) { throw; }
        catch (OperationCanceledException) { throw new TranscriptionException(TranscriptionFailure.UncertainAcceptance, "UPLOAD_INTERRUPTED"); }
        catch (Exception) { throw new TranscriptionException(TranscriptionFailure.UncertainAcceptance, "ACCEPTANCE_UNKNOWN"); }
    }
}