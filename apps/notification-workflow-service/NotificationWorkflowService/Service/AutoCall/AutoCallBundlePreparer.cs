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
            throw new AutoCallValidationException("A CallFlow with a valid opening step is required.");
        var steps = root.Elements("Step").Where(step => (string?)step.Attribute("id") == stepId).ToList();
        if (steps.Count != 1 || steps[0].Elements("Response").Count() != 1)
            throw new AutoCallValidationException("The opening step must have exactly one Response.");
        Guid executionId = Guid.NewGuid();
        string response = steps[0].Element("Response")!.ToString(SaveOptions.DisableFormatting)
            .Replace("{{executionId}}", executionId.ToString("D"), StringComparison.Ordinal);
        if (response.Contains("{{", StringComparison.Ordinal)) throw new AutoCallValidationException("The opening response contains unresolved variables.");
        return new AutoCallOpeningStep(stepId, executionId, response);
    }

    internal static AutoCallPreparedBundle Prepare(AutoCallAttempt attempt, string feedbackBaseUrl)
    {
        string baseUrl = AutoCallWorkerOptions.ValidateBaseUrl(feedbackBaseUrl);
        using var variables = JsonDocument.Parse(attempt.VariablesJson);
        if (variables.RootElement.ValueKind != JsonValueKind.Object) throw new AutoCallValidationException("Variables must be a JSON object.");
        var parameters = new SortedDictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in variables.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw new AutoCallValidationException("Preparation variables must have scalar values.");
            if (!parameters.TryAdd(property.Name, property.Value.Clone())) throw new AutoCallValidationException("Duplicate variable keys are not supported.");
        }
        foreach (string reserved in new[] { "callId", "feedbackBaseUrl", "recipient", "executionId" }) parameters.Remove(reserved);
        parameters["callId"] = JsonSerializer.SerializeToElement(attempt.CallId);
        parameters["feedbackBaseUrl"] = JsonSerializer.SerializeToElement(baseUrl);
        parameters["recipient"] = JsonSerializer.SerializeToElement(attempt.PhoneE164);
        using var text = new StringReader(attempt.TemplateTwiML);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        var root = document.Root;
        if (root?.Name != "CallFlow") throw new AutoCallValidationException("An unnamespaced CallFlow bundle is required.");
        if (root.Descendants("Record").Attributes().Any(attribute => attribute.Value.Contains("{{", StringComparison.Ordinal)))
            throw new AutoCallValidationException("Record attributes cannot contain variables.");
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
            foreach (var (name, value) in RecordDefaults)
                if (recording.Attribute(name) is null) recording.SetAttributeValue(name, value);
            recording.SetAttributeValue("action", next);
            recording.SetAttributeValue("method", "POST");
            recording.SetAttributeValue("recordingStatusCallback", baseUrl + "/webhooks/twilio/recordings/status" + suffix);
            recording.SetAttributeValue("recordingStatusCallbackMethod", "POST");
            recording.SetAttributeValue("recordingStatusCallbackEvent", "completed");
        }
        return new AutoCallPreparedBundle(document.ToString(SaveOptions.DisableFormatting), JsonSerializer.Serialize(parameters));
    }

    private static string Resolve(string value, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        if (Placeholder.Replace(value, "").Contains("{{", StringComparison.Ordinal)) throw new AutoCallValidationException("Malformed preparation placeholder.");
        return Placeholder.Replace(value, match =>
        {
            string name = match.Groups["name"].Value;
            if (string.Equals(name, "executionId", StringComparison.OrdinalIgnoreCase)) return "{{executionId}}";
            if (!parameters.TryGetValue(name, out var parameter) || parameter.ValueKind == JsonValueKind.Null ||
                parameter.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(parameter.GetString()))
                throw new AutoCallValidationException($"Required preparation variable '{name}' is missing.");
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
            throw new AutoCallValidationException("A version-1 flow graph is required.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps.EnumerateArray())
        {
            if (!step.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 200 || !ids.Add(id.GetString()!))
                throw new AutoCallValidationException("Flow graph step identities must be unique and nonblank.");
        }
        var bundleSteps = root.Elements().ToList();
        if (!ids.Contains(initial.GetString()!) || (string?)root.Attribute("initialStep") != initial.GetString() || bundleSteps.Count != ids.Count ||
            bundleSteps.Any(step => step.Name != "Step" || !ids.Remove((string?)step.Attribute("id") ?? "") || step.Elements().Count() != 1 || step.Element("Response") is null) || ids.Count != 0)
            throw new AutoCallValidationException("Flow graph and XML bundle step identities must match exactly.");
        ValidateMachineStep(definition, steps, initial.GetString()!, root);
        ValidateRecordSteps(steps, root);
    }

    private static readonly (string Name, string Value)[] RecordDefaults =
        [("maxLength", "120"), ("timeout", "5"), ("finishOnKey", "#"), ("playBeep", "true"), ("trim", "trim-silence")];

    private static readonly HashSet<string> RuntimeRecordAttributes = new(StringComparer.Ordinal)
        { "action", "method", "recordingStatusCallback", "recordingStatusCallbackMethod", "recordingStatusCallbackEvent" };

    /// <summary>A record step prompts (Say/Pause), records once, and falls through to a Redirect when nothing was recorded.</summary>
    private static void ValidateRecordSteps(JsonElement steps, XElement root)
    {
        var types = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var step in steps.EnumerateArray())
            types[step.GetProperty("id").GetString()!] = step.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        foreach (var step in steps.EnumerateArray())
        {
            string id = step.GetProperty("id").GetString()!;
            var response = root.Elements("Step").Single(element => (string?)element.Attribute("id") == id).Element("Response")!;
            if (types[id] != "record")
            {
                if (response.Descendants("Record").Any())
                    throw new AutoCallValidationException("Record may only be used in a record step.");
                continue;
            }
            if (!step.TryGetProperty("transitions", out var transitions) || transitions.ValueKind != JsonValueKind.Object ||
                transitions.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(["noRecording", "recorded"], StringComparer.Ordinal) is false ||
                transitions.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String || !types.ContainsKey(property.Value.GetString()!)))
                throw new AutoCallValidationException("A record step requires recorded and noRecording transitions to existing steps.");
            var verbs = response.Elements().ToList();
            if (verbs.Count < 2 || verbs[^1].Name != "Redirect" || verbs[^2].Name != "Record" ||
                verbs.Take(verbs.Count - 2).Any(verb => verb.Name != "Say" && verb.Name != "Pause") || response.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
                throw new AutoCallValidationException("A record step may only Say or Pause, then Record once, then Redirect.");
            ValidateRecordAttributes(verbs[^2]);
        }
    }

    private static void ValidateRecordAttributes(XElement record)
    {
        if (record.Nodes().Any()) throw new AutoCallValidationException("Record must be empty.");
        foreach (var attribute in record.Attributes())
        {
            string name = attribute.Name.ToString(), value = attribute.Value;
            bool valid = name switch
            {
                "maxLength" => IsInteger(value, 1, 300),
                "timeout" => IsInteger(value, 1, 30),
                "finishOnKey" => value.Length is > 0 and <= 12 && value.All(key => char.IsAsciiDigit(key) || key is '*' or '#'),
                "playBeep" => value is "true" or "false",
                "trim" => value is "trim-silence" or "do-not-trim",
                _ => RuntimeRecordAttributes.Contains(name)
            };
            if (!valid) throw new AutoCallValidationException($"Record attribute '{name}' is not allowed or has an invalid value.");
        }
    }

    private static bool IsInteger(string value, int minimum, int maximum) =>
        value.Length is > 0 and <= 3 && value.All(char.IsAsciiDigit) &&
        int.Parse(value, CultureInfo.InvariantCulture) is var number && number >= minimum && number <= maximum;

    /// <summary>The optional voicemail step: runtime-entered only, terminal, and limited to Say then a final Hangup.</summary>
    private static void ValidateMachineStep(JsonElement definition, JsonElement steps, string initialStep, XElement root)
    {
        string? graphValue = null;
        if (definition.TryGetProperty("machineStep", out var machine))
        {
            if (machine.ValueKind != JsonValueKind.String) throw new AutoCallValidationException("machineStep must be a step identity.");
            graphValue = machine.GetString();
        }
        string? xmlValue = (string?)root.Attribute("machineStep");
        if (graphValue is null && xmlValue is null) return;
        if (graphValue is null || xmlValue is null || graphValue != xmlValue || string.IsNullOrWhiteSpace(graphValue) || graphValue.Length > 200)
            throw new AutoCallValidationException("machineStep must be present in both the flow graph and the XML bundle with the same value.");
        if (graphValue == initialStep) throw new AutoCallValidationException("machineStep must differ from initialStep.");
        JsonElement? target = null;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.GetProperty("id").GetString() == graphValue) target = step;
            else if (step.TryGetProperty("transitions", out var transitions) && TargetsStep(transitions, graphValue))
                throw new AutoCallValidationException("No step may transition to machineStep.");
        }
        if (target is not { } found || !found.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "terminal" ||
            found.TryGetProperty("transitions", out var own) && own.ValueKind != JsonValueKind.Null && !(own.ValueKind == JsonValueKind.Object && !own.EnumerateObject().Any()))
            throw new AutoCallValidationException("machineStep must name a terminal step without transitions.");
        var response = root.Elements("Step").Single(step => (string?)step.Attribute("id") == graphValue).Element("Response")!;
        var verbs = response.Elements().ToList();
        if (verbs.Count < 2 || verbs[^1].Name != "Hangup" || verbs[..^1].Any(verb => verb.Name != "Say"))
            throw new AutoCallValidationException("machineStep may only Say a message and then Hang up.");
    }

    private static bool TargetsStep(JsonElement value, string stepId) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() == stepId,
        JsonValueKind.Object => value.EnumerateObject().Any(property => TargetsStep(property.Value, stepId)),
        JsonValueKind.Array => value.EnumerateArray().Any(item => TargetsStep(item, stepId)),
        _ => false
    };
}