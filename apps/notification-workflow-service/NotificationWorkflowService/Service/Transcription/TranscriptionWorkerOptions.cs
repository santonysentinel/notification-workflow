using Microsoft.Extensions.Configuration;

namespace NotificationWorkflowService.Service.Transcription;

public sealed class TranscriptionWorkerOptions
{
    public bool Enabled { get; init; }
    public int UploadConcurrency { get; init; } = 1;
    public int MaxOutstandingSubmissions { get; init; } = 10;
    public int LeaseSeconds { get; init; } = 300;
    public int RenewalSeconds { get; init; } = 60;
    public int DatabaseTimeoutSeconds { get; init; } = 5;
    public int PollSeconds { get; init; } = 5;
    public int RecoveryBatchSize { get; init; } = 100;
    public string WorkerId { get; init; } = "transcription-" + Guid.NewGuid().ToString("N");

    public void Validate()
    {
        if (UploadConcurrency is < 1 or > 100 || MaxOutstandingSubmissions < UploadConcurrency || MaxOutstandingSubmissions > 10000 ||
            LeaseSeconds is < 10 or > 3600 || RenewalSeconds < 1 || DatabaseTimeoutSeconds is < 1 or > 30 ||
            RenewalSeconds + 2 * DatabaseTimeoutSeconds >= LeaseSeconds || PollSeconds is < 1 or > 300 ||
            RecoveryBatchSize is < 1 or > 1000 || string.IsNullOrWhiteSpace(WorkerId) || WorkerId.Length > 100 ||
            WorkerId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidOperationException("Invalid Transcription:Worker concurrency, lease or polling configuration.");
    }

    internal static TranscriptionWorkerOptions Read(IConfiguration configuration)
        => configuration.GetSection("Transcription:Worker").Get<TranscriptionWorkerOptions>() ?? new();
}

public sealed class TranscriptionClaim
{
    public int SystemID { get; init; }
    public Guid LeaseToken { get; init; }
}

public interface ITranscriptionLifecycleRepository
{
    Task RecoverAsync(int batchSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<TranscriptionClaim>> ClaimAsync(int capacity, string workerId, int leaseSeconds, int maxOutstanding, CancellationToken cancellationToken);
    Task<bool> RenewAsync(TranscriptionClaim claim, string workerId, int leaseSeconds, CancellationToken cancellationToken);
    Task<TranscriptionWork?> GetWorkAsync(TranscriptionClaim claim, string workerId, CancellationToken cancellationToken);
    Task<bool> CompleteEmptyAsync(TranscriptionClaim claim, string workerId, CancellationToken cancellationToken);
    Task<bool> DispositionAsync(TranscriptionClaim claim, string workerId, int? submissionId, string disposition, string errorCode, bool definiteRejection, CancellationToken cancellationToken);
}