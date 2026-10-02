using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Entity;

namespace NotificationWorkflowService.Parser.Actions;

internal enum WorkflowActionMode
{
    Normal,
    Step
}

internal sealed class WorkflowActionContext(
    WorkflowActionMode mode,
    string platform,
    ILogger logger,
    IWorkflowActionOperations operations,
    IReadOnlyDictionary<int, string> roleActions,
    IReadOnlyDictionary<string, string> roles,
    IReadOnlyDictionary<string, List<Victim>> victims)
{
    internal WorkflowActionMode Mode { get; } = mode;
    internal string Platform { get; } = platform;
    internal ILogger Logger { get; } = logger;
    internal IWorkflowActionOperations Operations { get; } = operations;
    internal IReadOnlyDictionary<int, string> RoleActions { get; } = roleActions;
    internal IReadOnlyDictionary<string, string> Roles { get; } = roles;
    internal IReadOnlyDictionary<string, List<Victim>> Victims { get; } = victims;
}

internal readonly record struct WorkflowActionResult(bool Insert, string Summary);