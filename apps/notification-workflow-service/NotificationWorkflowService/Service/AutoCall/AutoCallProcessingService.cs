using System.Text.Json;
using System.Text.RegularExpressions;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed class AutoCallProcessingService(IAutoCallRepository repository, AutoCallContactService contacts, AutoCallWorkerOptions options) : IAutoCallProcessingService
{
    private static readonly Regex E164 = new(@"\A\+[1-9][0-9]{1,14}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<AutoCallAttempt> PrepareAsync(AutoCallJob job, string workerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.SystemID <= 0 || job.FlowId <= 0 || job.LeaseToken == Guid.Empty || string.IsNullOrWhiteSpace(workerId))
            throw new AutoCallValidationException("A complete claimed job is required for preparation.");
        AutoCallContactService.ValidateOid(job.OID);
        AutoCallWorkerOptions.ValidateBaseUrl(options.FeedbackBaseUrl);
        var officer = RoboCallOfficer.ReadFromOptions(job.OptionsJSON);
        string? customOfficerNumber = officer is null ? null
            : SelectRecipient(job, new AutoCallContacts(job.OID, "", []), out _);
        var resolved = await contacts.ResolveAsync(job.OID, officer, cancellationToken,
            readOfficerPhone: customOfficerNumber is null).ConfigureAwait(false);
        string? recipient = SelectRecipient(job, resolved, out string? deviceType);
        if (recipient is null)
        {
            if (deviceType is null) throw new AutoCallValidationException("No E.164 recipient is available for this OID.");
            await repository.SkipDeviceAsync(job, workerId, cancellationToken).ConfigureAwait(false);
            throw new AutoCallDeviceSkippedException(deviceType);
        }
        var attempt = await repository.BeginAttemptAsync(job, workerId, recipient, cancellationToken).ConfigureAwait(false);
        if (attempt.CallId <= 0 || attempt.QueueId != job.SystemID || attempt.OwnershipToken != job.LeaseToken ||
            attempt.OID != job.OID || attempt.FlowId != job.FlowId || !E164.IsMatch(attempt.PhoneE164))
            throw new AutoCallValidationException("Attempt identity does not match the owned claim.");
        if (attempt.IsPrepared) return attempt;
        if (attempt.TwiML is not null || attempt.Parameters is not null)
            throw new AutoCallValidationException("An incomplete existing preparation requires operator disposition.");
        var bundle = AutoCallBundlePreparer.Prepare(attempt, options.FeedbackBaseUrl);
        cancellationToken.ThrowIfCancellationRequested();
        int savedId = await repository.SavePreparationAsync(attempt, workerId, bundle, cancellationToken).ConfigureAwait(false);
        if (savedId != attempt.CallId) throw new AutoCallValidationException("Preparation did not return the original call identity.");
        return new AutoCallAttempt
        {
            CallId = attempt.CallId, QueueId = attempt.QueueId, QueueLeaseToken = attempt.QueueLeaseToken,
            CurrentLeaseToken = attempt.CurrentLeaseToken,
            OID = attempt.OID, PhoneE164 = attempt.PhoneE164, FlowId = attempt.FlowId,
            TwiML = bundle.TwiML, Parameters = bundle.Parameters,
            TemplateJson = attempt.TemplateJson, TemplateTwiML = attempt.TemplateTwiML, VariablesJson = attempt.VariablesJson
        };
    }

    /// <summary>
    /// Picks the number for the job's call device. Returns null with <paramref name="deviceType"/> set when the device
    /// has no usable number; without call devices it falls back to the first valid number.
    /// </summary>
    internal static string? SelectRecipient(AutoCallJob job, AutoCallContacts contacts, out string? deviceType)
    {
        deviceType = null;
        if (string.IsNullOrWhiteSpace(job.OptionsJSON))
            return contacts.PhoneNumbers.FirstOrDefault(number => E164.IsMatch(number));
        using var document = JsonDocument.Parse(job.OptionsJSON);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("callDevices", out var devices))
            return contacts.PhoneNumbers.FirstOrDefault(number => E164.IsMatch(number));
        if (devices.ValueKind != JsonValueKind.Array || job.CallDeviceIndex < 0 || job.CallDeviceIndex >= devices.GetArrayLength())
            throw new AutoCallValidationException("The claimed call device is not present in the queued options.");
        var device = devices[job.CallDeviceIndex];
        if (device.ValueKind != JsonValueKind.Object || !device.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(typeElement.GetString()))
            throw new AutoCallValidationException("The claimed call device has no type.");
        string type = typeElement.GetString()!;
        deviceType = type;
        if (string.Equals(type, RoboCallDevice.Custom, StringComparison.OrdinalIgnoreCase))
        {
            string? custom = device.TryGetProperty("phoneNumber", out var number) && number.ValueKind == JsonValueKind.String ? number.GetString() : null;
            return custom is not null && E164.IsMatch(custom) ? custom : null;
        }
        return contacts.Phones.FirstOrDefault(phone => string.Equals(phone.Type, type, StringComparison.OrdinalIgnoreCase) && E164.IsMatch(phone.Number))?.Number;
    }
}