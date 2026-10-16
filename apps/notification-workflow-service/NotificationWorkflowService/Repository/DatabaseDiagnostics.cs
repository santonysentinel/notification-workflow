using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace NotificationWorkflowService.Repository;

internal static class DatabaseDiagnostics
{
    internal static void Failure(ILogger logger, string database, string operation, Exception exception)
    {
        // SQL messages and exception objects can contain credentials or parameter values.
        var sql = exception as SqlException;
        logger.LogError(
            "Database {Database} operation {Operation} failed; error type {ErrorType}, SQL number {SqlNumber}, state {SqlState}, severity {SqlSeverity}, procedure {SqlProcedure}, line {SqlLine}, connection {SqlConnectionId}",
            database, operation, exception.GetType().Name, sql?.Number, sql?.State,
            sql?.Class, sql?.Procedure, sql?.LineNumber, sql?.ClientConnectionId);
    }

    internal static T Execute<T>(ILogger logger, string database, string operation, Func<T> execute)
    {
        try { return execute(); }
        catch (Exception exception)
        {
            Failure(logger, database, operation, exception);
            throw;
        }
    }

    internal static async Task<T> ExecuteAsync<T>(ILogger logger, string database, string operation,
        CancellationToken cancellationToken, Func<Task<T>> execute)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return await execute().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Failure(logger, database, operation, exception);
            throw;
        }
    }

    internal static Task ExecuteAsync(ILogger logger, string database, string operation,
        CancellationToken cancellationToken, Func<Task> execute)
        => ExecuteAsync(logger, database, operation, cancellationToken, async () =>
        {
            await execute().ConfigureAwait(false);
            return true;
        });
}
