using ActiveAlarmsParser.Service.NotificationService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using static ActiveAlarmsParser.Service.NotificationService.NotificationServiceData;

namespace NotificationWorkflowService.Tests;

public class NotificationHardeningTests
{
    private const string Ack = "{\"isSuccessful\":true,\"oid\":\"o\",\"victimid\":\"v\",\"type\":\"push\",\"activityid\":\"1\"}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportsArrayAndLegacyEncodedArray(bool encoded)
    {
        var envelope = new JObject { ["object"] = encoded ? new JValue("[" + Ack + "]") : JArray.Parse("[" + Ack + "]") };
        var response = NotificationServiceResponse.ParseResponse(envelope.ToString());
        var ack = Assert.Single(response.data);
        Assert.True(ack.isSuccessful);
        Assert.Equal("v", ack.victimid);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"object\":null}")]
    [InlineData("{\"object\":{}}")]
    [InlineData("{\"object\":123}")]
    [InlineData("{\"object\":\"secret-malformed-payload\"}")]
    [InlineData("{\"object\":[null]}")]
    [InlineData("{\"object\":[{}]}")]
    [InlineData("{\"object\":[],\"object\":[]}")]
    public void InvalidEnvelopeIsRejectedWithoutPayloadInException(string json)
    {
        var failure = Assert.Throws<JsonSerializationException>(() => NotificationServiceResponse.ParseResponse(json));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("secret-malformed-payload", failure.ToString());
    }

    [Theory]
    [InlineData("isSuccessful", "\"true\"")]
    [InlineData("isSuccessful", "1")]
    [InlineData("isSuccessful", "null")]
    [InlineData("oid", "123")]
    [InlineData("victimid", "null")]
    [InlineData("activityid", "\" \"")]
    public void AcknowledgementDoesNotCoerceRequiredFields(string field, string value)
    {
        var ack = JObject.Parse(Ack);
        ack[field] = JToken.Parse(value);
        var envelope = new JObject { ["object"] = new JArray(ack) };
        Assert.Throws<JsonSerializationException>(() => NotificationServiceResponse.ParseResponse(envelope.ToString()));
    }

    [Fact]
    public void OptionalFieldsStillSerializeAsNullWithOriginalJsonNames()
    {
        var notification = new Notification { oid = "o", victimid = "v", type = "push", activityid = "1" };
        var json = JObject.Parse(notification.ToJSON());
        Assert.Equal(JTokenType.Null, json["messagetitle"]!.Type);
        Assert.Equal(JTokenType.Null, json["deliverytime"]!.Type);
        Assert.Equal("v", json.Value<string>("victimid"));
        Assert.False(json.ContainsKey("retries"));
        Assert.Equal(JTokenType.Null, JObject.Parse(JsonConvert.SerializeObject(new ReminderSetting()))["EventName"]!.Type);
    }

    [Fact]
    public void InvalidOutgoingBatchDoesNotPartiallyMutateQueue()
    {
        var queue = new NotificationArray();
        Assert.ThrowsAny<ArgumentException>(() => queue.BulkAdd(new System.Collections.ArrayList
        {
            new Notification { oid = "o", victimid = "v", type = "push", activityid = "1" },
            new Notification()
        }));
        Assert.Equal(0, queue.Count());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"jwt\":\"secret-token\"}")]
    [InlineData("{\"isSuccessful\":false,\"jwt\":\"secret-token\"}")]
    [InlineData("{\"isSuccessful\":\"true\",\"jwt\":\"secret-token\"}")]
    [InlineData("{\"isSuccessful\":true,\"jwt\":\" \"}")]
    [InlineData("{\"isSuccessful\":true,\"jwt\":123}")]
    [InlineData("{\"isSuccessful\":true,\"jwt\":\"a\",\"jwt\":\"b\"}")]
    [InlineData("secret-malformed-auth")]
    public void AuthenticationRejectsUnsuccessfulMissingOrInvalidToken(string json)
    {
        var failure = Assert.Throws<JsonSerializationException>(() => AuthServiceResponse.ParseResponse(json));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("secret-token", failure.ToString());
        Assert.DoesNotContain("secret-malformed-auth", failure.ToString());
    }

    [Fact]
    public void AuthenticationRequiresExplicitTrueAndNonemptyJwt()
    {
        var response = AuthServiceResponse.ParseResponse("{\"isSuccessful\":true,\"jwt\":\"token\"}");
        Assert.True(response.isSuccessful);
        Assert.Equal("token", response.jwt);
    }

    [Fact]
    public async Task TokenExpiresAtExactUtcBoundaryAndConcurrentCallsRefreshOnce()
    {
        var clock = new ManualClock();
        var cache = new NotificationTokenCache(TimeSpan.FromMinutes(5), clock);
        int authentications = 0;
        Task<string> Authenticate(CancellationToken _) => Task.FromResult("token-" + Interlocked.Increment(ref authentications));
        var initial = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetAsync(Authenticate, default)));
        Assert.All(initial, token => Assert.Equal("token-1", token));
        Assert.Equal(1, authentications);
        clock.Advance(TimeSpan.FromMinutes(5));
        var refreshed = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetAsync(Authenticate, default)));
        Assert.All(refreshed, token => Assert.Equal("token-2", token));
        Assert.Equal(2, authentications);
        await cache.InvalidateAsync("token-1", default);
        Assert.Equal("token-2", await cache.GetAsync(Authenticate, default));
        await cache.InvalidateAsync("token-2", default);
        Assert.Equal("token-3", await cache.GetAsync(Authenticate, default));
    }

    [Fact]
    public async Task InvalidAuthenticationIsNotCachedAndCancellationReleasesGate()
    {
        var cache = new NotificationTokenCache(TimeSpan.FromMinutes(5));
        Assert.Equal("", await cache.GetAsync(_ => Task.FromResult(" "), default));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<string>(token);
        }, cancellation.Token));
        Assert.Equal("valid", await cache.GetAsync(_ => Task.FromResult("valid"), default));
    }

    [Fact]
    public async Task InFlightAuthenticationIsSharedAndWaitingCallerCanCancel()
    {
        var cache = new NotificationTokenCache(TimeSpan.FromMinutes(5));
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Task<string> Authenticate(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return result.Task;
        }
        var first = cache.GetAsync(Authenticate, default);
        var followers = Enumerable.Range(0, 20).Select(_ => cache.GetAsync(Authenticate, default)).ToArray();
        using var cancellation = new CancellationTokenSource();
        var canceledFollower = cache.GetAsync(Authenticate, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledFollower);
        Assert.Equal(1, calls);
        result.SetResult("shared-token");
        Assert.Equal("shared-token", await first);
        Assert.All(await Task.WhenAll(followers), token => Assert.Equal("shared-token", token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DiagnosticsExcludeExceptionMessagesInnerExceptionsAndPayloads()
    {
        var logger = new RecordingLogger();
        NotificationDiagnostics.Failure(logger, "Delivery", new HttpRequestException("secret-password-payload",
            new Exception("secret-token"), System.Net.HttpStatusCode.BadRequest));
        Assert.Null(logger.Exception);
        Assert.Contains("Delivery", logger.Message);
        Assert.Contains("400", logger.Message);
        Assert.DoesNotContain("secret", logger.Message);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class RecordingLogger : ILogger
    {
        public string Message { get; private set; } = "";
        public Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
            Exception = exception;
        }
    }
}