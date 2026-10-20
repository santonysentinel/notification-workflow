using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace NotificationWorkflowService.Service.Transcription;

internal static class TranscriptionDiagnostics
{
    internal static void Failure(ILogger logger, string operation, Exception exception,
        int? jobId = null, int? recordingId = null, string? cancellationReason = null)
    {
        // External exception messages/objects may contain tokens, audio URLs or payloads.
        var failure = exception as TranscriptionException;
        var sql = exception as SqlException;
        var json = exception as JsonException;
        logger.LogError(
            "Transcription {Operation} failed for job {JobId}, recording {RecordingId}; error type {FailureType}, category {FailureCategory}, code {ErrorCode}, retryable {Retryable}, cancellation {CancellationReason}, HTTP status {HttpStatus}, SQL number {SqlNumber}, procedure {SqlProcedure}, line {SqlLine}, JSON line {JsonLine}, byte {JsonByte}; provider message logging enabled {ProviderMessageLoggingEnabled}, sanitized provider message {SanitizedProviderMessage}, sanitized provider request {SanitizedProviderRequest}; stack {FailureStackTrace}",
            operation, jobId, recordingId, exception.GetType().FullName,
            failure?.Failure, Code(failure?.ErrorCode), failure?.Retryable, cancellationReason,
            exception is HttpRequestException http ? (int?)http.StatusCode : failure?.ProviderHttpStatus,
            sql?.Number, sql?.Procedure, sql?.LineNumber, json?.LineNumber, json?.BytePositionInLine,
            failure?.ProviderMessageLoggingEnabled, failure?.SanitizedProviderMessage, failure?.SanitizedProviderRequest, exception.StackTrace);
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                Failure(logger, operation + " (inner)", inner, jobId, recordingId, cancellationReason);
        }
        else if (exception.InnerException is { } inner)
            Failure(logger, operation + " (inner)", inner, jobId, recordingId, cancellationReason);
    }

    internal static string? Code(string? value)
        => value is null ? null : value.Length is > 0 and <= 100 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                ? value : "UNRECOGNIZED";
}
