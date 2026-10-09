using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Service.AutoCall;

namespace NotificationWorkflowService.Repository;

public sealed class AutoCallRepository(IConfiguration configuration) : IAutoCallRepository
{
    public Task<IReadOnlyList<AutoCallJob>> ClaimAsync(int capacity, string workerId, int leaseMinutes, CancellationToken cancellationToken)
        => ReadAsync<AutoCallJob>(ClaimCommand(capacity, workerId, leaseMinutes, cancellationToken));

    public async Task<AutoCallAttempt> BeginAttemptAsync(AutoCallJob job, string workerId, string recipient, CancellationToken cancellationToken)
        => (await ReadAsync<AutoCallAttempt>(BeginCommand(job, workerId, recipient, cancellationToken)).ConfigureAwait(false)).Single();

    public async Task<int> SavePreparationAsync(AutoCallAttempt attempt, string workerId, AutoCallPreparedBundle bundle, CancellationToken cancellationToken)
        => (await ReadAsync<int>(SaveCommand(attempt, workerId, bundle, cancellationToken)).ConfigureAwait(false)).Single();

    public async Task FailPreparationAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Command("dbo.automatedcalls_FailPreparation", ClaimParameters(job.SystemID, job.LeaseToken, workerId), cancellationToken)).ConfigureAwait(false);
    }

    private SqlConnection Connection()
    {
        string? value = configuration.GetConnectionString("AutoCallDB");
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("ConnectionStrings:AutoCallDB is required for attempt preparation.");
        return new SqlConnection(value);
    }

    private async Task<IReadOnlyList<T>> ReadAsync<T>(CommandDefinition command)
    {
        command.CancellationToken.ThrowIfCancellationRequested();
        await using var connection = Connection();
        await connection.OpenAsync(command.CancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<T>(command).ConfigureAwait(false)).ToList();
    }

    private static CommandDefinition Command(string procedure, DynamicParameters parameters, CancellationToken token)
        => new(procedure, parameters, commandTimeout: 30, commandType: CommandType.StoredProcedure, cancellationToken: token);

    private static DynamicParameters ClaimParameters(int queueId, Guid leaseToken, string workerId)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@QueueId", queueId, DbType.Int32);
        parameters.Add("@LeaseToken", leaseToken, DbType.Guid);
        parameters.Add("@WorkerId", workerId, DbType.AnsiString, size: 100);
        return parameters;
    }

    internal static CommandDefinition ClaimCommand(int capacity, string workerId, int leaseMinutes, CancellationToken token)
    {
        if (capacity <= 0 || leaseMinutes <= 0 || string.IsNullOrWhiteSpace(workerId)) throw new ArgumentException("Positive claim capacity, lease and worker identity are required.");
        var parameters = new DynamicParameters();
        parameters.Add("@BatchSize", capacity, DbType.Int32);
        parameters.Add("@WorkerId", workerId, DbType.AnsiString, size: 100);
        parameters.Add("@LeaseMinutes", leaseMinutes, DbType.Int32);
        return Command("dbo.activealarms_call_GetNextSetOfJobs", parameters, token);
    }

    internal static CommandDefinition BeginCommand(AutoCallJob job, string workerId, string recipient, CancellationToken token)
    {
        var parameters = ClaimParameters(job.SystemID, job.LeaseToken, workerId);
        parameters.Add("@PhoneE164", recipient, DbType.AnsiString, size: 16);
        return Command("dbo.automatedcalls_BeginAttempt", parameters, token);
    }

    internal static CommandDefinition SaveCommand(AutoCallAttempt attempt, string workerId, AutoCallPreparedBundle bundle, CancellationToken token)
    {
        var parameters = ClaimParameters(attempt.QueueId, attempt.QueueLeaseToken, workerId);
        parameters.Add("@CallId", attempt.CallId, DbType.Int32);
        parameters.Add("@TwiML", bundle.TwiML, DbType.String, size: -1);
        parameters.Add("@Parameters", bundle.Parameters, DbType.String, size: -1);
        return Command("dbo.automatedcalls_SavePreparation", parameters, token);
    }
}