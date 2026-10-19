using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Xml;

namespace NotificationWorkflowService.Service.AutoCall;

internal sealed class AutoCallValidationException(string message) : InvalidOperationException(message);

internal static class AutoCallDiagnostics
{
    internal static void Failure(ILogger logger, string operation, Exception exception,
        int? queueId = null, int? callId = null, string? cancellationReason = null,
        LogLevel level = LogLevel.Error)
    {
        // External messages and exception objects can contain SQL values, phone numbers,
        // request bodies or credentials. Only our validation messages are safe to include.
        string reason = exception switch
        {
            AutoCallValidationException validation => validation.Message,
            AutoCallRoutingException routing => routing.Failure.ToString(),
            TwilioSubmissionException => "Twilio submission did not return a validated acceptance.",
            OperationCanceledException => cancellationReason ?? "Operation canceled.",
            _ => "External exception message omitted; inspect stack trace and error metadata."
        };
        var sql = exception as SqlException;
        var json = exception as JsonException;
        var xml = exception as XmlException;
        string? providerCode = exception is TwilioSubmissionException submission && IsProviderCode(submission.ErrorCode)
            ? submission.ErrorCode : null;
        logger.Log(level,
            "Auto-call {Operation} failed for queue {QueueId}, call {CallId}; error type {FailureType}, reason {FailureReason}, cancellation {CancellationReason}, provider code {ProviderErrorCode}, HTTP status {HttpStatus}, SQL number {SqlNumber}, state {SqlState}, procedure {SqlProcedure}, line {SqlLine}, JSON line {JsonLine}, byte {JsonByte}, XML line {XmlLine}, position {XmlPosition}; stack {FailureStackTrace}",
            operation, queueId, callId, exception.GetType().FullName, reason, cancellationReason,
            providerCode,
            exception is HttpRequestException http ? (int?)http.StatusCode : null,
            sql?.Number, sql?.State, sql?.Procedure, sql?.LineNumber,
            json?.LineNumber, json?.BytePositionInLine, xml?.LineNumber, xml?.LinePosition,
            exception.StackTrace);
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                Failure(logger, operation + " (inner)", inner, queueId, callId, cancellationReason, level);
        }
        else if (exception.InnerException is { } inner)
            Failure(logger, operation + " (inner)", inner, queueId, callId, cancellationReason, level);
    }

    private static bool IsProviderCode(string code)
        => code is "ACCEPTANCE_UNKNOWN" or "INVALID_RESPONSE" ||
            code.StartsWith("HTTP_", StringComparison.Ordinal) && code.Length == 8 && code[5..].All(char.IsAsciiDigit) ||
            code.StartsWith("TWILIO_", StringComparison.Ordinal) && code.Length == 12 && code[7..].All(char.IsAsciiDigit);
}
