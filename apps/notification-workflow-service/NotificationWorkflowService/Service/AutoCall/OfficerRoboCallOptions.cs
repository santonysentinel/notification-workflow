using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed record RoboCallOfficerSelection(string Type, string? RoleType, [property: JsonPropertyName("roleId")] int? RoleID, string? RolePOGroup);

public sealed record RoboCallOfficer(RoboCallOfficerSelection Selection, int? Id, string? Name)
{
    internal static readonly JsonSerializerOptions QueueJson = new(JsonSerializerDefaults.Web);

    internal static RoboCallOfficer? ReadFromOptions(string? optionsJson)
    {
        if (optionsJson is null) return null;
        using var document = JsonDocument.Parse(optionsJson);
        if (!document.RootElement.TryGetProperty("officer", out var element)) return null;
        var officer = element.Deserialize<RoboCallOfficer>(QueueJson)
            ?? throw new InvalidOperationException("Queued officer is missing.");
        if (officer.Selection is null || officer.Selection.Type is not ("Primary" or "RoleBased" or "Custom") ||
            (officer.Selection.Type == "Custom" ? officer.Id is not null : officer.Id is not > 0 || string.IsNullOrWhiteSpace(officer.Name)))
            throw new InvalidOperationException("Queued officer identity is invalid.");
        return officer;
    }
}

public sealed record OfficerRoboCallOptions(ParticipantRoboCallOptions CallOptions, RoboCallOfficerSelection Officer)
{
    public const string SectionName = "officerRoboCall";

    public static OfficerRoboCallOptions Parse(string? optionsJson)
    {
        var call = ParticipantRoboCallOptions.Parse(optionsJson, SectionName, "ShowOfficerResponseTranscript", true);
        using var document = JsonDocument.Parse(optionsJson!);
        var section = ParticipantRoboCallOptions.Single(document.RootElement, SectionName)!.Value;
        var officer = ParticipantRoboCallOptions.Single(section, "Officer");
        if (officer is not { ValueKind: JsonValueKind.Object }) throw Invalid("Officer must be an object.");
        var typeElement = ParticipantRoboCallOptions.Single(officer.Value, "Type");
        string? type = typeElement is { ValueKind: JsonValueKind.String } ? typeElement.Value.GetString() : null;
        type = new[] { "Primary", "RoleBased", "Custom" }.FirstOrDefault(value => string.Equals(value, type, StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid("Officer.Type must be Primary, RoleBased or Custom.");
        string? roleType = OptionalString(officer.Value, "RoleType");
        string? group = OptionalString(officer.Value, "RolePOGroup");
        var roleElement = ParticipantRoboCallOptions.Single(officer.Value, "RoleID");
        int? roleId = null;
        if (roleElement is { ValueKind: not JsonValueKind.Null })
        {
            if (roleElement.Value.ValueKind != JsonValueKind.Number || !roleElement.Value.TryGetInt32(out int id) || id <= 0)
                throw Invalid("Officer.RoleID must be a positive integer or null.");
            roleId = id;
        }
        if (type == "RoleBased")
        {
            if (string.IsNullOrWhiteSpace(roleType) || string.IsNullOrWhiteSpace(group) || group.Length > 20 || group != group.Trim() || roleId is null)
                throw Invalid("RoleBased requires RoleType, a positive RoleID and RolePOGroup (at most 20 characters without surrounding whitespace).");
        }
        else if (roleType is not null || group is not null || roleId is not null)
            throw Invalid("Role fields are only allowed for RoleBased.");
        if (type == "Custom" && call.CallDevices.Any(device => device.Type != RoboCallDevice.Custom))
            throw Invalid("A Custom officer requires CustomPhone devices.");
        return new OfficerRoboCallOptions(call, new RoboCallOfficerSelection(type, roleType, roleId, group));
    }

    private static string? OptionalString(JsonElement parent, string name)
        => ParticipantRoboCallOptions.Single(parent, name) switch
        {
            null or { ValueKind: JsonValueKind.Null } => null,
            { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => throw Invalid($"Officer.{name} must be a string or null.")
        };

    private static InvalidOperationException Invalid(string message) => new("Officer Robo Call profile " + message);
}
