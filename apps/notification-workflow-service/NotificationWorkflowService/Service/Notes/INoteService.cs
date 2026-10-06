namespace NotificationWorkflowService.Service.Notes;

/// <summary>
/// Hydrates a note template and stores it once per invocation. Replays may create duplicates.
/// This service does not retry or guarantee that the existing workflow will replay a failed step.
/// </summary>
public interface INoteService
{
    Task AddNoteAsync(string template, string oid, CancellationToken cancellationToken = default);
}