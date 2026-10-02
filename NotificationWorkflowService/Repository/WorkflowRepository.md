# Workflow SQL extraction

`IRepository` and `Repository` now own all active workflow command construction,
SQL parameters, connections and readers. The notification-delivery repository remains
separate. There are 26 named workflow operations, used by 69 caller adapters across
the two normal implementations, the step parser and the hosting service.

## Extraction order

1. Reference/contact queries: client profiles, profile items, holidays, officer roles,
   victim mappings, attached zones, clear-profile items and contact lookups.
2. Action writes: MCAPP, normal/victim/push queues, audit, history and clearing.
3. State/checkpoints: alarm polling, expired-alarm processing, archive/current state,
   parser activity and current/last processed points.

## Initial compatibility boundary

The existing parser/helper method names remain adapters. Their row mapping, duplicate
handling, partial dictionary mutation, retry loops, logging, return values and outer
catch behavior remain in the callers. `parseAlarms` action order and state decisions
were not changed. Original constructors delegate to repository-aware overloads;
host DI supplies `IRepository`, including the manually created step parser.

`Prepare…` creates a disposable `IWorkflowOperation` without executing SQL. Adapters
prepare once inside their original setup catch using `DeferredWorkflowOperation`,
then use that same operation for their entire retry loop. This deliberately retains
legacy repeated `Open()` behavior rather than silently introducing reconnection or
a second retry policy. The operation owns its command, connection and streaming readers.

Stored procedures, parameter names/order/types/sizes, CLR parameter values and timeouts
are retained. Profile reads still have an infinite command timeout. Current-state
insertion still maps the display state to `@CurrentStateNo` and next parser state to
`@ParserStateNo`. The integer parser-ID text query is now parameterized; its result
mapping is unchanged. Disabled push-settings SQL was not activated or exposed as an
empty-command operation.

Parser repositories retain a constructor-time connection-string snapshot. Hosting-service
checkpoint reads explicitly reload configuration per operation, as their old methods did.
Missing configuration remains validated by compatibility callers. Malformed connection-string
syntax now fails during preparation inside the setup catch, rather than when constructing
the old outer connection before that catch; this is a small exception-boundary difference.

## Validation

- `WorkflowRepositoryTests`: offline command metadata/value/lifetime/configuration tests.
- `WorkflowRepositoryAdapterTests`: fake streaming rows, duplicate handling, argument
  forwarding, ownership, preparation failures, representative action ordering, normal
  batch termination, step continuation and checkpoint behavior.
- Existing workflow characterization tests continue to cover selection/victim filtering
  and lexical priority order. SQL parameter assertions now inspect repository builders.

No live database or API is needed. Stored-procedure definitions, actual SQL commits,
deadlock/timeout execution paths and accepted victim-delivery paths remain unverified.

## Deferred work

This is intentionally a low-level compatibility repository, not the final typed/async API.
The parsers still depend on `IDataReader` and `SqlException` for existing mapping/retry
behavior. Next steps can move mapping into typed operations, introduce bounded cancellable
retry policy, and replace lexical tests with broader recording-repository runtime tests.
Those changes must be separate from SQL relocation: current unbounded retry risks,
ignored Boolean write results, non-transactional effects and Int timestamp assignments
have not been corrected here.