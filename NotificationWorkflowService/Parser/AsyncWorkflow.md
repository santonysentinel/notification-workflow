# Async workflow and cancellation

Both worker modes propagate their stopping token through the hosting service, parser
setup/polling/execution, common action executor, repository operations, notification
settings, authentication, HTTP delivery and notification history. Operations are awaited
sequentially; this migration adds neither parallel action execution nor a workflow transaction.

## Contracts

- Parser I/O entry points now have `Async` names and return tasks: `setUpParserAsync`,
  `readPointsAsync`, `getExpiredAlarmsAsync`, `parseAlarmsAsync`,
  `PushAlertsToVictimsAsync` and `PushNotificationToVictimAsync`. Both normal constructor
  entry points remain, but their inherited executable public API is now async-only.
- `WorkflowActionExecutor.ExecuteAsync` awaits each callback with the same token.
  Existing write order, mode parameters and mutation timing are retained.
- `IRepository.Prepare…` remains synchronous because it only constructs commands; it
  performs no database I/O. `IWorkflowOperation` now uses native SqlClient async open,
  reader and non-query execution, `DbDataReader.ReadAsync`, async close and async disposal.
  Readers/commands/connections are disposed even on cancellation. Cleanup is not canceled.
- Reference builders await row reads and build locally. Cancellation discards staged
  reference data and leaves prior live dictionaries intact, including whole-refresh staging.
- Retry and polling sleeps use cancellable `Task.Delay`; catch blocks rethrow caller
  cancellation rather than treating shutdown as an ordinary failure, batch break or retry.
  Initial versus step orchestration and ordinary error behavior remain distinct.
- Notification delivery/token/settings callers now use only async APIs. The blocking
  delivery, response-handling and token wrappers have been removed. Pure in-memory
  resolution, mapping helpers and configuration-only initialization remain synchronous.

## Settings startup change

Notification construction configures the static settings caches without database reads.
Settings are loaded lazily on the first awaited accessor. Refresh and semaphore waits
accept cancellation; cancellation does not publish a snapshot or start the 30-second
failure cooldown. Successful and ordinary-failure cache lifetimes remain unchanged.
This replaces the previous eager, blocking constructor preload.

## Cancellation and partial effects

Cancellation interrupts subsequent work and checkpoint advancement; it cannot roll back
SQL commands or remote deliveries already accepted. Unconfirmed in-flight delivery retains
the existing review protection when idempotency is unconfirmed. Confirmed acknowledgements
remain removed if cancellation interrupts their history writes; history cancellation must
not requeue delivery or require review for a fully acknowledged batch.

Legacy SQL retry classification/counts and repeated-open behavior remain unchanged. Some
non-deadlock failures can still retry indefinitely, but caller cancellation is observed at
loop boundaries. Retry-policy hardening and transaction semantics require separate work.

## Validation

All existing behavior tests were migrated to await the new contracts without retaining
production sync wrappers. 306 additional offline cases cover cancellation/token propagation,
ordered awaiting, canceled row reads and snapshot rollback, action/local-catch cancellation,
archive/checkpoint boundaries, cancellable retry delays, settings serialization/refresh,
native repository pre-cancellation and confirmed-delivery history cancellation.

Full suite: 1,599 passed, no failures or skips. No service, live SQL connection or remote API
was started. Actual database-provider/server cancellation timing, stored-procedure effects
and deployed shutdown behavior remain integration-validation responsibilities.