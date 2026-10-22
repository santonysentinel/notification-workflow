using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Repository;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed record AutoCallFlow(int SystemId, string TemplateJson, string TwiML);
public sealed record AutoCallRequest(int ActiveAlarmId, string Oid, int FlowId, string VariablesJson, Guid EnqueueIdempotencyKey, string POGroupNum, string OptionsJson);

public interface IAutoCallQueueRepository
{
    Task<AutoCallFlow> ReadFlowAsync(int flowId, CancellationToken cancellationToken = default);
    Task<int> EnqueueAsync(AutoCallRequest request, CancellationToken cancellationToken = default);
}

public sealed class AutoCallEnqueueService(IAutoCallQueueRepository repository, IParticipantCurrentDetailsRepository participantDetails)
{
    private static readonly Regex Placeholder = new(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<int> EnqueueAsync(ActiveAlarm alarm, string platform, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (alarm.SystemID <= 0 || alarm.HistoryID <= 0 || string.IsNullOrWhiteSpace(platform))
            throw new InvalidOperationException("Auto-call action requires alarm identity, original HistoryID and platform.");
        AutoCallContactService.ValidateOid(alarm.ClientID);
        ValidatePOGroupNum(alarm.POGroupNum);
        var options = ParticipantRoboCallOptions.Parse(alarm.OptionsJson);
        var flow = await repository.ReadFlowAsync(options.FlowId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var details = await participantDetails.ReadAsync(alarm.ClientID, alarm.SystemID, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var request = CaptureRequest(alarm, platform, flow, details);
        return await repository.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static AutoCallRequest CaptureRequest(ActiveAlarm alarm, string platform, AutoCallFlow flow,
        IReadOnlyDictionary<string, JsonElement> participantDetails)
    {
        if (alarm.HistoryID <= 0) throw new InvalidOperationException("Auto-call enqueue requires the original HistoryID.");
        var options = ParticipantRoboCallOptions.Parse(alarm.OptionsJson);
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
        byte[] identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Contract = "autocall-request-v1", Platform = platform.ToUpperInvariant(),
            ActiveAlarmId = alarm.SystemID, alarm.HistoryID, Oid = alarm.ClientID,
            alarm.ProfileID, Step = alarm.StateNo, Loop = alarm.CurrentLoopNumber, FlowId = options.FlowId
        });
        byte[] hash = SHA256.HashData(identity);
        hash[6] = (byte)((hash[6] & 15) | 128);
        hash[8] = (byte)((hash[8] & 63) | 128);
        var key = new Guid(hash.AsSpan(0, 16), bigEndian: true);
        // The central queue contract keeps its own name for the profile's ShowParticipantResponseTranscript setting.
        // waitForCompletion is only written when true; callDevices lists the devices in dialling order.
        var queueOptionValues = new Dictionary<string, object>
        {
            ["historyId"] = alarm.HistoryID,
            ["postRecordingTranscript"] = options.ShowParticipantResponseTranscript,
        };
        if (options.WaitForCallCompletion) queueOptionValues["waitForCompletion"] = true;
        queueOptionValues["callDevices"] = options.CallDevices.Select(device => new { type = device.Type, phoneNumber = device.PhoneNumber }).ToArray();
        string queueOptions = JsonSerializer.Serialize(queueOptionValues);
        return new AutoCallRequest(alarm.SystemID, alarm.ClientID, options.FlowId, JsonSerializer.Serialize(participantDetails), key, alarm.POGroupNum,
            queueOptions);
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