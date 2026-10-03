# Common priority-action execution

`WorkflowActionExecutor.ExecuteAsync` owns the priority switch shared by both normal parsers
and the step parser. `WorkflowActionContext` supplies the mode, platform, logger, role
labels, reference dictionaries and `IWorkflowActionOperations` callbacks. The result
contains the insertion flag and profile-action summary; the alarm retains its existing
`ProcessNextStep` mutations.

Private parser adapters delegate to the original action helpers. Their SQL operation
ownership, Boolean results, retries, swallowed exceptions and rethrows remain unchanged.
The executor awaits callbacks sequentially with caller cancellation. It does not open
connections, start transactions, retry, or schedule delivery.

## Ordering and mode boundaries

- Normal victim push/reminder preamble, initial audit and optional clearing stay in the
  normal parser, before execution. They are not added to step processing.
- Every branch retains its officer/MCAPP/queue/contact/audit/history call order. Return
  values previously ignored remain ignored; assignments after writes still occur only
  after those calls return. Local victim/client catches still clear recipients and proceed
  with their audit/history calls where the original branch did so.
- Priority 7 retains audit type 1 in normal mode and type 3 in step mode.
- Normal branch audits use step 1; step mode uses `CurrentStateNo`, except victim/client
  priorities 13/15/16/17, which still use literal 1. Role action 3 still queues type 3.
- Mode-specific summaries are retained, including empty step summaries for 13/15/16,
  priority-14 singular/plural wording, and console/log formatting differences.
- Priority 1 disables subsequent current-state insertion in both modes. Unknown priorities
  disable it only in normal mode; the later archive-label lookup can still fail.
- Existing truncation behavior is retained, including normal priority 17's unused substring
  assignment and potential exception. These are compatibility quirks, not corrected rules.

The final summary audit, expiry calculation, archive, conditional state writes and normal
checkpoint stay in their parser shells. Normal per-alarm failures still terminate the batch;
step failures still allow later alarms. No workflow-wide transaction was introduced: earlier
effects can succeed before later failure, exactly as before. Transaction semantics require
separate confirmation, including stored-procedure and notification effects.

## Validation boundary

`WorkflowActionExecutorTests` supplies recording callbacks for 324 runtime cases across
priorities, roles and modes. It verifies parameters, operation order, summary text, mutation
timing, false returns, local catch continuation and injected failures at recorded operation
positions. Existing repository-adapter runtime tests cover representative full parser paths.

`WorkflowActionSourceCharacterizationTests` now checks parser-shell delegation, preamble and
tail ordering, expiry, checkpoint/catch rules and repository parameter mappings instead of
requiring three duplicated priority switches. It remains lexical coverage, not runtime SQL.

No live SQL/API is used. Not every priority is tested through each entire parser shell;
actual commits, transport delivery, console output/colors and transaction semantics remain
outside these runtime assertions.