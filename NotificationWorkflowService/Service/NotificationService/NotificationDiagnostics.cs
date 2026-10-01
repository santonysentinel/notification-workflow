using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ActiveAlarmsParser.Service.NotificationService;

internal static class NotificationDiagnostics
{
    // Exception messages/inner exceptions can include raw JSON, URLs, credentials or SQL
    // values. Log only allowlisted metadata, never the exception object or its message.
    internal static void Failure(ILogger logger, string operation, Exception exception)
        => logger.LogError("Notification operation {Operation} failed; error type {ErrorType}, HTTP status {HttpStatus}, SQL number {SqlNumber}",
            operation, exception.GetType().Name,
            exception is HttpRequestException http ? (int?)http.StatusCode : null,
            exception is SqlException sql ? (int?)sql.Number : null);
}