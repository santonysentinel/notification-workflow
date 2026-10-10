using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Service.AutoCall;

namespace NotificationWorkflowService.Repository;

public sealed class AutoCallRepository(IConfiguration configuration) : IAutoCallRepository, IAutoCallSubmissionRepository, IAutoCallRecoveryRepository
{
    public async Task<AutoCallSubmissionResult> BeginSubmissionAsync(AutoCallAttempt attempt, string workerId, Guid token, AutoCallSubmissionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = AutoCallRecoveryOptions.Read(configuration, AutoCallWorkerOptions.Read(configuration));
        return (await ReadAsync<AutoCallSubmissionResult>(BeginSubmissionCommand(attempt, workerId, token, request, cancellationToken,
            options.MaxSubmissionAttempts, options.MaxRecoveryHours)).ConfigureAwait(false)).Single();
    }

    public async Task<AutoCallSubmissionResult> SaveSubmissionAsync(AutoCallAttempt attempt, Guid token, string state, string? sid, string? errorCode, CancellationToken cancellationToken)
        => (await ReadAsync<AutoCallSubmissionResult>(SaveSubmissionCommand(attempt, token, state, sid, errorCode, cancellationToken)).ConfigureAwait(false)).Single();

    internal static CommandDefinition BeginSubmissionCommand(AutoCallAttempt attempt, string workerId, Guid token, AutoCallSubmissionRequest request, CancellationToken cancellationToken, int maxSubmissionAttempts = 3, int maxRecoveryHours = 24)
    {
        var parameters = ClaimParameters(attempt.QueueId, attempt.QueueLeaseToken, workerId);
        parameters.Add("@CallId", attempt.CallId, DbType.Int32);
        parameters.Add("@SubmissionToken", token, DbType.Guid);
        parameters.Add("@RequestJSON", System.Text.Json.JsonSerializer.Serialize(request), DbType.String, size: -1);
        if (attempt.CurrentLeaseToken != Guid.Empty) parameters.Add("@CurrentLeaseToken", attempt.CurrentLeaseToken, DbType.Guid);
        if (maxSubmissionAttempts != 3) parameters.Add("@MaxSubmissionAttempts", maxSubmissionAttempts, DbType.Int32);
        if (maxRecoveryHours != 24) parameters.Add("@MaxRecoveryHours", maxRecoveryHours, DbType.Int32);
        return Command("dbo.automatedcalls_BeginSubmission", parameters, cancellationToken);
    }

    internal static CommandDefinition SaveSubmissionCommand(AutoCallAttempt attempt, Guid token, string state, string? sid, string? errorCode, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@QueueId", attempt.QueueId, DbType.Int32);
        parameters.Add("@LeaseToken", attempt.QueueLeaseToken, DbType.Guid);
        parameters.Add("@CallId", attempt.CallId, DbType.Int32);
        parameters.Add("@SubmissionToken", token, DbType.Guid);
        parameters.Add("@SubmissionState", state, DbType.AnsiString, size: 20);
        parameters.Add("@ProviderCallId", sid, DbType.AnsiString, size: 200);
        parameters.Add("@ErrorCode", errorCode, DbType.AnsiString, size: 100);
        return Command("dbo.automatedcalls_SaveSubmission", parameters, cancellationToken);
    }

    public Task<IReadOnlyList<AutoCallJob>> ClaimAsync(int capacity, string workerId, int leaseMinutes, CancellationToken cancellationToken)
        => ReadAsync<AutoCallJob>(ClaimCommand(capacity, workerId, leaseMinutes, cancellationToken));

    public async Task<AutoCallAttempt> BeginAttemptAsync(AutoCallJob job, string workerId, string recipient, CancellationToken cancellationToken)
    {
        var attempt = (await ReadAsync<AutoCallAttempt>(BeginCommand(job, workerId, recipient, cancellationToken)).ConfigureAwait(false)).Single();
        attempt.CurrentLeaseToken = job.LeaseToken;
        return attempt;
    }

    public async Task<bool> RenewAsync(AutoCallJob job, string workerId, int leaseMinutes, CancellationToken cancellationToken)
    {
        var parameters = ClaimParameters(job.SystemID, job.LeaseToken, workerId);
        parameters.Add("@LeaseMinutes", leaseMinutes, DbType.Int32);
        return (await ReadAsync<bool>(Command("dbo.automatedcalls_RenewLease", parameters, cancellationToken)).ConfigureAwait(false)).Single();
    }

    public async Task<AutoCallAttempt?> ReadRecoveryAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken)
    {
        var attempt = (await ReadAsync<AutoCallAttempt>(Command("dbo.automatedcalls_ReadRecovery", ClaimParameters(job.SystemID, job.LeaseToken, workerId), cancellationToken)).ConfigureAwait(false)).Single();
        job.SubmissionCount = attempt.SubmissionCount;
        job.RecoveryCount = attempt.RecoveryCount;
        return attempt.CallId == 0 ? null : attempt;
    }

    public async Task DispositionAsync(AutoCallJob job, string workerId, string disposition, string errorCode, int delaySeconds, AutoCallRecoveryOptions options, CancellationToken cancellationToken)
    {
        var parameters = ClaimParameters(job.SystemID, job.LeaseToken, workerId);
        parameters.Add("@Disposition", disposition, DbType.AnsiString, size: 20);
        parameters.Add("@ErrorCode", errorCode, DbType.AnsiString, size: 100);
        parameters.Add("@DelaySeconds", delaySeconds, DbType.Int32);
        parameters.Add("@MaxSubmissionAttempts", options.MaxSubmissionAttempts, DbType.Int32);
        parameters.Add("@MaxPreparationAttempts", options.MaxPreparationAttempts, DbType.Int32);
        parameters.Add("@MaxRecoveryHours", options.MaxRecoveryHours, DbType.Int32);
        await ExecuteRecoveryAsync("dbo.automatedcalls_RecoveryDisposition", parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task ObserveAsync(AutoCallJob job, string workerId, AutoCallAttempt attempt, TwilioCallObservation observation, int reconcileSeconds, CancellationToken cancellationToken)
    {
        var parameters = ClaimParameters(job.SystemID, job.LeaseToken, workerId);
        parameters.Add("@CallId", attempt.CallId, DbType.Int32);
        parameters.Add("@ProviderCallId", observation.Sid, DbType.AnsiString, size: 200);
        parameters.Add("@CallStatus", observation.Status, DbType.AnsiString, size: 20);
        parameters.Add("@CallDurationSeconds", observation.DurationSeconds, DbType.Int32);
        parameters.Add("@ReconcileSeconds", reconcileSeconds, DbType.Int32);
        parameters.Add("@MaxRecoveryHours", AutoCallRecoveryOptions.Read(configuration, AutoCallWorkerOptions.Read(configuration)).MaxRecoveryHours, DbType.Int32);
        await ExecuteRecoveryAsync("dbo.automatedcalls_Observe", parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteRecoveryAsync(string procedure, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Command(procedure, parameters, cancellationToken)).ConfigureAwait(false);
    }

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
        var parameters = ClaimParameters(attempt.QueueId, attempt.OwnershipToken, workerId);
        parameters.Add("@CallId", attempt.CallId, DbType.Int32);
        parameters.Add("@TwiML", bundle.TwiML, DbType.String, size: -1);
        parameters.Add("@Parameters", bundle.Parameters, DbType.String, size: -1);
        return Command("dbo.automatedcalls_SavePreparation", parameters, token);
    }
}