using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NotificationWorkflowService.Service.Transcription;

public sealed class CallbackTokenClient(IHttpClientFactory factory, TranscriptionOptions options, ILogger<CallbackTokenClient>? logger = null) : ICallbackTokenClient
{
    internal const string ClientName = "TranscriptionIdentity";
    private readonly ILogger<CallbackTokenClient> diagnosticsLogger = logger ?? NullLogger<CallbackTokenClient>.Instance;
    private readonly SemaphoreSlim keyLock = new(1, 1);
    private IReadOnlyCollection<SecurityKey>? keys;
    private DateTime keysExpireAt;

    public async Task<CallbackCredential> AcquireAsync(TimeSpan minimumValidity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Identity.Validate();
        if (minimumValidity <= TimeSpan.Zero || minimumValidity >= TimeSpan.FromMinutes(options.Identity.TokenValidityMinutes))
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_CREDENTIAL_WINDOW");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Identity.TimeoutSeconds));
        try
        {
            using var client = factory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Identity.Endpoint(TranscriptionIdentityOptions.AuthenticationPath))
            { Content = JsonContent.Create(new { signInname = options.Identity.SignInName, password = options.Identity.Password }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                diagnosticsLogger.LogWarning("Transcription identity authentication returned HTTP {HttpStatus}", (int)response.StatusCode);
                throw new TranscriptionException(response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                    ? TranscriptionFailure.TransientPreparation : TranscriptionFailure.Validation, "IDENTITY_REJECTED",
                    response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable);
            }
            using var body = await TranscriptionHttp.ReadJsonAsync(response.Content, deadline.Token).ConfigureAwait(false);
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jwt", out var value) ||
                value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_IDENTITY_RESPONSE");
            string token = value.GetString()!;
            var handler = new JsonWebTokenHandler { MaximumTokenSizeInBytes = 16000 };
            var validation = await handler.ValidateTokenAsync(token, Parameters(await GetKeysAsync(client, false, deadline.Token).ConfigureAwait(false))).ConfigureAwait(false);
            if (!validation.IsValid && validation.Exception is SecurityTokenSignatureKeyNotFoundException)
                validation = await handler.ValidateTokenAsync(token, Parameters(await GetKeysAsync(client, true, deadline.Token).ConfigureAwait(false))).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                diagnosticsLogger.LogError("Transcription callback token rejected: {TokenRejectionReason}", "TOKEN_VALIDATION_FAILED");
                if (validation.Exception is { } exception)
                    TranscriptionDiagnostics.Failure(diagnosticsLogger, "Validate callback token", exception);
                throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_CALLBACK_TOKEN");
            }
            if (validation.SecurityToken is not JsonWebToken jwt)
            {
                diagnosticsLogger.LogError("Transcription callback token rejected: {TokenRejectionReason}", "UNEXPECTED_TOKEN_TYPE");
                throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_CALLBACK_TOKEN");
            }
            var now = DateTime.UtcNow;
            var requiredUntil = now.Add(minimumValidity);
            if (jwt.ValidTo <= requiredUntil)
            {
                diagnosticsLogger.LogError(
                    "Transcription callback token rejected: {TokenRejectionReason}; current UTC {CurrentUtc:O}, expiry UTC {ExpiresUtc:O}, required valid until UTC {RequiredValidUntilUtc:O}, remaining {RemainingValiditySeconds} seconds, required more than {RequiredValiditySeconds} seconds",
                    "INSUFFICIENT_REMAINING_VALIDITY", now, jwt.ValidTo, requiredUntil,
                    (jwt.ValidTo - now).TotalSeconds, minimumValidity.TotalSeconds);
                throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_CALLBACK_TOKEN");
            }
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload));
            if (!payload.RootElement.TryGetProperty("permissions", out var permission) || permission.ValueKind != JsonValueKind.String)
            {
                diagnosticsLogger.LogError("Transcription callback token rejected: {TokenRejectionReason}", "MISSING_OR_INVALID_PERMISSIONS_CLAIM");
                throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_CALLBACK_TOKEN");
            }
            string[] permissions = permission.GetString()!.Split(',').Select(item => item.Trim()).ToArray();
            if (permissions.Length == 0 || !permissions.All(TranscriptionOptions.DecimalId) || !permissions.Contains(options.Identity.PermissionId, StringComparer.Ordinal))
            {
                diagnosticsLogger.LogError("Transcription callback token rejected: {TokenRejectionReason}", "MISSING_OR_INVALID_CALLBACK_PERMISSION");
                throw new TranscriptionException(TranscriptionFailure.Validation, "MISSING_CALLBACK_PERMISSION");
            }
            return new CallbackCredential(token, jwt.ValidTo);
        }
        catch (TranscriptionException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Acquire callback credential", exception);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Acquire callback credential", exception,
                cancellationReason: $"Identity request exceeded its {options.Identity.TimeoutSeconds}-second deadline.");
            throw new TranscriptionException(TranscriptionFailure.TransientPreparation, "IDENTITY_TIMEOUT", true);
        }
        catch (HttpRequestException exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Acquire callback credential", exception);
            throw new TranscriptionException(TranscriptionFailure.TransientPreparation, "IDENTITY_UNAVAILABLE", true);
        }
        catch (Exception exception)
        {
            TranscriptionDiagnostics.Failure(diagnosticsLogger, "Acquire callback credential", exception);
            throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_IDENTITY_RESPONSE");
        }
    }

    private TokenValidationParameters Parameters(IReadOnlyCollection<SecurityKey> signingKeys) => new()
    {
        ValidIssuer = options.Identity.Issuer, ValidAudience = options.Identity.Audience,
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, RequireExpirationTime = true,
        RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKeys = signingKeys,
        ValidAlgorithms = [options.Identity.Algorithm], ClockSkew = TimeSpan.FromSeconds(5),
        IncludeTokenOnFailedValidation = false, LogValidationExceptions = false, LogTokenId = false
    };

    private async Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(HttpClient client, bool refresh, CancellationToken cancellationToken)
    {
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && keys is not null && keysExpireAt > DateTime.UtcNow) return keys;
            using var request = new HttpRequestMessage(HttpMethod.Get, options.Identity.Endpoint(options.Identity.JwtJwksPath));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                diagnosticsLogger.LogWarning("Transcription identity JWKS retrieval returned HTTP {HttpStatus}", (int)response.StatusCode);
                throw new TranscriptionException(TranscriptionFailure.TransientPreparation, "JWKS_UNAVAILABLE", true);
            }
            using var document = await TranscriptionHttp.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var candidate = new JsonWebKeySet(document.RootElement.GetRawText()).Keys
                .Where(jwk => !jwk.HasPrivateKey && (string.IsNullOrEmpty(jwk.Use) || jwk.Use == "sig") &&
                    (string.IsNullOrEmpty(jwk.Alg) || jwk.Alg == options.Identity.Algorithm) &&
                    (jwk.KeyOps.Count == 0 || jwk.KeyOps.Contains("verify")) &&
                    ((options.Identity.Algorithm == "RS256" && jwk.Kty == "RSA") || (options.Identity.Algorithm == "ES256" && jwk.Kty == "EC" && jwk.Crv == "P-256")))
                .Cast<SecurityKey>().ToArray();
            if (candidate.Length == 0) throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_JWKS");
            keys = candidate;
            keysExpireAt = DateTime.UtcNow.AddMinutes(10);
            return candidate;
        }
        finally { keyLock.Release(); }
    }
}

internal static class TranscriptionHttp
{
    internal static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentType?.MediaType is not ("application/json" or "application/jwk-set+json"))
            throw new InvalidDataException("Expected a JSON response.");
        if (content.Headers.ContentLength > 65536) throw new InvalidDataException("Response exceeds the safe limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 65536) throw new InvalidDataException("Response exceeds the safe limit.");
            buffer.Write(chunk, 0, count);
        }
        var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (!Unique(document.RootElement)) { document.Dispose(); throw new InvalidDataException("Ambiguous JSON response."); }
        return document;
    }

    private static bool Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) if (!Unique(child)) return false;
        return true;
    }
}