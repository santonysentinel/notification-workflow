# Reference-data snapshots and resolution

All three parsers use `WorkflowReferenceDataBuilders` for reader-to-dictionary mapping
and `WorkflowProfileResolver` for holiday/profile/item selection. Neither component
executes SQL, logs, retries, or publishes live data. Builders consume but do not own readers.

## Publication contract

- Each loader builds a fresh local result on each attempt. It publishes only after the
  entire read, mapping, and operation disposal succeed. Failure preserves the old dictionary
  reference and contents; successfully reading zero rows replaces it with an empty dictionary.
- `setUpParser` stages all loads in a new `WorkflowReferenceData` shell. Only after all
  loads succeed does it replace the live dictionaries. A later failure discards earlier
  staged results. The `finally` block always clears pending staging, including rethrows.
- Normal parsers publish ten dictionaries, including the victim list/type pair, MEZ
  mappings, attached zones, and clear events. Steps retain their existing six dictionaries.
- Parser workers refresh/process serially. Publication assigns multiple fields sequentially;
  this is all-or-nothing on refresh failure, NOT a concurrent-reader atomic swap or an
  immutable collection API. Concurrent refresh/processing of one parser is unsupported.

## Preserved rules

Profile names/client mappings retain first-wins behavior; profile and clearing lists
retain duplicates/order; role keys still reject duplicates. Holiday rows retain their
contiguous-group requirement and empty-group sentinel. MEZ/zone sets still deduplicate.
Step victim rows do not read `VictimType`; normal rows rebuild victim types alongside victims.
Existing conversions and missing/invalid column failures remain unchanged.

Holiday matching uses all four untrimmed group keys and inclusive calendar dates, without
short-circuiting after a match. Missing holiday profiles do not fall back to normal profiles.
Initial selection includes the end time, prefers state zero, otherwise the minimum eligible
state, and keeps the first eligible duplicate. Step selection excludes the end time and
preserves existing loop-counter/successor rules. Only step-item resolution mutates the alarm's
loop counter; callers still apply priority, role contacts, clearing and fallback decisions.

SQL contracts, action order, checkpoint behavior and retry policies are unchanged. One legacy
exception distinction remains: step victim preparation failures rethrow rather than return
false; staging is discarded in either case. Reference disposal errors are now caught before
publication and reported through the loader's failure result.

## Tests

`WorkflowReferenceDataTests` adds 163 offline cases: preparation/disposal failures at every
setup position, old-data visibility during reads, complete and empty successful replacement,
staging cleanup, standalone loader behavior, partial-reader builder failures, duplicate and
grouping rules, and deterministic holiday/profile/step resolution. Existing characterization
and repository tests remain regression coverage. No live SQL/API calls are made.

Legacy retry loops remain unchanged; transient execution failures with actual retries are
not exercised here to avoid blocking sleeps and unbounded paths. Accepted notification
delivery, stored-procedure behavior and multithreaded parser use remain outside this coverage.