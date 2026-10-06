using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ActiveAlarmsParser.Service.NotificationService;

namespace NotificationWorkflowService.Repository;

public sealed class NotificationRepository : INotificationRepository
{
    private readonly string connectionString;
    private readonly ILogger<NotificationRepository> logger;

    public NotificationRepository(IConfiguration configuration, ILogger<NotificationRepository> logger)
    {
        this.logger = logger;
        string? configured = configuration["ClientDatabase"];
        if (string.IsNullOrWhiteSpace(configured)) configured = configuration.GetConnectionString("ClientDatabase");
        connectionString = !string.IsNullOrWhiteSpace(configured) ? configured
            : throw new InvalidOperationException("ClientDatabase is required for notification database access.");
    }

    public Task<IReadOnlyList<AccountNotificationSettingRow>> ReadAccountNotificationSettingsAsync(CancellationToken cancellationToken = default)
        => ReadAsync<AccountNotificationSettingRow>("ActiveAlarms_ReadAccountPushNotificationSettings", cancellationToken);

    public Task<IReadOnlyList<ReminderSettingRow>> ReadReminderSettingsAsync(CancellationToken cancellationToken = default)
        => ReadAsync<ReminderSettingRow>("ActiveAlarms_ReadAccountPushNotificationTypes", cancellationToken);

    public Task<IReadOnlyList<VictimNotificationSettingRow>> ReadVictimSettingsAsync(CancellationToken cancellationToken = default)
        => ReadAsync<VictimNotificationSettingRow>("ActiveAlarms_ReadVictimNotificationSettings", cancellationToken);

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string procedure, CancellationToken cancellationToken)
    {
        // Retry timing belongs to the settings cache; there is no second query retry loop.
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Procedure definitions are not available. Retain legacy parameter discovery
            // and read transactions rather than assuming @session is the only parameter.
            using var metadata = new SqlCommand(procedure, connection, transaction)
            {
                CommandType = CommandType.StoredProcedure, CommandTimeout = 600
            };
            cancellationToken.ThrowIfCancellationRequested();
            // SqlClient exposes no async DeriveParameters; cancellation can still cancel
            // its metadata command. Replace this when procedure signatures are verified.
            using (cancellationToken.Register(() => metadata.Cancel()))
            {
                SqlCommandBuilder.DeriveParameters(metadata);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            foreach (SqlParameter parameter in metadata.Parameters)
            {
                bool input = parameter.Direction == ParameterDirection.Input;
                string name = parameter.ParameterName.TrimStart('@');
                parameters.Add(parameter.ParameterName,
                    input && string.Equals(name, "session", StringComparison.OrdinalIgnoreCase) ? "" : null,
                    input ? DbType.AnsiString : parameter.DbType, parameter.Direction,
                    parameter.Size != 0 ? parameter.Size : null);
            }
            var rows = await connection.QueryAsync<T>(new CommandDefinition(procedure, parameters,
                transaction, commandTimeout: 600, commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return rows.ToList();
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackFailure) { NotificationDiagnostics.Failure(logger, "SettingsRollback", rollbackFailure); }
            throw;
        }
    }

    public Task InsertNotificationHistoryAsync(string historyId, string victimId, string note, int type, CancellationToken cancellationToken = default)
    {
        // Parse locally rather than relying on SqlClient's parameter conversion.
        int id = int.Parse(historyId, CultureInfo.InvariantCulture);
        return ExecuteHistoryWithRetryAsync(async token =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);
            await connection.ExecuteAsync(CreateHistoryCommand(id, victimId, note, type, token)).ConfigureAwait(false);
        }, logger, cancellationToken);
    }

    internal static CommandDefinition CreateHistoryCommand(int historyId, string victimId, string note, int type, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@HistoryID", historyId, DbType.Int32);
        parameters.Add("@emails", note, DbType.AnsiString, size: 1024);
        parameters.Add("@type", type, DbType.Int32);
        parameters.Add("@victimid", victimId, DbType.AnsiString, size: 30);
        return new CommandDefinition("ActiveAlarms_InsertIntoHistory", parameters, commandTimeout: 30,
            commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);
    }

    internal static async Task ExecuteHistoryWithRetryAsync(Func<CancellationToken, Task> execute, ILogger logger,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= (duration, token) => Task.Delay(duration, token);
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await execute(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SqlException exception) when (exception.Number == 1205 && attempt < maxAttempts)
            {
                logger.LogWarning("History insert deadlocked (SQL 1205) on attempt {Attempt} of {MaxAttempts}; retrying", attempt, maxAttempts);
                await delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}