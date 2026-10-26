using System.Text.Json;
using System.Text.RegularExpressions;

namespace NotificationWorkflowService.Service.AutoCall;

/// <summary>A phone device to dial: a participant phone type, or <c>Custom</c> with an explicit E.164 number.</summary>
public sealed record RoboCallDevice(string Type, string? PhoneNumber = null)
{
    public const string Custom = "Custom";
    public static readonly IReadOnlyList<string> Types = ["CellPhone", "HomePhone", "WorkPhone", Custom];
}

/// <summary>
/// Action-19 settings read from tenant <c>ProfileItems.OptionsJSON.participantRoboCall</c>.
/// Property names match case-insensitively; unknown settings are ignored.
/// </summary>
public sealed record ParticipantRoboCallOptions(int FlowId, bool ShowParticipantResponseTranscript,
    bool WaitForCallCompletion = false, int CallTimeoutSeconds = ParticipantRoboCallOptions.DefaultCallTimeoutSeconds,
    RoboCallDevice? PrimaryCallDevice = null, RoboCallDevice? SecondaryCallDevice = null)
{
    public const string SectionName = "participantRoboCall";
    public const int DefaultCallTimeoutSeconds = 300;
    private static readonly Regex E164 = new(@"\A\+[1-9][0-9]{1,14}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Devices in dialling order: primary, then the optional secondary.</summary>
    public IReadOnlyList<RoboCallDevice> CallDevices =>
        new[] { PrimaryCallDevice, SecondaryCallDevice }.OfType<RoboCallDevice>().ToArray();

    public static ParticipantRoboCallOptions Parse(string? optionsJson)
        => Parse(optionsJson, SectionName, "ShowParticipantResponseTranscript", false);

    internal static ParticipantRoboCallOptions Parse(string? optionsJson, string sectionName, string transcriptProperty, bool officer)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
            throw Invalid("OptionsJSON is required.");
        JsonDocument document;
        try { document = JsonDocument.Parse(optionsJson); }
        catch (JsonException exception) { throw new InvalidOperationException("Robo Call profile OptionsJSON is not valid JSON.", exception); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("OptionsJSON must be a JSON object.");
            var section = Single(document.RootElement, sectionName)
                ?? throw Invalid($"OptionsJSON.{sectionName} is required.");
            if (section.ValueKind != JsonValueKind.Object) throw Invalid($"OptionsJSON.{sectionName} must be a JSON object.");

            var flow = Single(section, "FlowId");
            if (flow is not { ValueKind: JsonValueKind.Number } || !flow.Value.TryGetInt32(out int flowId) || flowId <= 0)
                throw Invalid($"OptionsJSON.{sectionName}.FlowId must be a positive integer.");

            bool showTranscript = Single(section, transcriptProperty) switch
            {
                null or { ValueKind: JsonValueKind.Null } => true,
                { ValueKind: JsonValueKind.True } => true,
                { ValueKind: JsonValueKind.False } => false,
                _ => throw Invalid($"OptionsJSON.{sectionName}.{transcriptProperty} must be a boolean."),
            };
            bool wait = Single(section, "WaitForCallCompletion") switch
            {
                null or { ValueKind: JsonValueKind.Null } or { ValueKind: JsonValueKind.False } => false,
                { ValueKind: JsonValueKind.True } => true,
                _ => throw Invalid($"OptionsJSON.{sectionName}.WaitForCallCompletion must be a boolean."),
            };

            int timeout = DefaultCallTimeoutSeconds;
            var timeoutElement = Single(section, "CallTimeoutSeconds");
            if (timeoutElement is { ValueKind: not JsonValueKind.Null })
            {
                if (timeoutElement.Value.ValueKind != JsonValueKind.Number || !timeoutElement.Value.TryGetInt32(out timeout) || timeout <= 0)
                    throw Invalid($"OptionsJSON.{sectionName}.CallTimeoutSeconds must be a positive integer or null.");
            }
            var primary = Device(section, "PrimaryCallDevice", sectionName, officer)
                ?? throw Invalid($"OptionsJSON.{sectionName}.PrimaryCallDevice is required.");
            var secondary = Device(section, "SecondaryCallDevice", sectionName, officer);
            return new ParticipantRoboCallOptions(flowId, showTranscript, wait, timeout, primary, secondary);
        }
    }

    private static RoboCallDevice? Device(JsonElement section, string name, string sectionName, bool officer)
    {
        var element = Single(section, name);
        if (element is null or { ValueKind: JsonValueKind.Null }) return null;
        string path = $"OptionsJSON.{sectionName}.{name}";
        if (element.Value.ValueKind != JsonValueKind.Object) throw Invalid($"{path} must be a JSON object or null.");
        var typeElement = Single(element.Value, "Type");
        string? type = typeElement is { ValueKind: JsonValueKind.String } ? typeElement.Value.GetString() : null;
        IReadOnlyList<string> allowedTypes = officer ? ["CellPhone", "CustomPhone"] : RoboCallDevice.Types;
        type = allowedTypes.FirstOrDefault(known => string.Equals(known, type, StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid($"{path}.Type must be one of {string.Join(", ", allowedTypes)}.");
        if (type == "CustomPhone") type = RoboCallDevice.Custom;
        var numberElement = Single(element.Value, "PhoneNumber");
        string? number = numberElement switch
        {
            null or { ValueKind: JsonValueKind.Null } => null,
            { ValueKind: JsonValueKind.String } => numberElement.Value.GetString(),
            _ => throw Invalid($"{path}.PhoneNumber must be a string or null."),
        };
        if (type == RoboCallDevice.Custom)
        {
            if (number is null || !E164.IsMatch(number))
                throw Invalid($"{path}.PhoneNumber must be an E.164 number when Type is Custom.");
        }
        else if (!string.IsNullOrWhiteSpace(number))
            throw Invalid($"{path}.PhoneNumber is only allowed when Type is Custom.");
        else number = null;
        return new RoboCallDevice(type, number);
    }

    internal static JsonElement? Single(JsonElement parent, string name)
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
