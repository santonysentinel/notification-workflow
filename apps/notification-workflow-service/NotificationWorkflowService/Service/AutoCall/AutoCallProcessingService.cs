using System.Text.RegularExpressions;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed class AutoCallProcessingService(IAutoCallRepository repository, AutoCallContactService contacts, AutoCallWorkerOptions options) : IAutoCallProcessingService
{
    private static readonly Regex E164 = new(@"\A\+[1-9][0-9]{1,14}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<AutoCallAttempt> PrepareAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.SystemID <= 0 || job.FlowId <= 0 || job.LeaseToken == Guid.Empty || string.IsNullOrWhiteSpace(workerId))
            throw new InvalidOperationException("A complete claimed job is required for preparation.");
        AutoCallContactService.ValidateOid(job.OID);
        AutoCallWorkerOptions.ValidateBaseUrl(options.FeedbackBaseUrl);
        var resolved = await contacts.ResolveAsync(job.OID, cancellationToken).ConfigureAwait(false);
        string recipient = resolved.PhoneNumbers.FirstOrDefault(number => E164.IsMatch(number))
            ?? throw new InvalidOperationException("No E.164 recipient is available for this OID.");
        var attempt = await repository.BeginAttemptAsync(job, workerId, recipient, cancellationToken).ConfigureAwait(false);
        if (attempt.CallId <= 0 || attempt.QueueId != job.SystemID || attempt.OwnershipToken != job.LeaseToken ||
            attempt.OID != job.OID || attempt.FlowId != job.FlowId || !E164.IsMatch(attempt.PhoneE164))
            throw new InvalidOperationException("Attempt identity does not match the owned claim.");
        if (attempt.IsPrepared) return attempt;
        if (attempt.TwiML is not null || attempt.Parameters is not null)
            throw new InvalidOperationException("An incomplete existing preparation requires operator disposition.");
        var bundle = AutoCallBundlePreparer.Prepare(attempt, options.FeedbackBaseUrl);
        cancellationToken.ThrowIfCancellationRequested();
        int savedId = await repository.SavePreparationAsync(attempt, workerId, bundle, cancellationToken).ConfigureAwait(false);
        if (savedId != attempt.CallId) throw new InvalidOperationException("Preparation did not return the original call identity.");
        return new AutoCallAttempt
        {
            CallId = attempt.CallId, QueueId = attempt.QueueId, QueueLeaseToken = attempt.QueueLeaseToken,
            CurrentLeaseToken = attempt.CurrentLeaseToken,
            OID = attempt.OID, PhoneE164 = attempt.PhoneE164, FlowId = attempt.FlowId,
            TwiML = bundle.TwiML, Parameters = bundle.Parameters,
            TemplateJson = attempt.TemplateJson, TemplateTwiML = attempt.TemplateTwiML, VariablesJson = attempt.VariablesJson
        };
    }
}