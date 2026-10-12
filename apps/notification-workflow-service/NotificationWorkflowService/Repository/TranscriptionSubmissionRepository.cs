using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Service.Transcription;

namespace NotificationWorkflowService.Repository;

public sealed class TranscriptionSubmissionRepository(IConfiguration configuration) : ITranscriptionSubmissionRepository
{
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
        string? connectionString = configuration.GetConnectionString("AutoCallDB");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new TranscriptionException(TranscriptionFailure.Persistence, "DATABASE_NOT_CONFIGURED");
        try
        {
            await using var connection = new SqlConnection(connectionString);
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