using NotificationWorkflowService.Entity;

namespace NotificationWorkflowService.Parser.ReferenceData;

/// <summary>
/// A complete set of locally built reference dictionaries. Each instance starts empty;
/// callers can publish it only after every required builder has succeeded.
/// </summary>
internal sealed class WorkflowReferenceData
{
    public Dictionary<string, int> ClientProfileMapping { get; init; } = new();
    public Dictionary<string, int> ClientHolidayProfileMapping { get; init; } = new();
    public Dictionary<int, Profile> ActiveProfiles { get; init; } = new();
    public Dictionary<string, List<Holiday>> ActiveHolidays { get; init; } = new();
    public Dictionary<string, string> Roles { get; init; } = new();
    public Dictionary<string, List<Victim>> Victims { get; init; } = new();
    public Dictionary<string, string> VictimTypeDict { get; init; } = new();
    public Dictionary<string, HashSet<string>> MEZVictims { get; init; } = new();
    public Dictionary<string, Dictionary<string, HashSet<string>>> AttachedVictimZones { get; init; } = new();
    public Dictionary<int, List<ProfileItemClear>> ClearEvents { get; init; } = new();
}