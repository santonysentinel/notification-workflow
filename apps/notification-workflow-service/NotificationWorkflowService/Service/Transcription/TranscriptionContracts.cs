using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace NotificationWorkflowService.Service.Transcription;

public sealed class TranscriptionOptions
{
    public bool Enabled { get; init; }
    public string CallbackUrl { get; init; } = "https://feedback.example.com/webhooks/deepgram/transcriptions/completed";
    public string DeepgramBaseUrl { get; init; } = "https://api.deepgram.com";
    public string DeepgramApiKey { get; init; } = "";
    public int SubmissionTimeoutSeconds { get; init; } = 600;
    public int MediaHeaderTimeoutSeconds { get; init; } = 30;
    public int MaxDurationSeconds { get; init; } = 3600;
    public long MaxAudioBytes { get; init; } = 128 * 1024 * 1024;
    public bool RequireModelImprovementOptOut { get; init; } = true;
    public TranscriptionIdentityOptions Identity { get; init; } = new();
    public TranscriptionTwilioOptions Twilio { get; init; } = new();

    public void Validate()
    {
        if (!Enabled) throw new TranscriptionException(TranscriptionFailure.Validation, "DISABLED");
        var callback = HttpsUrl(CallbackUrl);
        var provider = HttpsUrl(DeepgramBaseUrl);
        if (!new[] { 443, 8080, 8443 }.Contains(callback.Port) || provider.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(DeepgramApiKey) || DeepgramApiKey.Length > 4096 || DeepgramApiKey.Any(character => !char.IsAscii(character) || char.IsControl(character) || char.IsWhiteSpace(character)) ||
            SubmissionTimeoutSeconds is < 1 or > 3600 || MediaHeaderTimeoutSeconds is < 1 or > 120 ||
            MaxDurationSeconds is < 1 or > 86400 || MaxAudioBytes is < 44 or > 1024L * 1024 * 1024)
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_PROVIDER_CONFIGURATION");
        Identity.Validate();
        Twilio.Validate();
    }

    internal static Uri HttpsUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_HTTPS_ENDPOINT");
        return uri;
    }

    internal static bool DecimalId(string value) => Regex.IsMatch(value, @"\A[1-9][0-9]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    internal static bool Sid(string value, string prefix) => Regex.IsMatch(value, @"\A" + prefix + @"[0-9a-fA-F]{32}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    internal static TranscriptionOptions Read(IConfiguration configuration) => configuration.GetSection("Transcription").Get<TranscriptionOptions>() ?? new();
}

public sealed class TranscriptionIdentityOptions
{
    public string BaseUrl { get; init; } = "http://172.27.112.243:8085";
    public string JwtJwksPath { get; init; } = "/.well-known/jwks.json";
    public bool AllowInsecureIdentityHttp { get; init; }
    public string SignInName { get; init; } = "";
    public string Password { get; init; } = "";
    public string PermissionId { get; init; } = "";
    public string Issuer { get; init; } = "sentineldna.com";
    public string Audience { get; init; } = "sentineldna.com";
    public string Algorithm { get; init; } = "RS256";
    public int TokenValidityMinutes { get; init; } = 120;
    public int TimeoutSeconds { get; init; } = 30;

    internal Uri Endpoint(string path)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var root) ||
            (root.Scheme != "https" && !(root.Scheme == "http" && AllowInsecureIdentityHttp)) ||
            root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(path) || path.Contains('?') || path.Contains('#') || path.Contains("..", StringComparison.Ordinal))
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_IDENTITY_ENDPOINT");
        var endpoint = new Uri(new Uri(root.AbsoluteUri.TrimEnd('/') + "/"), path);
        if (endpoint.Authority != root.Authority || endpoint.Scheme != root.Scheme)
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_IDENTITY_ENDPOINT");
        return endpoint;
    }

    internal void Validate()
    {
        _ = Endpoint("accounts/authenticate2");
        _ = Endpoint(JwtJwksPath);
        if (string.IsNullOrWhiteSpace(SignInName) || string.IsNullOrWhiteSpace(Password) ||
            !TranscriptionOptions.DecimalId(PermissionId) || string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience) ||
            Algorithm is not ("RS256" or "ES256") || TokenValidityMinutes is < 1 or > 1440 || TimeoutSeconds is < 1 or > 120)
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_IDENTITY_CONFIGURATION");
    }
}

public sealed class TranscriptionTwilioOptions
{
    public string AccountSid { get; init; } = "";
    public string ApiKeySid { get; init; } = "";
    public string ApiKeySecret { get; init; } = "";
    public string AuthToken { get; init; } = "";

    internal void Validate()
    {
        if (!TranscriptionOptions.Sid(AccountSid, "AC") ||
            (ApiKeySid.Length > 0 ? !TranscriptionOptions.Sid(ApiKeySid, "SK") || string.IsNullOrWhiteSpace(ApiKeySecret) : string.IsNullOrWhiteSpace(AuthToken)))
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_TWILIO_CONFIGURATION");
    }
}

public enum TranscriptionFailure { Validation, TransientPreparation, DefiniteRejection, UncertainAcceptance, Persistence }

public sealed class TranscriptionException(TranscriptionFailure failure, string errorCode, bool retryable = false)
    : Exception($"Transcription operation failed: {failure} ({errorCode}).")
{
    public TranscriptionFailure Failure { get; } = failure;
    public string ErrorCode { get; } = errorCode;
    public bool Retryable { get; } = retryable;
}

public sealed class CallbackCredential
{
    internal string Token { get; }
    public DateTime ExpiresAt { get; }
    public byte[] Hash => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Token));
    internal CallbackCredential(string token, DateTime expiresAt) { Token = token; ExpiresAt = expiresAt; }
    public override string ToString() => "CallbackCredential [redacted]";
}

public sealed class TranscriptionWork
{
    public int JobId { get; init; }
    public int RecordingId { get; init; }
    public Guid LeaseToken { get; init; }
    public string ConfigurationJSON { get; init; } = "";
    public int CallbackWaitMinutes { get; init; } = 60;
    public string Provider { get; init; } = "Twilio";
    public string AccountSid { get; init; } = "";
    public string RecordingSid { get; init; } = "";
    public string RecordingStatus { get; init; } = "";
    public int? DurationSeconds { get; init; }
    public int? Channels { get; init; }
}

public sealed record TranscriptionSubmissionIdentity(int JobId, Guid CorrelationId, Guid SubmissionToken);
public sealed record TranscriptionCreation(string Outcome, int? SubmissionId, int? AttemptNumber = null);
public sealed record TranscriptionResult(string Outcome, string? RequestId = null, TranscriptionFailure? Failure = null, string? ErrorCode = null, bool Retryable = false);

public interface ICallbackTokenClient { Task<CallbackCredential> AcquireAsync(TimeSpan minimumValidity, CancellationToken cancellationToken); }
public interface ITwilioRecordingClient { Task<TranscriptionAudio> OpenAsync(TranscriptionWork work, CancellationToken cancellationToken); }
public interface IDeepgramClient { Task<string> SubmitAsync(TranscriptionAudio audio, string configurationJSON, TranscriptionSubmissionIdentity identity, CallbackCredential credential, CancellationToken cancellationToken); }
public interface ITranscriptionSubmissionRepository
{
    Task<TranscriptionCreation> CreateAsync(TranscriptionWork work, string workerId, TranscriptionSubmissionIdentity identity, CallbackCredential credential, CancellationToken cancellationToken);
    Task<bool> BeginAsync(TranscriptionWork work, string workerId, TranscriptionSubmissionIdentity identity, CancellationToken cancellationToken);
    Task<string> SaveAcceptanceAsync(TranscriptionSubmissionIdentity identity, string requestId, CancellationToken cancellationToken);
}

public sealed class TranscriptionAudio(Stream content, long length, IDisposable? owner = null) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public long Length { get; } = length;
    public async ValueTask DisposeAsync()
    {
        try { await Content.DisposeAsync().ConfigureAwait(false); }
        finally { owner?.Dispose(); }
    }
    public override string ToString() => "TranscriptionAudio [stream]";
}