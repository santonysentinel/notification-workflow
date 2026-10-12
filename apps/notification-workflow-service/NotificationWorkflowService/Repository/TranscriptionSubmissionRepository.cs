using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Service.Transcription;

namespace NotificationWorkflowService.Repository;

public sealed class TranscriptionSubmissionRepository(IConfiguration configuration) : ITranscriptionSubmissionRepository, ITranscriptionLifecycleRepository, ITranscriptionMetricsRepository
{
    public Task<TranscriptionQueueSnapshot> GetMetricsAsync(CancellationToken cancellationToken)
        => ReadAsync<TranscriptionQueueSnapshot>(Command("dbo.transcription_GetQueueMetrics", new DynamicParameters(), cancellationToken));

    public async Task RecoverAsync(int batchSize, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@BatchSize", batchSize, DbType.Int32);
        _ = await ReadManyAsync<RecoveryRow>(Command("dbo.transcription_RecoverTimeouts", parameters, cancellationToken)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TranscriptionClaim>> ClaimAsync(int capacity, string workerId, int leaseSeconds, int maxOutstanding, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@BatchSize", capacity, DbType.Int32);
        parameters.Add("@WorkerId", workerId, DbType.AnsiString, size: 100);
        parameters.Add("@LeaseSeconds", leaseSeconds, DbType.Int32);
        parameters.Add("@MaxOutstandingSubmissions", maxOutstanding, DbType.Int32);
        return ReadManyAsync<TranscriptionClaim>(Command("dbo.transcription_ClaimJobs", parameters, cancellationToken));
    }

    public Task<bool> RenewAsync(TranscriptionClaim claim, string workerId, int leaseSeconds, CancellationToken cancellationToken)
    {
        var parameters = Fence(claim, workerId);
        parameters.Add("@LeaseSeconds", leaseSeconds, DbType.Int32);
        return ReadAsync<bool>(Command("dbo.transcription_RenewLease", parameters, cancellationToken));
    }

    public async Task<TranscriptionWork?> GetWorkAsync(TranscriptionClaim claim, string workerId, CancellationToken cancellationToken)
    {
        var work = (await ReadManyAsync<TranscriptionWork>(Command("dbo.transcription_GetWorkContext", Fence(claim, workerId), cancellationToken)).ConfigureAwait(false)).SingleOrDefault();
        return work;
    }

    public Task<bool> CompleteEmptyAsync(TranscriptionClaim claim, string workerId, CancellationToken cancellationToken)
        => ReadAsync<bool>(Command("dbo.transcription_CompleteEmpty", Fence(claim, workerId), cancellationToken));

    public Task<bool> DispositionAsync(TranscriptionClaim claim, string workerId, int? submissionId, string disposition, string errorCode, bool definiteRejection, CancellationToken cancellationToken)
    {
        var parameters = Fence(claim, workerId);
        parameters.Add("@SubmissionId", submissionId, DbType.Int32);
        parameters.Add("@Disposition", disposition, DbType.AnsiString, size: 10);
        parameters.Add("@ErrorCode", errorCode, DbType.AnsiString, size: 100);
        parameters.Add("@DefiniteRejection", definiteRejection, DbType.Boolean);
        return ReadAsync<bool>(Command("dbo.transcription_ScheduleRetry", parameters, cancellationToken));
    }

    private sealed class RecoveryRow { public int JobId { get; init; } public string Status { get; init; } = ""; }

    private static DynamicParameters Fence(TranscriptionClaim claim, string workerId)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@JobId", claim.SystemID, DbType.Int32);
        parameters.Add("@WorkerId", workerId, DbType.AnsiString, size: 100);
        parameters.Add("@LeaseToken", claim.LeaseToken, DbType.Guid);
        return parameters;
    }

    private async Task<IReadOnlyList<T>> ReadManyAsync<T>(CommandDefinition command)
    {
        command.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = Connection();
            await connection.OpenAsync(command.CancellationToken).ConfigureAwait(false);
            return (await connection.QueryAsync<T>(command).ConfigureAwait(false)).ToList();
        }
        catch (OperationCanceledException) when (command.CancellationToken.IsCancellationRequested) { throw new OperationCanceledException(command.CancellationToken); }
        catch (Exception) { throw new TranscriptionException(TranscriptionFailure.Persistence, "TRANSCRIPTION_DATABASE_UNAVAILABLE"); }
    }

    private SqlConnection Connection()
    {
        string? connectionString = configuration.GetConnectionString("AutoCallDB");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new TranscriptionException(TranscriptionFailure.Persistence, "DATABASE_NOT_CONFIGURED");
        return new SqlConnection(connectionString);
    }
    public async Task<TranscriptionCreation> CreateAsync(TranscriptionWork work, string workerId, TranscriptionSubmissionIdentity identity,
        CallbackCredential credential, CancellationToken cancellationToken)
    {
        var parameters = FencedIdentity(work, workerId, identity);
        parameters.Add("@CallbackTokenHash", credential.Hash, DbType.Binary, size: 32);
        parameters.Add("@CallbackTokenExpiresAt", credential.ExpiresAt, DbType.DateTime2);
        return await ReadAsync<TranscriptionCreation>(Command("dbo.transcription_CreateSubmission", parameters, cancellationToken)).ConfigureAwait(false);
    }

    public Task<bool> BeginAsync(TranscriptionWork work, string workerId, TranscriptionSubmissionIdentity identity, CancellationToken cancellationToken)
        => ReadAsync<bool>(Command("dbo.transcription_BeginSubmission", FencedIdentity(work, workerId, identity), cancellationToken));

    public Task<string> SaveAcceptanceAsync(TranscriptionSubmissionIdentity identity, string requestId, CancellationToken cancellationToken)
    {
        var parameters = Identity(identity);
        parameters.Add("@RequestId", requestId, DbType.AnsiString, size: 36);
        return ReadAsync<string>(Command("dbo.transcription_SaveAcceptance", parameters, cancellationToken));
    }

    private async Task<T> ReadAsync<T>(CommandDefinition command)
    {
        command.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = Connection();
            await connection.OpenAsync(command.CancellationToken).ConfigureAwait(false);
            return await connection.QuerySingleAsync<T>(command).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (command.CancellationToken.IsCancellationRequested) { throw new OperationCanceledException(command.CancellationToken); }
        catch (Exception) { throw new TranscriptionException(TranscriptionFailure.Persistence, "TRANSCRIPTION_DATABASE_UNAVAILABLE"); }
    }

    private static DynamicParameters Identity(TranscriptionSubmissionIdentity identity)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@JobId", identity.JobId, DbType.Int32);
        parameters.Add("@CorrelationId", identity.CorrelationId, DbType.Guid);
        parameters.Add("@SubmissionToken", identity.SubmissionToken, DbType.Guid);
        return parameters;
    }

    private static DynamicParameters FencedIdentity(TranscriptionWork work, string workerId, TranscriptionSubmissionIdentity identity)
    {
        if (work.JobId != identity.JobId) throw new TranscriptionException(TranscriptionFailure.Validation, "INVALID_SUBMISSION_IDENTITY");
        var parameters = Identity(identity);
        parameters.Add("@WorkerId", workerId, DbType.AnsiString, size: 100);
        parameters.Add("@LeaseToken", work.LeaseToken, DbType.Guid);
        return parameters;
    }

    private static CommandDefinition Command(string procedure, DynamicParameters parameters, CancellationToken cancellationToken)
        => new(procedure, parameters, commandTimeout: 30, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken);
}