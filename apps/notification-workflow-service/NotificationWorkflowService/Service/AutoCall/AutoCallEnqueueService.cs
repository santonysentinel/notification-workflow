using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Repository;

namespace NotificationWorkflowService.Service.AutoCall;

// SystemId is the CallFlowTemplates row the profile names; VersionId is the active CallFlowTemplateVersions row that was read.
public sealed record AutoCallFlow(int SystemId, string TemplateJson, string TwiML, int? VersionId = null);
// FlowVersionId pins the version validated at capture; null lets the queue pin the active version.
public sealed record AutoCallRequest(int ActiveAlarmId, string Oid, int FlowId, string VariablesJson, Guid EnqueueIdempotencyKey, string POGroupNum, string OptionsJson,
    int? FlowVersionId = null);

public interface IAutoCallQueueRepository
{
    Task<AutoCallFlow> ReadFlowAsync(int flowId, CancellationToken cancellationToken = default);
    Task<RoboCallOfficer?> ReadQueuedOfficerAsync(Guid enqueueKey, CancellationToken cancellationToken);
    Task<int> EnqueueAsync(AutoCallRequest request, CancellationToken cancellationToken = default);
}

public sealed class AutoCallEnqueueService(IAutoCallQueueRepository repository, IParticipantCurrentDetailsRepository participantDetails,
    IAutoCallOfficerRepository? officers = null)
{
    private static readonly Regex Placeholder = new(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<int> EnqueueAsync(ActiveAlarm alarm, string platform, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (alarm.SystemID <= 0 || alarm.HistoryID <= 0 || string.IsNullOrWhiteSpace(platform))
            throw new InvalidOperationException("Auto-call action requires alarm identity, original HistoryID and platform.");
        AutoCallContactService.ValidateOid(alarm.ClientID);
        ValidatePOGroupNum(alarm.POGroupNum);
        var officerOptions = alarm.Priority == 20 ? OfficerRoboCallOptions.Parse(alarm.OptionsJson) : null;
        var options = officerOptions?.CallOptions ?? ParticipantRoboCallOptions.Parse(alarm.OptionsJson);
        var flow = await repository.ReadFlowAsync(options.FlowId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var details = await participantDetails.ReadAsync(alarm.ClientID, alarm.SystemID, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RoboCallOfficer? officer = null;
        if (officerOptions is not null)
        {
            officer = await repository.ReadQueuedOfficerAsync(EnqueueKey(alarm, platform, options.FlowId, true), cancellationToken).ConfigureAwait(false);
            if (officer is not null && officer.Selection != officerOptions.Officer)
                throw new InvalidOperationException("Officer selection conflicts with the queued action.");
            officer ??= officerOptions.Officer.Type == "Custom"
                ? new RoboCallOfficer(officerOptions.Officer, null, null)
                : await (officers ?? throw new InvalidOperationException("Officer resolver is required for Action 20."))
                    .ResolveAsync(alarm.ClientID, officerOptions.Officer, cancellationToken).ConfigureAwait(false);
        }
        var request = CaptureRequest(alarm, platform, flow, details, officer);
        return await repository.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static AutoCallRequest CaptureRequest(ActiveAlarm alarm, string platform, AutoCallFlow flow,
        IReadOnlyDictionary<string, JsonElement> participantDetails, RoboCallOfficer? officer = null)
    {
        if (alarm.HistoryID <= 0) throw new InvalidOperationException("Auto-call enqueue requires the original HistoryID.");
        var officerOptions = alarm.Priority == 20 ? OfficerRoboCallOptions.Parse(alarm.OptionsJson) : null;
        var options = officerOptions?.CallOptions ?? ParticipantRoboCallOptions.Parse(alarm.OptionsJson);
        if (officerOptions is not null && (officer is null || officer.Selection != officerOptions.Officer))
            throw new InvalidOperationException("Officer Robo Call requires its resolved officer snapshot.");
        if (flow.SystemId != options.FlowId) throw new InvalidOperationException("Call flow identity does not match the workflow action.");
        using var graph = JsonDocument.Parse(flow.TemplateJson);
        if (graph.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Call flow graph must be a JSON object.");
        using var text = new StringReader(flow.TwiML);
        using var xml = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var bundle = XDocument.Load(xml);
        if (bundle.Root?.Name != "CallFlow") throw new InvalidOperationException("Call flow must contain a CallFlow XML bundle.");
        var sources = GraphStrings(graph.RootElement)
            .Concat(bundle.DescendantNodes().OfType<XText>().Select(node => node.Value))
            .Concat(bundle.Root.DescendantsAndSelf().Attributes().Select(attribute => attribute.Value));
        foreach (string source in sources)
        {
            string remainder = Placeholder.Replace(source, "");
            if (remainder.Contains("{{", StringComparison.Ordinal))
                throw new InvalidOperationException("Call flow contains an invalid variable placeholder.");
        }
        var key = EnqueueKey(alarm, platform, options.FlowId, officerOptions is not null);
        // The central queue contract keeps its own name for the profile's ShowParticipantResponseTranscript setting.
        // waitForCompletion is only written when true; callDevices lists the devices in dialling order.
        var queueOptionValues = new Dictionary<string, object>
        {
            ["historyId"] = alarm.HistoryID,
            ["postRecordingTranscript"] = options.ShowParticipantResponseTranscript,
        };
        if (options.WaitForCallCompletion) queueOptionValues["waitForCompletion"] = true;
        queueOptionValues["callDevices"] = options.CallDevices.Select(device => new { type = device.Type, phoneNumber = device.PhoneNumber }).ToArray();
        if (officer is not null) queueOptionValues["officer"] = JsonSerializer.SerializeToElement(officer, RoboCallOfficer.QueueJson);
        string queueOptions = JsonSerializer.Serialize(queueOptionValues);
        return new AutoCallRequest(alarm.SystemID, alarm.ClientID, options.FlowId, JsonSerializer.Serialize(participantDetails), key, alarm.POGroupNum,
            queueOptions, flow.VersionId);
    }

    private static Guid EnqueueKey(ActiveAlarm alarm, string platform, int flowId, bool officer)
    {
        byte[] identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Contract = officer ? "officer-autocall-request-v1" : "autocall-request-v1", Platform = platform.ToUpperInvariant(),
            ActiveAlarmId = alarm.SystemID, alarm.HistoryID, Oid = alarm.ClientID,
            alarm.ProfileID, Step = alarm.StateNo, Loop = alarm.CurrentLoopNumber, FlowId = flowId
        });
        byte[] hash = SHA256.HashData(identity);
        hash[6] = (byte)((hash[6] & 15) | 128);
        hash[8] = (byte)((hash[8] & 63) | 128);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    internal static void ValidatePOGroupNum(string? poGroupNum)
    {
        if (string.IsNullOrWhiteSpace(poGroupNum) || poGroupNum.Length > 20)
            throw new InvalidOperationException("Auto-call enqueue requires a nonblank POGroupNum of at most 20 characters.");
    }

    private static IEnumerable<string> GraphStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            yield return element.GetString()!;
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (string value in GraphStrings(property.Value)) yield return value;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                foreach (string value in GraphStrings(child)) yield return value;
        }
    }
}