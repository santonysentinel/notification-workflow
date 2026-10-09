using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NotificationWorkflowService.Service.AutoCall;

internal static class AutoCallBundlePreparer
{
    private static readonly Regex Placeholder = new(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static AutoCallOpeningStep PrepareOpening(string twiML)
    {
        using var text = new StringReader(twiML);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        var root = XDocument.Load(reader).Root;
        string? stepId = (string?)root?.Attribute("initialStep");
        if (root?.Name != "CallFlow" || string.IsNullOrWhiteSpace(stepId) || stepId.Length > 200)
            throw new InvalidOperationException("A CallFlow with a valid opening step is required.");
        var steps = root.Elements("Step").Where(step => (string?)step.Attribute("id") == stepId).ToList();
        if (steps.Count != 1 || steps[0].Elements("Response").Count() != 1)
            throw new InvalidOperationException("The opening step must have exactly one Response.");
        Guid executionId = Guid.NewGuid();
        string response = steps[0].Element("Response")!.ToString(SaveOptions.DisableFormatting)
            .Replace("{{executionId}}", executionId.ToString("D"), StringComparison.Ordinal);
        if (response.Contains("{{", StringComparison.Ordinal)) throw new InvalidOperationException("The opening response contains unresolved variables.");
        return new AutoCallOpeningStep(stepId, executionId, response);
    }

    internal static AutoCallPreparedBundle Prepare(AutoCallAttempt attempt, string feedbackBaseUrl)
    {
        string baseUrl = AutoCallWorkerOptions.ValidateBaseUrl(feedbackBaseUrl);
        using var variables = JsonDocument.Parse(attempt.VariablesJson);
        if (variables.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Variables must be a JSON object.");
        var parameters = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in variables.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("Preparation variables must have scalar non-null values.");
            if (!parameters.TryAdd(property.Name, property.Value.Clone())) throw new InvalidOperationException("Duplicate variable keys are not supported.");
        }
        parameters["callId"] = JsonSerializer.SerializeToElement(attempt.CallId);
        parameters["feedbackBaseUrl"] = JsonSerializer.SerializeToElement(baseUrl);
        parameters["recipient"] = JsonSerializer.SerializeToElement(attempt.PhoneE164);
        parameters.Remove("executionId");
        using var text = new StringReader(attempt.TemplateTwiML);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        var root = document.Root;
        if (root?.Name != "CallFlow") throw new InvalidOperationException("An unnamespaced CallFlow bundle is required.");
        foreach (var node in document.DescendantNodes().OfType<XText>()) node.Value = Resolve(node.Value, parameters);
        foreach (var attribute in root.DescendantsAndSelf().Attributes()) attribute.Value = Resolve(attribute.Value, parameters);
        ValidateGraph(attempt.TemplateJson, root);
        string suffix = "?callId=" + attempt.CallId.ToString(CultureInfo.InvariantCulture);
        string next = baseUrl + "/webhooks/twilio/voice/next" + suffix + "&executionId={{executionId}}";
        foreach (var gather in root.Descendants("Gather"))
        {
            gather.SetAttributeValue("action", next);
            gather.SetAttributeValue("method", "POST");
            gather.SetAttributeValue("actionOnEmptyResult", "true");
        }
        foreach (var redirect in root.Descendants("Redirect"))
        {
            redirect.Value = next;
            redirect.SetAttributeValue("method", "POST");
        }
        foreach (var recording in root.Descendants("Record"))
        {
            recording.SetAttributeValue("action", next);
            recording.SetAttributeValue("method", "POST");
            recording.SetAttributeValue("recordingStatusCallback", baseUrl + "/webhooks/twilio/recordings/status" + suffix);
            recording.SetAttributeValue("recordingStatusCallbackMethod", "POST");
        }
        return new AutoCallPreparedBundle(document.ToString(SaveOptions.DisableFormatting), JsonSerializer.Serialize(parameters));
    }

    private static string Resolve(string value, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        if (Placeholder.Replace(value, "").Contains("{{", StringComparison.Ordinal)) throw new InvalidOperationException("Malformed preparation placeholder.");
        return Placeholder.Replace(value, match =>
        {
            string name = match.Groups["name"].Value;
            if (name == "executionId") return "{{executionId}}";
            if (!parameters.TryGetValue(name, out var parameter)) throw new InvalidOperationException("A required preparation variable is missing.");
            return parameter.ValueKind == JsonValueKind.String ? parameter.GetString()! : parameter.GetRawText();
        });
    }

    private static void ValidateGraph(string templateJson, XElement root)
    {
        using var graph = JsonDocument.Parse(templateJson);
        var definition = graph.RootElement;
        if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out int number) || number != 1 ||
            !definition.TryGetProperty("initialStep", out var initial) || initial.ValueKind != JsonValueKind.String ||
            !definition.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("A version-1 flow graph is required.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps.EnumerateArray())
        {
            if (!step.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 200 || !ids.Add(id.GetString()!))
                throw new InvalidOperationException("Flow graph step identities must be unique and nonblank.");
        }
        var bundleSteps = root.Elements().ToList();
        if (!ids.Contains(initial.GetString()!) || (string?)root.Attribute("initialStep") != initial.GetString() || bundleSteps.Count != ids.Count ||
            bundleSteps.Any(step => step.Name != "Step" || !ids.Remove((string?)step.Attribute("id") ?? "") || step.Elements().Count() != 1 || step.Element("Response") is null) || ids.Count != 0)
            throw new InvalidOperationException("Flow graph and XML bundle step identities must match exactly.");
    }
}