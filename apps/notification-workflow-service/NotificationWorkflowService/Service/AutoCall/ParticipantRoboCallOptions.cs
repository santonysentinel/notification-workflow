using System.Text.Json;

namespace NotificationWorkflowService.Service.AutoCall;

/// <summary>
/// Action-19 settings read from tenant <c>ProfileItems.OptionsJSON.participantRoboCall</c>.
/// Property names match case-insensitively; settings not yet consumed (devices, timeout, etc.) are ignored.
/// </summary>
public sealed record ParticipantRoboCallOptions(int FlowId, bool ShowParticipantResponseTranscript)
{
    public const string SectionName = "participantRoboCall";

    public static ParticipantRoboCallOptions Parse(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
            throw Invalid("OptionsJSON is required.");
        JsonDocument document;
        try { document = JsonDocument.Parse(optionsJson); }
        catch (JsonException exception) { throw new InvalidOperationException("Robo Call profile OptionsJSON is not valid JSON.", exception); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("OptionsJSON must be a JSON object.");
            var section = Single(document.RootElement, SectionName)
                ?? throw Invalid($"OptionsJSON.{SectionName} is required.");
            if (section.ValueKind != JsonValueKind.Object) throw Invalid($"OptionsJSON.{SectionName} must be a JSON object.");

            var flow = Single(section, "FlowId");
            if (flow is not { ValueKind: JsonValueKind.Number } || !flow.Value.TryGetInt32(out int flowId) || flowId <= 0)
                throw Invalid($"OptionsJSON.{SectionName}.FlowId must be a positive integer.");

            bool showTranscript = Single(section, "ShowParticipantResponseTranscript") switch
            {
                null or { ValueKind: JsonValueKind.Null } => true,
                { ValueKind: JsonValueKind.True } => true,
                { ValueKind: JsonValueKind.False } => false,
                _ => throw Invalid($"OptionsJSON.{SectionName}.ShowParticipantResponseTranscript must be a boolean."),
            };
            return new ParticipantRoboCallOptions(flowId, showTranscript);
        }
    }

    private static JsonElement? Single(JsonElement parent, string name)
    {
        JsonElement? found = null;
        foreach (var property in parent.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) throw Invalid($"OptionsJSON contains duplicate '{name}' properties.");
            found = property.Value;
        }
        return found;
    }

    private static InvalidOperationException Invalid(string message) => new($"Robo Call profile {message}");
}
