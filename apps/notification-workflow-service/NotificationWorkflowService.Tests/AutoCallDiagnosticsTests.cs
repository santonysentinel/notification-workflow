using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Service.AutoCall;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class AutoCallDiagnosticsTests
{
    internal sealed record Entry(LogLevel Level, string Message, Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    internal sealed class Logs<T> : ILogger<T>
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new(logLevel, formatter(state, exception), exception,
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
    }

    [Fact]
    public void SuppliedSnapshotLogsMissingOffenderNameWithoutParticipantValues()
    {
        var attempt = new AutoCallAttempt
        {
            CallId = 42,
            VariablesJson = """{"alarmname":"private alarm","last_location_time":null,"lat":null,"lon":null}""",
            TemplateJson = """{"schemaVersion":1,"initialStep":"step-1","steps":[{"id":"step-1","type":"redirect","transitions":{"next":"step-2"}},{"id":"step-2","type":"terminal"}]}""",
            TemplateTwiML = """<CallFlow initialStep="step-1"><Step id="step-1"><Response><Say>Hello, {{OffenderName}}</Say><Redirect>{{feedbackBaseUrl}}/webhooks/twilio/voice/next?callId={{callId}}&amp;executionId={{executionId}}</Redirect></Response></Step><Step id="step-2"><Response><Hangup/></Response></Step></CallFlow>"""
        };
        var failure = Assert.Throws<AutoCallValidationException>(() =>
            AutoCallBundlePreparer.Prepare(attempt, "https://feedback.example"));
        var logger = new Logs<AutoCallRecoveryService>();
        AutoCallDiagnostics.Failure(logger, "Preparation", failure, 1, 42);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("Required preparation variable 'OffenderName' is missing.", entry.Properties["FailureReason"]);
        Assert.Equal(1, entry.Properties["QueueId"]);
        Assert.Equal(42, entry.Properties["CallId"]);
        Assert.Contains("AutoCallBundlePreparer", Assert.IsType<string>(entry.Properties["FailureStackTrace"]));
        Assert.DoesNotContain("private alarm", entry.Message);
        Assert.DoesNotContain("<CallFlow", entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void ExternalAndInnerFailuresLogStacksWithoutMessages()
    {
        var logger = new Logs<AutoCallRecoveryService>();
        void ThrowInner() => throw new InvalidOperationException("secret phone +15551234567");
        var inner = Assert.Throws<InvalidOperationException>(ThrowInner);
        void ThrowOuter() => throw new HttpRequestException("secret URL", inner, System.Net.HttpStatusCode.BadGateway);
        var failure = Assert.Throws<HttpRequestException>(ThrowOuter);
        AutoCallDiagnostics.Failure(logger, "Lookup", failure, 1);
        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(502, logger.Entries.First().Properties["HttpStatus"]);
        Assert.All(logger.Entries, entry =>
        {
            Assert.NotNull(entry.Properties["FailureStackTrace"]);
            Assert.DoesNotContain("secret", entry.Message);
            Assert.DoesNotContain("+15551234567", entry.Message);
            Assert.Null(entry.Exception);
        });
    }

    [Fact]
    public void SqlFailureRetainsActionableMetadataWithoutRawMessage()
    {
        var logger = new Logs<AutoCallRecoveryService>();
        AutoCallDiagnostics.Failure(logger, "Renewal", NotificationHistoryRetryTests.CreateSqlException(2812), 1);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(2812, entry.Properties["SqlNumber"]);
        Assert.Equal("procedure", entry.Properties["SqlProcedure"]);
        Assert.Equal(1, entry.Properties["SqlLine"]);
        Assert.DoesNotContain("deadlock test message", entry.Message);
    }

    [Theory]
    [InlineData("TWILIO_21211", "TWILIO_21211")]
    [InlineData("HTTP_500", "HTTP_500")]
    [InlineData("ACCEPTANCE_UNKNOWN", "ACCEPTANCE_UNKNOWN")]
    [InlineData("INVALID_RESPONSE", "INVALID_RESPONSE")]
    [InlineData("SECRET_TOKEN", null)]
    public void ProviderCodesAreAllowlisted(string code, string? expected)
    {
        var logger = new Logs<AutoCallSubmissionService>();
        AutoCallDiagnostics.Failure(logger, "Submission", new TwilioSubmissionException(false, code), 1, 42);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expected, entry.Properties["ProviderErrorCode"]);
        Assert.DoesNotContain("SECRET_TOKEN", entry.Message);
    }

    [Fact]
    public void MalformedJsonLogsPositionWithoutPayload()
    {
        var logger = new Logs<AutoCallRecoveryService>();
        var json = Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("""{"secret":private}"""));
        AutoCallDiagnostics.Failure(logger, "Parse request", json, 1);
        var entry = Assert.Single(logger.Entries);
        Assert.NotNull(entry.Properties["JsonLine"]);
        Assert.NotNull(entry.Properties["JsonByte"]);
        Assert.DoesNotContain("secret", entry.Message);
        Assert.DoesNotContain("private", entry.Message);
    }

    private sealed class RoutingFailure : IAutoCallRoutingStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromException<string?>(new TimeoutException("secret Redis endpoint"));
    }

    private sealed class UnusedPhones : IAutoCallPhoneRepository
    {
        public Task<IReadOnlyList<AutoCallPhone>> ReadAsync(string connectionString, string oid, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task RedisFailureIsLoggedBeforeMasking()
    {
        var logger = new Logs<AutoCallContactService>();
        var service = new AutoCallContactService(new RoutingFailure(), new UnusedPhones(), new ConfigurationBuilder().Build(), logger);
        var failure = await Assert.ThrowsAsync<AutoCallRoutingException>(() => service.ResolveAsync("oid"));
        Assert.Equal(AutoCallRoutingFailure.Unavailable, failure.Failure);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("Redis routing lookup", entry.Properties["Operation"]);
        Assert.Equal(typeof(TimeoutException).FullName, entry.Properties["FailureType"]);
        Assert.DoesNotContain("secret Redis endpoint", entry.Message);
    }
}
