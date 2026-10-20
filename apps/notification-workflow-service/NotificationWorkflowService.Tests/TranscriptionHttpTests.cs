using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationWorkflowService.Service.Transcription;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class TranscriptionHttpTests
{
    private const string Account = "AC11111111111111111111111111111111";
    private const string Recording = "RE22222222222222222222222222222222";
    private const string RequestId = "33333333-3333-4333-8333-333333333333";
    private const string Snapshot = "{\"model\":\"nova-3\",\"version\":\"latest\",\"language\":\"en\",\"utterances\":true,\"multichannel\":true,\"diarize\":false,\"punctuate\":false,\"smart_format\":false,\"mip_opt_out\":true}";
    private static TranscriptionOptions Options(int timeout = 2, long maxBytes = 134217728, string permissionId = "1188", string algorithm = "RS256", string identityBase = "https://identity.example", bool allowHttp = false, bool logProviderMessages = false) => new()
    {
        Enabled = true, DeepgramApiKey = "DEEPGRAM-SECRET", SubmissionTimeoutSeconds = timeout, MediaHeaderTimeoutSeconds = timeout, MaxAudioBytes = maxBytes,
        LogSanitizedProviderErrorMessages = logProviderMessages,
        Identity = new() { BaseUrl = identityBase, AllowInsecureIdentityHttp = allowHttp, SignInName = "LOGIN-NAME", Password = "LOGIN-SECRET", PermissionId = permissionId, Algorithm = algorithm, TimeoutSeconds = timeout },
        Twilio = new() { AccountSid = Account, ApiKeySid = "SK44444444444444444444444444444444", ApiKeySecret = "TWILIO-SECRET" }
    };
    private static TranscriptionWork Work(int channels = 1, string configuration = Snapshot) => new()
    {
        JobId = 1, RecordingId = 7, LeaseToken = Guid.NewGuid(), AccountSid = Account, RecordingSid = Recording,
        RecordingStatus = "completed", DurationSeconds = 1, Channels = channels, ConfigurationJSON = configuration
    };
    private static CallbackCredential Credential() => new("CALLBACK.HEADER.SIGNATURE", DateTime.UtcNow.AddHours(2));
    private static TranscriptionSubmissionIdentity Identity() => new(1, Guid.NewGuid(), Guid.NewGuid());
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Wav(byte[] bytes, bool knownLength = true)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new NonSeekableStream(bytes)) };
        response.Content.Headers.ContentType = new("audio/x-wav");
        return response;
    }
    private static byte[] Wave(int channels = 1)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.ASCII, true);
        int size = 8000 * 2 * channels;
        writer.Write("RIFF"u8); writer.Write(36 + size); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)channels); writer.Write(8000); writer.Write(size);
        writer.Write((ushort)(2 * channels)); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(size); writer.Write(new byte[size]);
        return memory.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RetrievesAuthenticatedWavWithoutChangingChannelsAndStreamsSnapshotSubmission(int channels)
    {
        byte[] wave = Wave(channels);
        var credential = Credential();
        var identity = Identity();
        var factory = new Factory(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{Account}/Recordings/{Recording}.wav?RequestedChannels={channels}", request.RequestUri!.AbsoluteUri);
                Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
                Assert.Equal("SK44444444444444444444444444444444:TWILIO-SECRET", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
                return Wav(wave, false);
            }
            Assert.Equal("Token DEEPGRAM-SECRET", request.Headers.Authorization!.ToString());
            Assert.Equal("audio/wav", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(wave.Length, request.Content.Headers.ContentLength);
            Assert.Contains("model=nova-3", request.RequestUri!.Query);
            Assert.Contains("mip_opt_out=true", request.RequestUri.Query);
            Assert.Contains("multichannel=true", request.RequestUri.Query);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.DoesNotContain("callback_method", request.RequestUri.Query);
            Assert.Contains("version=latest", request.RequestUri.Query);
            string callback = request.RequestUri.Query.TrimStart('?').Split('&').Single(item => item.StartsWith("callback=", StringComparison.Ordinal))[9..];
            Assert.Equal($"https://{identity.CorrelationId:D}:{credential.Token}@feedback.example.com/webhooks/deepgram/transcriptions/completed", Uri.UnescapeDataString(callback));
            using var uploaded = new MemoryStream();
            await request.Content.CopyToAsync(uploaded, cancellationToken);
            Assert.Equal(wave, uploaded.ToArray());
            return Json($"{{\"request_id\":\"{RequestId}\"}}");
        });
        await using var audio = await new TwilioRecordingClient(factory, Options()).OpenAsync(Work(channels), default);
        Assert.Equal(RequestId, await new DeepgramClient(factory, Options()).SubmitAsync(audio, Snapshot, identity, credential, default));
        Assert.Equal(2, factory.Calls);
    }

    [Theory]
    [InlineData(404, TranscriptionFailure.TransientPreparation, true)]
    [InlineData(429, TranscriptionFailure.TransientPreparation, true)]
    [InlineData(503, TranscriptionFailure.TransientPreparation, true)]
    [InlineData(403, TranscriptionFailure.Validation, false)]
    [InlineData(302, TranscriptionFailure.Validation, false)]
    public async Task ClassifiesMissingRateLimitedUnavailableOrRejectedMedia(int status, TranscriptionFailure failure, bool retryable)
    {
        var factory = new Factory((_, _) => Task.FromResult(Json("TWILIO-SECRET raw provider body", (HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => new TwilioRecordingClient(factory, Options()).OpenAsync(Work(), default));
        Assert.Equal(failure, error.Failure);
        Assert.Equal(retryable, error.Retryable);
        Assert.DoesNotContain("TWILIO-SECRET", error.ToString());
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData("channels")]
    [InlineData("mime")]
    [InlineData("riff")]
    [InlineData("bytes")]
    [InlineData("compressed")]
    [InlineData("duration")]
    public async Task RejectsInvalidMediaBeforeSubmission(string invalid)
    {
        byte[] wave = Wave(invalid == "channels" ? 2 : 1);
        if (invalid == "riff") wave[0] = 0;
        if (invalid == "duration") BitConverter.GetBytes(800000U).CopyTo(wave, 40);
        var response = Wav(wave);
        if (invalid == "mime") response.Content.Headers.ContentType = new("text/html");
        if (invalid == "compressed") response.Content.Headers.ContentEncoding.Add("gzip");
        var factory = new Factory((_, _) => Task.FromResult(response));
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => new TwilioRecordingClient(factory, Options(maxBytes: invalid == "bytes" ? 100 : 134217728)).OpenAsync(Work(), default));
        Assert.Equal(TranscriptionFailure.Validation, error.Failure);
    }

    [Fact]
    public async Task BoundsMediaHeaderTimeoutWithoutLeakingTransportErrors()
    {
        var factory = new Factory(async (_, cancellationToken) => { await Task.Delay(Timeout.Infinite, cancellationToken); return Json("{}"); });
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => new TwilioRecordingClient(factory, Options(timeout: 1)).OpenAsync(Work(), default));
        Assert.Equal("MEDIA_TIMEOUT", error.ErrorCode);
        Assert.True(error.Retryable);
    }

    [Theory]
    [InlineData(400, "{\"err_code\":\"INVALID_AUDIO\",\"err_msg\":\"secret transcript\"}", TranscriptionFailure.DefiniteRejection, false)]
    [InlineData(400, "{\"err_code\":\"Bad Request\",\"err_msg\":\"Bad Request: failed to process audio: corrupt or unsupported data\"}", TranscriptionFailure.DefiniteRejection, false)]
    [InlineData(400, "{\"err_code\":\"Bad Request\",\"request_id\":\"33333333-3333-4333-8333-333333333333\"}", TranscriptionFailure.DefiniteRejection, false)]
    [InlineData(400, "{\"err_code\":\"Bad Request\",\"request_id\":\"bad\"}", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(400, "{\"err_code\":\"Bad Request\",\"results\":{}}", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(400, "{\"err_code\":\"Bad Request\",\"err_code\":\"Bad Request\"}", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(429, "{\"err_code\":\"TOO_MANY_REQUESTS\",\"err_msg\":\"DEEPGRAM-SECRET\"}", TranscriptionFailure.DefiniteRejection, true)]
    [InlineData(429, "{\"err_code\":\"TOO_MANY_REQUESTS\",\"request_id\":\"33333333-3333-4333-8333-333333333333\"}", TranscriptionFailure.DefiniteRejection, true)]
    [InlineData(429, "{\"err_code\":\"TOO_MANY_REQUESTS\",\"request_id\":\"bad\"}", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(401, "{\"err_code\":\"INVALID_AUTH\"}", TranscriptionFailure.DefiniteRejection, false)]
    [InlineData(429, "bad gateway", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(503, "{\"err_code\":\"SERVICE_UNAVAILABLE\"}", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(408, "{}", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(200, "{}", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(200, "{\"request_id\":\"bad\"}", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(200, "{\"request_id\":\"33333333-3333-4333-8333-333333333333\",\"err_code\":\"BAD\"}", TranscriptionFailure.UncertainAcceptance, false)]
    [InlineData(200, "{\"request_id\":\"33333333-3333-4333-8333-333333333333\",\"request_id\":\"33333333-3333-4333-8333-333333333333\"}", TranscriptionFailure.UncertainAcceptance, false)]
    public async Task SeparatesDefiniteRejectionFromUncertainAcceptance(int status, string response, TranscriptionFailure failure, bool retryable)
    {
        var factory = new Factory(async (request, cancellationToken) => { await request.Content!.CopyToAsync(Stream.Null, cancellationToken); return Json(response, (HttpStatusCode)status); });
        await using var audio = new TranscriptionAudio(new MemoryStream(Wave()), Wave().Length);
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => new DeepgramClient(factory, Options()).SubmitAsync(audio, Snapshot, Identity(), Credential(), default));
        Assert.Equal(failure, error.Failure);
        Assert.Equal(retryable, error.Retryable);
        Assert.DoesNotContain("secret transcript", error.ToString());
        Assert.DoesNotContain("DEEPGRAM-SECRET", error.ToString());
        Assert.DoesNotContain("CALLBACK.HEADER.SIGNATURE", error.ToString());
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData("Bad Request", "Bad Request: No such model/language/tier combination found.", "BAD_REQUEST", "UNSUPPORTED_MODEL_LANGUAGE_TIER")]
    [InlineData("Bad Request", "Bad Request: failed to process audio: corrupt or unsupported data", "BAD_REQUEST", "CORRUPT_OR_UNSUPPORTED_AUDIO")]
    [InlineData("INVALID_QUERY_PARAMETER", "Invalid callback url.", "INVALID_QUERY_PARAMETER", "INVALID_CALLBACK_URL")]
    [InlineData("INVALID_QUERY_PARAMETER", "Failed to deserialize query parameters: CALLBACK.HEADER.SIGNATURE DEEPGRAM-SECRET", "INVALID_QUERY_PARAMETER", "INVALID_QUERY_PARAMETERS")]
    [InlineData("DEEPGRAM_SECRET", "CALLBACK.HEADER.SIGNATURE DEEPGRAM-SECRET secret transcript", "UNRECOGNIZED", "UNSPECIFIED")]
    public async Task LogsOnlySafeDeepgramErrorCodesAndReasons(string code, string message, string safeCode, string safeReason)
    {
        var logs = new Logs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var factory = new Factory(async (request, cancellationToken) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            return Json(JsonSerializer.Serialize(new { err_code = code, err_msg = message, request_id = RequestId }), HttpStatusCode.BadRequest);
        });
        await using var audio = new TranscriptionAudio(new MemoryStream(Wave()), Wave().Length);
        var failure = await Assert.ThrowsAsync<TranscriptionException>(() =>
            new DeepgramClient(factory, Options(), loggerFactory.CreateLogger<DeepgramClient>())
                .SubmitAsync(audio, Snapshot, Identity(), Credential(), default));
        Assert.Equal(TranscriptionFailure.DefiniteRejection, failure.Failure);
        Assert.False(failure.Retryable);
        var entry = Assert.Single(logs.Entries.Where(item => item.ContainsKey("ProviderErrorCode")));
        Assert.Equal(safeCode, entry["ProviderErrorCode"]);
        Assert.Equal(safeReason, entry["ProviderErrorReason"]);
        Assert.Equal(400, entry["HttpStatus"]);
        string output = string.Join('\n', logs.Messages) + failure;
        foreach (string secret in new[] { "CALLBACK.HEADER.SIGNATURE", "DEEPGRAM-SECRET", "DEEPGRAM_SECRET", "secret transcript", "callback=" })
            Assert.DoesNotContain(secret, output);
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderMessageDiagnosticFlagIsBoundFromConfiguration(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transcription:LogSanitizedProviderErrorMessages"] = enabled.ToString()
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTranscriptionClients();
        using var provider = services.BuildServiceProvider();
        Assert.Equal(enabled, provider.GetRequiredService<TranscriptionOptions>().LogSanitizedProviderErrorMessages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryProviderMessageLoggingIsOptInAndRedactsCredentialsAndUrls(bool enabled)
    {
        var logs = new Logs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Error).AddProvider(logs));
        var identity = Identity();
        var credential = Credential();
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(identity.CorrelationId.ToString("D") + ":" + credential.Token));
        const string otherJwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJzZWNyZXQifQ.c2lnbmF0dXJl";
        const string rawUrl = "https://user:password@example.com/callback?token=private-value";
        string encodedUrl = Uri.EscapeDataString(rawUrl);
        string providerMessage = $"Invalid request: callback too long; {credential.Token} {otherJwt} {basic} " +
            $"DEEPGRAM-SECRET LOGIN-NAME LOGIN-SECRET TWILIO-SECRET {identity.SubmissionToken:D} {identity.SubmissionToken:N} " +
            $"{rawUrl} {encodedUrl}\r\nmessage ended.";
        var factory = new Factory((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new { err_code = "Bad Request", err_msg = providerMessage }), HttpStatusCode.BadRequest)));
        await using var audio = new TranscriptionAudio(new MemoryStream(Wave()), Wave().Length);
        var error = await Assert.ThrowsAsync<TranscriptionException>(() =>
            new DeepgramClient(factory, Options(logProviderMessages: enabled), loggerFactory.CreateLogger<DeepgramClient>())
                .SubmitAsync(audio, Snapshot, identity, credential, default));
        Assert.Equal(TranscriptionFailure.DefiniteRejection, error.Failure);
        Assert.Equal("PROVIDER_REJECTED", error.ErrorCode);
        Assert.Equal(400, error.ProviderHttpStatus);
        Assert.Equal(enabled, error.ProviderMessageLoggingEnabled);
        var diagnostic = Assert.Single(logs.Entries.Where(item => item.ContainsKey("ProviderErrorCode")));
        Assert.Equal(enabled, diagnostic["ProviderMessageLoggingEnabled"]);
        var entries = logs.Entries.Where(item => item.ContainsKey("ProviderErrorCode") && item.ContainsKey("SanitizedProviderMessage")).ToArray();
        if (enabled)
        {
            string sanitized = Assert.IsType<string>(Assert.Single(entries)["SanitizedProviderMessage"]);
            Assert.StartsWith("Invalid request: callback too long;", sanitized);
            Assert.EndsWith("\\u000d\\u000amessage ended.", sanitized);
            Assert.Contains("[REDACTED_URL]", sanitized);
            Assert.Contains("[REDACTED_JWT]", sanitized);
        }
        else
        {
            Assert.Empty(entries);
            Assert.DoesNotContain("callback too long", string.Join('\n', logs.Messages));
        }
        string output = string.Join('\n', logs.Messages);
        foreach (string secret in new[] { credential.Token, otherJwt, basic, "DEEPGRAM-SECRET", "LOGIN-NAME", "LOGIN-SECRET",
            "TWILIO-SECRET", identity.SubmissionToken.ToString("D"), identity.SubmissionToken.ToString("N"),
            rawUrl, encodedUrl, "private-value", "user:password" })
            Assert.DoesNotContain(secret, output);
        Assert.False(new TranscriptionOptions().LogSanitizedProviderErrorMessages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredSubmissionPipelineIncludesSanitizedMessageInExistingFailureEntries(bool enabled)
    {
        var logs = new Logs();
        var events = new List<string>();
        var providers = new Providers(events);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error).AddProvider(logs));
        services.AddTranscriptionClients();
        services.AddSingleton(Options(logProviderMessages: enabled));
        services.AddSingleton<ITranscriptionSubmissionRepository>(new SubmissionRepository(events));
        services.AddSingleton<ICallbackTokenClient>(providers);
        services.AddSingleton<ITwilioRecordingClient>(providers);
        services.AddSingleton<IHttpClientFactory>(new Factory(async (request, cancellationToken) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            return Json("{\"err_code\":\"Bad Request\",\"err_msg\":\"Actual explanation: CALLBACK.HEADER.SIGNATURE DEEPGRAM-SECRET\"}", HttpStatusCode.BadRequest);
        }));
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<ITranscriptionSubmissionService>().SubmitAsync(Work(), "worker", default);

        Assert.Equal(TranscriptionFailure.DefiniteRejection, result.Failure);
        Assert.Equal("PROVIDER_REJECTED", result.ErrorCode);
        foreach (string operation in new[] { "Deepgram upload", "Provider upload" })
        {
            var entry = Assert.Single(logs.Entries.Where(item => item.TryGetValue("Operation", out var value) && Equals(value, operation)));
            Assert.Equal(400, entry["HttpStatus"]);
            Assert.Equal(enabled, entry["ProviderMessageLoggingEnabled"]);
            Assert.Equal(enabled ? "Actual explanation: [REDACTED] [REDACTED]" : null, entry["SanitizedProviderMessage"]);
        }
        string output = string.Join('\n', logs.Messages);
        foreach (string secret in new[] { "CALLBACK.HEADER.SIGNATURE", "DEEPGRAM-SECRET" })
            Assert.DoesNotContain(secret, output);
        if (!enabled) Assert.DoesNotContain("Actual explanation", output);
    }

    [Fact]
    public async Task TemporaryProviderMessageLoggingPreservesCompleteNonSensitiveMessage()
    {
        string message = "Invalid request: " + new string('x', 8000) + " final explanation.";
        var logs = new Logs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var factory = new Factory((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new { err_code = "Bad Request", err_msg = message }), HttpStatusCode.BadRequest)));
        await using var audio = new TranscriptionAudio(new MemoryStream(Wave()), Wave().Length);
        await Assert.ThrowsAsync<TranscriptionException>(() =>
            new DeepgramClient(factory, Options(logProviderMessages: true), loggerFactory.CreateLogger<DeepgramClient>())
                .SubmitAsync(audio, Snapshot, Identity(), Credential(), default));
        var entry = Assert.Single(logs.Entries.Where(item => item.ContainsKey("ProviderErrorCode") && item.ContainsKey("SanitizedProviderMessage")));
        Assert.Equal(message, entry["SanitizedProviderMessage"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidQueryDiagnosticsIncludeExactOptionsAndMeasuredLengthsWithoutCallbackCredentials(bool enabled)
    {
        var logs = new Logs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        string snapshot = Snapshot.Replace("\"punctuate\":true", "\"punctuate\":false", StringComparison.Ordinal);
        var identity = Identity();
        var credential = Credential();
        int targetBytes = 0, callbackLength = 0;
        var factory = new Factory(async (request, cancellationToken) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            targetBytes = Encoding.UTF8.GetByteCount(request.RequestUri!.PathAndQuery);
            Assert.DoesNotContain("callback_method", request.RequestUri.Query);
            Assert.Equal(11, request.RequestUri.Query.TrimStart('?').Split('&').Length);
            string callback = request.RequestUri.Query.TrimStart('?').Split('&').Single(item => item.StartsWith("callback=", StringComparison.Ordinal))[9..];
            callbackLength = Uri.UnescapeDataString(callback).Length;
            return Json(JsonSerializer.Serialize(new { err_code = "Bad Request", err_msg = "Invalid query string.", request_id = RequestId }), HttpStatusCode.BadRequest);
        });
        await using var audio = new TranscriptionAudio(new MemoryStream(Wave()), Wave().Length);
        var error = await Assert.ThrowsAsync<TranscriptionException>(() =>
            new DeepgramClient(factory, Options(logProviderMessages: enabled), loggerFactory.CreateLogger<DeepgramClient>())
                .SubmitAsync(audio, snapshot, identity, credential, default));
        Assert.Equal(TranscriptionFailure.DefiniteRejection, error.Failure);
        var failureEntry = Assert.Single(logs.Entries.Where(item => item.TryGetValue("Operation", out var value) && Equals(value, "Deepgram upload")));
        if (enabled)
        {
            string summary = Assert.IsType<string>(failureEntry["SanitizedProviderRequest"]);
            Assert.Contains("model=nova-3&version=latest&language=en&utterances=true&multichannel=true&diarize=false&punctuate=false&smart_format=false&mip_opt_out=true", summary);
            Assert.Contains("callback=[REDACTED]&tag=[REDACTED]", summary);
            Assert.DoesNotContain("callback_method", summary);
            Assert.Contains($"request target bytes={targetBytes}", summary);
            Assert.Contains($"callback URL characters={callbackLength}", summary);
            Assert.Contains($"callback credential characters={credential.Token.Length}", summary);
            Assert.Contains($"provider request ID={RequestId}", summary);
        }
        else
            Assert.Null(failureEntry["SanitizedProviderRequest"]);
        string output = string.Join('\n', logs.Messages);
        foreach (string secret in new[] { credential.Token, identity.CorrelationId.ToString("D"), "feedback.example.com", "DEEPGRAM-SECRET" })
            Assert.DoesNotContain(secret, output);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("extra")]
    [InlineData("interrupted")]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    public async Task InterruptedUploadsNeverClaimDefiniteRejectionOrRetryPost(string interruption)
    {
        byte[] wave = Wave();
        byte[] media = interruption == "truncated" ? wave[..^100] : interruption == "extra" ? [.. wave, 1] : wave;
        using var cancellation = new CancellationTokenSource();
        var factory = new Factory(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get) return Wav(media, false);
            if (interruption == "interrupted") throw new HttpRequestException(request.RequestUri!.ToString() + " DEEPGRAM-SECRET");
            if (interruption == "cancelled") cancellation.Cancel();
            if (interruption is "timeout" or "cancelled") await Task.Delay(Timeout.Infinite, cancellationToken);
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            return Json($"{{\"request_id\":\"{RequestId}\"}}");
        });
        await using var audio = await new TwilioRecordingClient(factory, Options(timeout: 1)).OpenAsync(Work(), cancellation.Token);
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => new DeepgramClient(factory, Options(timeout: 1)).SubmitAsync(audio, Snapshot, Identity(), Credential(), cancellation.Token));
        Assert.Equal(TranscriptionFailure.UncertainAcceptance, error.Failure);
        Assert.DoesNotContain("DEEPGRAM-SECRET", error.ToString());
        Assert.DoesNotContain("callback=", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CALLBACK.HEADER.SIGNATURE", error.ToString());
        Assert.Equal(2, factory.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model\":\"nova-3\",\"utterances\":true,\"multichannel\":true}")]
    public async Task RejectsIncompleteSnapshotsWithoutNetworkTraffic(string configuration)
    {
        var factory = new Factory((_, _) => throw new InvalidOperationException());
        await using var audio = new TranscriptionAudio(new MemoryStream(Wave()), Wave().Length);
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => new DeepgramClient(factory, Options()).SubmitAsync(audio, configuration, Identity(), Credential(), default));
        Assert.Equal(TranscriptionFailure.Validation, error.Failure);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task TreatsUnexpectedAcceptanceMimeAndOversizedResponsesAsUncertain()
    {
        foreach (bool oversized in new[] { false, true })
        {
            var response = oversized ? Json(new string('x', 65537)) : new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"{{\"request_id\":\"{RequestId}\"}}", Encoding.UTF8, "text/html") };
            var factory = new Factory((_, _) => Task.FromResult(response));
            await using var audio = new TranscriptionAudio(new MemoryStream(Wave()), Wave().Length);
            var error = await Assert.ThrowsAsync<TranscriptionException>(() => new DeepgramClient(factory, Options()).SubmitAsync(audio, Snapshot, Identity(), Credential(), default));
            Assert.Equal(TranscriptionFailure.UncertainAcceptance, error.Failure);
            Assert.Equal(1, factory.Calls);
        }
    }

    [Theory]
    [InlineData("https://identity.example")]
    [InlineData("https://identity.example/legacy/")]
    [InlineData("http://172.27.112.243:8085/legacy/")]
    public void AuthenticationUsesTheExactRootedRouteWithoutChangingJwks(string baseUrl)
    {
        var identity = Options(identityBase: baseUrl, allowHttp: true).Identity;
        identity.Validate();
        string origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        Assert.Equal(origin + "/security/v2/auth/authenticate",
            identity.Endpoint(TranscriptionIdentityOptions.AuthenticationPath).AbsoluteUri);
        Assert.Equal(origin + "/.well-known/jwks.json", identity.Endpoint(identity.JwtJwksPath).AbsoluteUri);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"jwt\":null}")]
    [InlineData("{\"jwt\":\"\"}")]
    [InlineData("{\"jwt\":\"  \"}")]
    [InlineData("{\"jwt\":123}")]
    [InlineData("[]")]
    public async Task RejectsMissingOrInvalidJwtBeforeJwksRetrieval(string response)
    {
        var factory = new Factory((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/security/v2/auth/authenticate", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(response));
        });
        var failure = await Assert.ThrowsAsync<TranscriptionException>(() =>
            new CallbackTokenClient(factory, Options()).AcquireAsync(TimeSpan.FromMinutes(70), default));
        Assert.Equal("INVALID_IDENTITY_RESPONSE", failure.ErrorCode);
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData("12, 1188,13", 120, true)]
    [InlineData("11188", 120, false)]
    [InlineData("1188,", 120, false)]
    [InlineData("1188", 30, false)]
    [InlineData("1188", -1, false)]
    public async Task AcquiresAndValidatesExactPermissionAndRemainingValidity(string permissions, int expiryMinutes, bool valid)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "test" };
        string jwt = Token(signingKey, permissions, DateTime.UtcNow.AddMinutes(expiryMinutes));
        var factory = IdentityFactory(jwt, Jwks(rsa));
        var client = new CallbackTokenClient(factory, Options());
        if (valid)
        {
            var credential = await client.AcquireAsync(TimeSpan.FromMinutes(70), default);
            Assert.Equal(jwt, credential.Token);
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(jwt)), credential.Hash);
            Assert.DoesNotContain(jwt, credential.ToString());
            await client.AcquireAsync(TimeSpan.FromMinutes(70), default);
            Assert.Equal(3, factory.Calls);
        }
        else
        {
            var error = await Assert.ThrowsAsync<TranscriptionException>(() => client.AcquireAsync(TimeSpan.FromMinutes(70), default));
            Assert.Equal(TranscriptionFailure.Validation, error.Failure);
            Assert.DoesNotContain(jwt, error.ToString());
        }
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(70, false)]
    [InlineData(71, true)]
    public async Task LogsInsufficientLiveTokenLifetimeWithoutSecrets(int expiryMinutes, bool valid)
    {
        using var rsa = RSA.Create(2048);
        string jwt = Token(new RsaSecurityKey(rsa) { KeyId = "test" }, "1188", DateTime.UtcNow.AddMinutes(expiryMinutes));
        var logs = new Logs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var client = new CallbackTokenClient(IdentityFactory(jwt, Jwks(rsa)), Options(), loggerFactory.CreateLogger<CallbackTokenClient>());
        var minimumValidity = TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(600 + 5);
        if (valid)
        {
            var credential = await client.AcquireAsync(minimumValidity, default);
            Assert.Equal(jwt, credential.Token);
            Assert.Empty(logs.Messages);
        }
        else
        {
            var error = await Assert.ThrowsAsync<TranscriptionException>(() => client.AcquireAsync(minimumValidity, default));
            Assert.Equal("INVALID_CALLBACK_TOKEN", error.ErrorCode);
            Assert.False(error.Retryable);
            var entry = Assert.Single(logs.Entries.Where(item => item.ContainsKey("TokenRejectionReason")));
            Assert.Equal("INSUFFICIENT_REMAINING_VALIDITY", entry["TokenRejectionReason"]);
            var now = Assert.IsType<DateTime>(entry["CurrentUtc"]);
            var expiry = Assert.IsType<DateTime>(entry["ExpiresUtc"]);
            Assert.Equal(minimumValidity.TotalSeconds, Assert.IsType<double>(entry["RequiredValiditySeconds"]));
            Assert.Equal(now.Add(minimumValidity), Assert.IsType<DateTime>(entry["RequiredValidUntilUtc"]));
            Assert.Equal((expiry - now).TotalSeconds, Assert.IsType<double>(entry["RemainingValiditySeconds"]));
        }
        string output = string.Join('\n', logs.Messages);
        foreach (string secret in new[] { jwt, "LOGIN-NAME", "LOGIN-SECRET" })
            Assert.DoesNotContain(secret, output);
    }

    [Fact]
    public async Task DefaultJobBudgetAcceptsFreshOneHourTokenThroughSubmission()
    {
        using var rsa = RSA.Create(2048);
        string jwt = Token(new RsaSecurityKey(rsa) { KeyId = "test" }, "1188", DateTime.UtcNow.AddHours(1));
        var options = new TranscriptionOptions
        {
            Enabled = true, DeepgramApiKey = "DEEPGRAM-SECRET",
            Identity = Options().Identity, Twilio = Options().Twilio
        };
        var work = Work();
        Assert.Equal(45, work.CallbackWaitMinutes);
        Assert.Equal(600, options.SubmissionTimeoutSeconds);
        Assert.Equal(3305, work.CallbackWaitMinutes * 60 + options.SubmissionTimeoutSeconds + 5);
        var events = new List<string>();
        var repository = new SubmissionRepository(events);
        var providers = new Providers(events);
        var tokens = new CallbackTokenClient(IdentityFactory(jwt, Jwks(rsa)), options);
        var service = new TranscriptionSubmissionService(repository, tokens, providers, providers, options);

        var result = await service.SubmitAsync(work, "worker", default);

        Assert.Equal("accepted", result.Outcome);
        Assert.Equal(["media", "create", "begin", "post", "save"], events);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(jwt)), repository.Hash);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("key")]
    [InlineData("permissions-type")]
    [InlineData("missing-expiry")]
    [InlineData("not-before")]
    public async Task RejectsInvalidIdentityTokenTrust(string invalid)
    {
        using var rsa = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = invalid == "issuer" ? "other" : "sentineldna.com", Audience = invalid == "audience" ? "other" : "sentineldna.com",
            Claims = new Dictionary<string, object> { ["permissions"] = invalid == "permissions-type" ? new[] { "1188" } : "1188" },
            Expires = invalid == "missing-expiry" ? null : DateTime.UtcNow.AddHours(2),
            NotBefore = invalid == "not-before" ? DateTime.UtcNow.AddHours(1) : DateTime.UtcNow.AddSeconds(-1),
            SigningCredentials = new(new RsaSecurityKey(invalid == "key" ? other : rsa) { KeyId = "test" }, "RS256")
        };
        string jwt = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
        var logs = new Logs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => new CallbackTokenClient(IdentityFactory(jwt, Jwks(rsa)), Options(),
            loggerFactory.CreateLogger<CallbackTokenClient>()).AcquireAsync(TimeSpan.FromMinutes(70), default));
        Assert.Equal(TranscriptionFailure.Validation, error.Failure);
        Assert.DoesNotContain(jwt, error.ToString());
        Assert.Contains(logs.Messages, text => text.Contains(invalid == "permissions-type"
            ? "MISSING_OR_INVALID_PERMISSIONS_CLAIM" : "TOKEN_VALIDATION_FAILED", StringComparison.Ordinal));
        Assert.DoesNotContain(jwt, string.Join('\n', logs.Messages));
    }

    [Fact]
    public async Task SupportsEs256AndPinsTheConfiguredHttpIdentityException()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ec.ExportParameters(false);
        string jwks = JsonSerializer.Serialize(new { keys = new[] { new { kty = "EC", kid = "test", use = "sig", alg = "ES256", crv = "P-256", x = Base64UrlEncoder.Encode(parameters.Q.X!), y = Base64UrlEncoder.Encode(parameters.Q.Y!) } } });
        string jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        { Issuer = "sentineldna.com", Audience = "sentineldna.com", Claims = new Dictionary<string, object> { ["permissions"] = "1188" }, Expires = DateTime.UtcNow.AddHours(2), SigningCredentials = new(new ECDsaSecurityKey(ec) { KeyId = "test" }, "ES256") });
        var factory = IdentityFactory(jwt, jwks, "http://172.27.112.243:8085");
        var credential = await new CallbackTokenClient(factory, Options(algorithm: "ES256", identityBase: "http://172.27.112.243:8085", allowHttp: true)).AcquireAsync(TimeSpan.FromMinutes(70), default);
        Assert.Equal(jwt, credential.Token);
        var disabledFactory = IdentityFactory(jwt, jwks);
        await Assert.ThrowsAsync<TranscriptionException>(() => new CallbackTokenClient(disabledFactory, Options(identityBase: "http://172.27.112.243:8085")).AcquireAsync(TimeSpan.FromMinutes(70), default));
        Assert.Equal(0, disabledFactory.Calls);
    }

    [Fact]
    public async Task PersistsIdentityAndHashBeforeAudioAndSavesAcceptanceAfterHostCancellation()
    {
        var events = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var repo = new SubmissionRepository(events);
        var providers = new Providers(events) { OnSubmit = () => cancellation.Cancel() };
        var result = await new TranscriptionSubmissionService(repo, providers, providers, providers, Options()).SubmitAsync(Work(), "worker", cancellation.Token);
        Assert.Equal("accepted", result.Outcome);
        Assert.Equal(["token", "media", "create", "begin", "post", "save"], events);
        Assert.Equal(Credential().Hash, repo.Hash);
        Assert.False(repo.SaveTokenWasCancelled);
        Assert.Equal(RequestId, result.RequestId);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("begin")]
    [InlineData("blocked")]
    [InlineData("save")]
    public async Task PersistenceFailuresOrReplayNeverCauseUnmarkedOrRepeatedPost(string failure)
    {
        var events = new List<string>();
        var logs = new Logs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var repo = new SubmissionRepository(events) { Failure = failure };
        var providers = new Providers(events);
        var service = new TranscriptionSubmissionService(repo, providers, providers, providers, Options(),
            loggerFactory.CreateLogger<TranscriptionSubmissionService>());
        var result = await service.SubmitAsync(Work(), "worker", default);
        Assert.Equal(failure == "save", events.Contains("post"));
        if (failure == "save") Assert.Equal(TranscriptionFailure.Persistence, result.Failure);
        if (failure is "blocked" or "begin") Assert.DoesNotContain("save", events);
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain("CALLBACK.HEADER.SIGNATURE", string.Join('\n', logs.Messages));
    }

    [Theory]
    [InlineData("identity", "IDENTITY_UNAVAILABLE")]
    [InlineData("media", "MEDIA_UNAVAILABLE")]
    public async Task DependencyErrorsAreLoggedBeforeMaskingWithoutSecrets(string stage, string code)
    {
        var logs = new Logs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var factory = new Factory((_, _) => throw new HttpRequestException("LOGIN-SECRET TWILIO-SECRET secret transcript"));
        TranscriptionException failure;
        if (stage == "identity")
            failure = await Assert.ThrowsAsync<TranscriptionException>(() =>
                new CallbackTokenClient(factory, Options(), loggerFactory.CreateLogger<CallbackTokenClient>())
                    .AcquireAsync(TimeSpan.FromMinutes(1), default));
        else
            failure = await Assert.ThrowsAsync<TranscriptionException>(() =>
                new TwilioRecordingClient(factory, Options(), loggerFactory.CreateLogger<TwilioRecordingClient>())
                    .OpenAsync(Work(), default));
        Assert.Equal(code, failure.ErrorCode);
        Assert.Contains(logs.Messages, text => text.Contains("HttpRequestException", StringComparison.Ordinal));
        string output = string.Join('\n', logs.Messages);
        foreach (string secret in new[] { "LOGIN-SECRET", "TWILIO-SECRET", "secret transcript", "CALLBACK.HEADER.SIGNATURE" })
            Assert.DoesNotContain(secret, output);
    }

    [Fact]
    public async Task NamedClientsHaveNoCredentialBearingHttpLogs()
    {
        var logger = new Logs();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging(builder => builder.ClearProviders().AddProvider(logger));
        services.AddTranscriptionClients();
        services.AddSingleton(Options());
        services.AddHttpClient(DeepgramClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(async (request, cancellationToken) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            throw new HttpRequestException(request.RequestUri + " DEEPGRAM-SECRET secret transcript");
        }));
        using var provider = services.BuildServiceProvider();
        await using var audio = new TranscriptionAudio(new MemoryStream(Wave()), Wave().Length);
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => provider.GetRequiredService<IDeepgramClient>().SubmitAsync(audio, Snapshot, Identity(), Credential(), default));
        string output = string.Join('\n', logger.Messages) + error;
        foreach (string secret in new[] { "DEEPGRAM-SECRET", "CALLBACK.HEADER.SIGNATURE", "secret transcript", "callback=" }) Assert.DoesNotContain(secret, output);
        Assert.Contains(logger.Messages, text => text.Contains("Deepgram upload", StringComparison.Ordinal) && text.Contains("HttpRequestException", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, text => text.Contains("Start processing HTTP request", StringComparison.Ordinal));
    }

    private static string Token(SecurityKey key, string permissions, DateTime expires) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    { Issuer = "sentineldna.com", Audience = "sentineldna.com", Claims = new Dictionary<string, object> { ["permissions"] = permissions }, Expires = expires, NotBefore = DateTime.UtcNow.AddHours(-1), IssuedAt = DateTime.UtcNow.AddHours(-1), SigningCredentials = new(key, "RS256") });
    private static string Jwks(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "test", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) } } });
    }
    private static Factory IdentityFactory(string jwt, string jwks, string root = "https://identity.example") => new(async (request, cancellationToken) =>
    {
        if (request.Method == HttpMethod.Get) { Assert.Equal(root + "/.well-known/jwks.json", request.RequestUri!.AbsoluteUri); return Json(jwks); }
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(root + "/security/v2/auth/authenticate", request.RequestUri!.AbsoluteUri);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"signInname\":\"LOGIN-NAME\",\"password\":\"LOGIN-SECRET\"}", await request.Content.ReadAsStringAsync(cancellationToken));
        return Json(JsonSerializer.Serialize(new { jwt }));
    });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(new Handler((request, token) => { Calls++; return send(request, token); })) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    private sealed class SubmissionRepository(List<string> events) : ITranscriptionSubmissionRepository
    {
        public string Failure { get; init; } = "";
        public byte[]? Hash { get; private set; }
        public bool SaveTokenWasCancelled { get; private set; }
        public Task<TranscriptionCreation> CreateAsync(TranscriptionWork work, string workerId, TranscriptionSubmissionIdentity identity, CallbackCredential credential, CancellationToken cancellationToken)
        { events.Add("create"); Hash = credential.Hash; if (Failure == "create") throw new Exception("LOGIN-SECRET"); return Task.FromResult(new TranscriptionCreation("created", 1)); }
        public Task<bool> BeginAsync(TranscriptionWork work, string workerId, TranscriptionSubmissionIdentity identity, CancellationToken cancellationToken)
        { events.Add("begin"); if (Failure == "begin") throw new Exception("LOGIN-SECRET"); return Task.FromResult(Failure != "blocked"); }
        public Task<string> SaveAcceptanceAsync(TranscriptionSubmissionIdentity identity, string requestId, CancellationToken cancellationToken)
        { events.Add("save"); SaveTokenWasCancelled = cancellationToken.IsCancellationRequested; if (Failure == "save") throw new Exception("LOGIN-SECRET"); return Task.FromResult("saved"); }
    }
    private sealed class Providers(List<string> events) : ICallbackTokenClient, ITwilioRecordingClient, IDeepgramClient
    {
        public Action? OnSubmit { get; init; }
        public Task<CallbackCredential> AcquireAsync(TimeSpan minimumValidity, CancellationToken cancellationToken) { events.Add("token"); return Task.FromResult(Credential()); }
        public Task<TranscriptionAudio> OpenAsync(TranscriptionWork work, CancellationToken cancellationToken) { events.Add("media"); return Task.FromResult(new TranscriptionAudio(new MemoryStream(Wave()), Wave().Length)); }
        public Task<string> SubmitAsync(TranscriptionAudio audio, string configurationJSON, TranscriptionSubmissionIdentity identity, CallbackCredential credential, CancellationToken cancellationToken) { events.Add("post"); OnSubmit?.Invoke(); return Task.FromResult(RequestId); }
    }
    private sealed class Logs : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];
        public List<Dictionary<string, object?>> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                Entries.Add(properties.ToDictionary(item => item.Key, item => item.Value));
        }
    }
}