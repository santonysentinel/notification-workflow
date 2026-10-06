# Workflow behavior baseline

This records characterized behavior, not recommended behavior. Subsequent SQL and
reference-data extractions preserve selection/action decisions; reference refresh now
retains old data on failure instead of clearing live dictionaries prematurely.
Normal mode uses `WorkFlowCommon`; `WorkFlowInitiator` is now a constructor-only
compatibility subclass after executable-equivalence and caller verification. Both entry
points are covered alongside the independent `WorkFlowSteps` orchestration.

## Coverage and limitations

- `WorkflowSelectionCharacterizationTests`: executes existing selection/holiday methods
  with isolated in-memory state; covers boundaries, overlap, list duplicates and loops.
- `WorkflowVictimCharacterizationTests`: executes the early filtering paths of both normal
  implementations; confirms zero HTTP/SQL calls for excluded victims. Accepted delivery
  paths and payload eligibility after settings lookup are not yet fully characterized.
- `WorkflowActionExecutorTests`: executes the shared priority switch against recording
  callbacks for every priority/role/mode, including argument order and failure paths.
- `WorkflowActionSourceCharacterizationTests`: checks parser-shell delegation, preamble,
  tail, checkpoint and helper failure branches. These are source-contract
  tests, NOT proof of successful writes, delivery, stored-procedure behavior or transactional
  atomicity. Recording-repository runtime tests now supplement representative paths.
- Existing notification tests cover delivery acknowledgements, history failure separation,
  repository retry behavior, cache snapshots, authentication and serialization.

`WorkflowRepositoryAdapterTests` now exercises streaming fake rows through the existing
adapters: profile/contact/victim duplicates, reference mapping, preparation failures and
representative runtime action/state/checkpoint sequences. SQL metadata is independently
covered by `WorkflowRepositoryTests`. No database or notification service is started.

`WorkflowReferenceDataTests` additionally verifies local builders and whole-refresh
publication: every preparation/disposal failure preserves the existing live references,
while complete successful loads (including empty results) replace them.

## Profile and holiday selection

Normal initial selection includes both start AND end times. It prefers eligible state 0;
otherwise the minimum eligible state number. The first eligible list item for that state
wins. An exact-end-time item can therefore remain eligible in normal mode.

Step selection includes the start and excludes the end. It uses the matching state in
state-number order, choosing the first eligible duplicate. No matching profile removes
the alarm from the step batch. Preserve this distinction until business rules are reviewed.

Holiday checks inspect POGroupNum, POGroup1, POGroup2 and POGroup3, inclusive by date.
Whitespace-only groups are skipped but dictionary lookup uses the original untrimmed key.
Null group strings currently throw. Holiday profile selection depends on this result.

## Victim exclusion

Normal parsers require an offender mapping. A known victim type other than exact `VAPP`
is excluded (case-sensitive). GPS20/GPS21/GPS76/GPS77 exclude victims whose ID differs
from MEZEventVictimID. 200/206/GPS14/GPS17 require membership in the attached-victim zone
mapping, keyed by the existing zone-ID/category construction. These exclusions are tested;
step processing has no equivalent cloud push/reminder path.

## Action order

Notation: G = officer-group notification helper; Q(n) = normal queue insert type n;
V(n) = victim/client queue insert type n; M = MCAPP insert; A(n) = audit type n;
H(n) = history type n; R = contact lookup. Arrows show attempted call order, not commits.
The executor runtime tests assert mode-specific arguments and ordered callback attempts;
parser-shell source tests cover the preamble and tail surrounding execution.

| Priority | Calls inside action branch |
| --- | --- |
| 1 | No action writes; disables subsequent current-state insertion |
| 2 | G → Q(3) → A(1) → H(1) |
| 3 | G → M; ProcessNextStep becomes 0 |
| 4 | G only; no dedicated fax insert |
| 5 | G → Q(1) → R(audit emails) → A(3) → H(0) |
| 6 | G → M; ProcessNextStep becomes 0 |
| 7 | M → G → Q(3) → A(1) → H(1); ProcessNextStep becomes 0 |
| 9 | G → M → Q(1) → R(audit emails) → A(3) → H(0); ProcessNextStep becomes 0 |
| 11 | Delay; no action writes, common audit/archive/state tail still runs |
| 12 / role 1 | M if instruction is nonempty; ProcessNextStep becomes 0 |
| 12 / role 2 | Q(3) → A(14) → H(1) if recipients nonempty |
| 12 / role 3 | Q(3) → A(14) → H(1), if recipients nonempty, in both modes |
| 13 | Build victim email list → V(3, victim flag) → A(14) → H(1) |
| 14 | G → M; ProcessNextStep becomes 0 |
| 15 | R(client email) → V(3) if nonempty → A(14) → H(1) |
| 16 | R(client text) → V(4) if nonempty → A(14) → H(1) |
| 17 | Build victim text list → V(4, victim flag) → A(14) → H(1) |

G does nothing unless EmailJoin == 1. Otherwise it calls Q(2); if that returns true,
it reads audit emails, writes A(3), reads audit emails again, and writes H(0).

Priority 0 has an archive label but no explicit action branch. 8/10 have neither an explicit
branch nor archive label. Normal defaults disable state insertion; step defaults do not.
An unmapped archive label throws when the dictionary is indexed; do not normalize silently.

Normal prelude: legacy victim push queue → cloud push/reminder (separate catches) →
A(15) → optional clearing → action branch. Steps have no equivalent push/clearing prelude.
Both append A(14) after the action switch, then archive, then optionally current state.
Normal action audits use step 1; steps use CurrentStateNo.

## State/expiry/checkpoint baseline

- Normal expiry is min(event timestamp + StateTime, UTC now + StateTime). Parsing the
  received timestamp also occurs even though the parsed value is unused.
- Step expiry uses UTC now + StateTime, not the stored previous expiry.
- Normal archive uses state 1. Step archive uses CurrentStateNo.
- Normal explicit-next-state insertion carries NextStateNo with display StateNo + 1,
  loop 0 and ProcessNextStep. Without explicit next state it uses StateNo + 1 and flag 1.
- Steps carry NextStateNo, CurrentStateNo display, CurrentLoopNumber and ProcessNextStep;
  fallback uses CurrentStateNo + 1, CurrentStateNo display, loop 0 and flag 1.
- The SQL current-state method maps display number to @CurrentStateNo and parser/next
  number to @ParserStateNo. Parameter names must not be swapped during extraction.
- Steps increment CurrentLoopNumber at LoopStartState. At >= NumberOfLoops they seek
  the next eligible state outside that loop, resetting the loop count to -1. If none
  exists the alarm is removed. Selection itself does not alter ProcessNextStep.
- Normal records sysID only after its archive/state calls return; after the batch it
  attempts a parser-activity update for the last completed alarm. Steps have no such checkpoint.

## Failure behavior and migration hazards

- Normal per-alarm exceptions break the batch; already completed sysID can still be
  checkpointed. The outer catch logs and swallows failures, including checkpoint failure.
- Steps catch per-alarm exceptions and continue to later alarms.
- Several action methods swallow exceptions/return false and callers ignore those results;
  archive/state methods often rethrow. Attempts and accepted effects are not equivalent.
- Contact/victim branches can catch lookup/queue failures and still attempt audit/history
  with an empty recipient description.
- Adapter retry loops reuse repository operations/open connections, classify deadlocks using ErrorCode/message text,
  and can loop indefinitely for non-deadlock SqlExceptions. Do not execute these legacy
  paths against a real database for characterization.
- Side effects are separate commands, not an encompassing transaction. Earlier writes may
  succeed before a later failure prevents checkpoint advancement, allowing replay.
- Legacy truncation and text-type inconsistencies are baseline quirks, not fixes in this step.

SQL operations are now extracted without changing these action/state decisions. Recording
fake operations cover representative actual attempted writes and injected preparation
failures. Remaining work includes broader runtime priority coverage, SQL execution/retry
failure injection and normal polling without console-title mutation.