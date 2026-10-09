using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NotificationWorkflowService.Entity;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed record AutoCallFlow(int SystemId, string TemplateJson, string TwiML);
public sealed record AutoCallRequest(int ActiveAlarmId, string Oid, int FlowId, string VariablesJson, Guid EnqueueIdempotencyKey);

public interface IAutoCallQueueRepository
{
    Task<AutoCallFlow> ReadFlowAsync(int flowId, CancellationToken cancellationToken = default);
    Task<int> EnqueueAsync(AutoCallRequest request, CancellationToken cancellationToken = default);
}

public sealed class AutoCallEnqueueService(IAutoCallQueueRepository repository)
{
    private static readonly Regex Placeholder = new(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "callId", "feedbackBaseUrl", "executionId", "recipient" };

    public async Task<int> EnqueueAsync(ActiveAlarm alarm, string platform, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (alarm.SystemID <= 0 || alarm.FlowId <= 0 || string.IsNullOrWhiteSpace(platform))
            throw new InvalidOperationException("Auto-call action requires alarm identity, platform and FlowId.");
        AutoCallContactService.ValidateOid(alarm.ClientID);
        var flow = await repository.ReadFlowAsync(alarm.FlowId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var request = CaptureRequest(alarm, platform, flow);
        return await repository.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static AutoCallRequest CaptureRequest(ActiveAlarm alarm, string platform, AutoCallFlow flow)
    {
        if (flow.SystemId != alarm.FlowId) throw new InvalidOperationException("Call flow identity does not match the workflow action.");
        using var graph = JsonDocument.Parse(flow.TemplateJson);
        if (graph.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Call flow graph must be a JSON object.");
        using var text = new StringReader(flow.TwiML);
        using var xml = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var bundle = XDocument.Load(xml);
        if (bundle.Root?.Name != "CallFlow") throw new InvalidOperationException("Call flow must contain a CallFlow XML bundle.");
        var variables = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        var sources = GraphStrings(graph.RootElement)
            .Concat(bundle.DescendantNodes().OfType<XText>().Select(node => node.Value))
            .Concat(bundle.Root.DescendantsAndSelf().Attributes().Select(attribute => attribute.Value));
        foreach (string source in sources)
        {
            foreach (Match match in Placeholder.Matches(source))
            {
                string name = match.Groups["name"].Value;
                if (Reserved.Contains(name) || variables.ContainsKey(name)) continue;
                var property = typeof(ActiveAlarm).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                object? value = property?.GetValue(alarm);
                if (property is null || value is null || value is string label && string.IsNullOrWhiteSpace(label))
                    throw new InvalidOperationException("A required auto-call variable is unknown or missing.");
                variables.Add(name, JsonSerializer.SerializeToElement(value, property.PropertyType));
            }
            string remainder = Placeholder.Replace(source, "");
            if (remainder.Contains("{{", StringComparison.Ordinal))
                throw new InvalidOperationException("Call flow contains an invalid variable placeholder.");
        }
        byte[] identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Contract = "autocall-request-v1", Platform = platform.ToUpperInvariant(),
            ActiveAlarmId = alarm.SystemID, alarm.HistoryID, Oid = alarm.ClientID,
            alarm.ProfileID, Step = alarm.StateNo, Loop = alarm.CurrentLoopNumber, alarm.FlowId
        });
        byte[] hash = SHA256.HashData(identity);
        hash[6] = (byte)((hash[6] & 15) | 128);
        hash[8] = (byte)((hash[8] & 63) | 128);
        var key = new Guid(hash.AsSpan(0, 16), bigEndian: true);
        return new AutoCallRequest(alarm.SystemID, alarm.ClientID, alarm.FlowId, JsonSerializer.Serialize(variables), key);
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