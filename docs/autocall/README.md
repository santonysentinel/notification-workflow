# Auto-Call Worker

Recording transcription is a separate opt-in workflow. Its deployment, producer/API
configuration, callbacks, recovery, metrics and audited retranscription procedures
are documented in the [transcription guide](../transcription/README.md).

## Phase 0: Tenant Routing And Phone Lookup

The C# service registers `AutoCallContactService` for use by the future auto-call
worker. Registration does not start another hosted worker or contact Redis/SQL.
Phase 1 adds queue snapshots and workflow enqueueing below. Phase 2 adds an opt-in
claim loop and attempt preparation. Phase 3 adds separately opt-in Twilio submission.
No schema changes are needed for Phase 0 itself.

Given an OID from the central AutoCallDB queue:

1. Read `dna.client.<uppercase OID>` from Redis.
2. Extract `platform` from the JSON object, or accept a plain platform name.
3. Resolve exactly `ConnectionStrings:<platform>` from C# configuration.
4. On that tenant database, execute `dbo.active_alarms_GetAllParticipantPhonenumbers`
   with `@oid` as `varchar(20)` and the original OID, unchanged.
5. Return the OID, platform and all nonblank string values from `phone_numbers`.

Redis OID/ClientUID fields are ignored; no OID-to-participant rewriting occurs.
Platform keys support ASCII letters, digits, underscores and hyphens, up to 100
characters. Routing is read for each lookup rather than cached across OIDs.
There is no fallback to `connstr`, AutoCallDB or any other configured database.
The central database remains responsible for queue/attempt data, not phone discovery.

## Phase 1: Queue Contract And Workflow Action

Workflow action **19: Auto Call Participant** enqueues in both normal and step
parser modes. Tenant profile queries must return the existing `ProfileItems.flowid`
field; the selected item's value becomes the alarm's `FlowId`. Missing/NULL columns
map to zero for older profiles, but action 19 rejects a missing/invalid FlowId.
This change does not create or migrate the assumed tenant profile field.

`AutoCallEnqueueService` reads the central, non-deleted `CallFlowTemplates` row
and captures a sorted `VariablesJSON` object. Placeholders in JSON string values
and XML text/attributes use `{{Name}}`; each business name must exactly match a
public `ActiveAlarm` property, including case (for example `OffenderName`,
`AlarmText`, `ClientID`). Unknown names, missing values and blank strings fail
before enqueueing. No aliases or implicit bindings are guessed. Values stay raw;
XML escaping belongs to attempt preparation. Publish flows against the complete
version-1 transition contract; the webhook graph validator remains authoritative.
Reserved `callId`, `feedbackBaseUrl`, `recipient` and `executionId` are not business snapshot
fields and remain the responsibility of attempt preparation/step issuance.

The action's enqueue UUID is deterministic: SHA-256 over the versioned occurrence
identity (case-folded platform, alarm ID, history ID, original OID, profile, step,
loop and FlowId), using the first 128 bits with UUID version-8/variant bits. Replays
of that occurrence retain its key. A different occurrence gets a different key;
changed variables do not create a new key to evade snapshot-conflict detection.
The unique key returns the original job even after processing/completion, without
resetting scheduling, attempt counts or status. A different alarm/OID/FlowId or
byte-different VariablesJSON with the same key fails with SQL error 51001.

Templates are immutable by publishing convention: edits require a new row/FlowId.
This migration does not prevent administrators from updating template rows directly.
Do not edit a referenced row in place. No recipient field is added or consumed;
phone discovery continues to use the queue's original OID and Phase 0.

### Deployment And Legacy Rows

Apply `database/013.sql` to AutoCallDB after the existing schema/migrations (012).
It adds nullable `VariablesJSON` and `EnqueueIdempotencyKey`, a snapshot constraint
and a filtered unique key, and replaces `activealarms_call_InsertJob` and
`activealarms_call_GetNextSetOfJobs`. Fresh installations include the columns in
001 and must still apply 013. Do not redeploy the old procedures from 003/004 afterward.
Deploy 013 before enabling action 19 and coordinate other enqueue callers.

InsertJob requires a complete snapshot and returns the original/new `SystemID`.
Claim results include `SystemID`, `ActiveAlarmID`, `OID`, `FlowId`, `VariablesJSON`,
`EnqueueIdempotencyKey`, `AttemptCount`, `MaxAttempts`, `LeaseToken`, `LockedUntil`.
Due READY jobs retain priority ordering, attempt limits and atomic lease ownership.
Migration 017 adds expired-lease recovery, described in Phase 4 below.

Legacy rows with no key or incomplete snapshots are never claimed, leased or
incremented. They remain visible in their original state, including READY; there
is no automatic backfill from current alarm/profile data. Operators must explicitly
cancel/dead-letter them, or supply a verified original snapshot, immutable FlowId
and unique occurrence key under a controlled migration. Never guess historical values.

The existing HTTP `POST /api/v1/autocall` caller now requires a `variables` JSON
object and a nonzero `enqueueIdempotencyKey` UUID alongside its existing identity
fields. Preserve both across retries; conflicting snapshots return HTTP 409.
Its existing routing behavior is unchanged. Action 19 uses direct central SQL access.

## Phase 2: Attempt Preparation

`AutoCallWorker` runs alongside the selected Normal or Step workflow worker only
when `AutoCall:Worker:Enabled` is true (default false). It claims no more than
`MaxConcurrency` minus its in-flight preparation/submission tasks, starts every claimed item
immediately, and polls again when capacity becomes available. There is no local
backlog of already leased jobs. Preparation has a cancellation deadline shorter
than the lease; host shutdown cancels outstanding work and drains those tasks.

`AutoCallProcessingService` uses Phase 0 to resolve the original OID and selects
the first number, in returned order, already matching E.164 (`+`, a nonzero first
digit, and 2-15 digits total). It never guesses country codes, splits strings or
normalizes numbers. Missing valid numbers fail preparation. Alternative-number
retry remains deferred. Opt-in provider submission is described in Phase 3.

`AutoCallRepository` uses asynchronous SqlClient connections and Dapper commands
against exactly `ConnectionStrings:AutoCallDB`, with explicit SQL types, 30-second
command timeouts and propagated cancellation. It calls the following procedures:

1. `automatedcalls_BeginAttempt` locks the queue row, checks PROCESSING status,
  current lease token, exact worker ID and SQL UTC expiry. It creates one Twilio
  attempt with queue ID, QueueLeaseToken, OID, recipient and FlowId, then returns
  the call ID, original attempt identity and flow/variable context. Replaying the
  same claim returns the original attempt, preserving its recipient.
2. Personalization parses XML with DTD/external entities disabled and a 2 MiB
  character limit. Values are substituted as text/attribute values, not raw XML.
  The graph's schema version and step identities must match the bundle. Runtime
  `callId`, `recipient` and `feedbackBaseUrl` override same-named input values;
  only `{{executionId}}` remains unresolved for subsequent step issuance.
3. Gather actions, Redirect destinations and Record actions use the configured
  HTTPS base URL plus `/webhooks/twilio/voice/next`, the allocated call ID and
  execution placeholder. Record status callbacks use
  `/webhooks/twilio/recordings/status?callId=<id>`. POST methods are explicit;
  Gather silence invokes its no-input callback. A configured path prefix is retained.
4. `automatedcalls_SavePreparation` checks ownership again and atomically stores
  the personalized TwiML bundle and complete Parameters object before returning
  the prepared attempt. Repeated identical saves do not update the row; changed
  snapshots fail with 51001. Lost, replaced or expired ownership fails with 51002.

Apply `database/014.sql` after 013 before enabling the worker. It adds a filtered
unique `(AutomatedCallQueueId, QueueLeaseToken)` index and the preparation/failure
procedures, and can be rerun. Existing duplicate non-NULL claim identities must be
resolved explicitly before deployment; the migration never deletes attempts or
backfills legacy lease tokens. Fresh installations must also apply 014.

Failures/timeouts call `automatedcalls_FailPreparation`: while ownership remains
valid, an unprepared claim becomes FAILED and its unsubmitted attempt becomes
failed, atomically. Only a fixed error code/message is stored. Already prepared
or provider-bound attempts are never failed by this path, including ambiguous
save outcomes. Host cancellation does not dispose the claim as failed.
This is the pre-017 failure policy. Phase 4 replaces worker failure disposition
with bounded recovery and dead-letter handling.

With Twilio submission disabled, this stage does **not** dial, download recordings or submit transcription jobs.
With 017, prepared jobs are rescheduled without dialing while submission is
disabled, and eventually dead-lettered at the recovery age limit. Enable
preparation alone only for controlled verification. Logs contain technical IDs and
failure types, not phone numbers, variables, XML or connection strings.

## Phase 3: Twilio Submission

Apply `database/015.sql` after 014 before enabling submission. Fresh installations
include the columns in 001 and must also apply 015 for the procedures. The migration
is rerunnable. Existing attempts retain NULL submission state and cannot be dialed
automatically; only newly inserted attempts receive READY. Never backfill historical
attempts as READY without proving that no provider request was made.

Apply `database/016.sql` after 015 **before deploying/enabling the inline-opening
worker**. It replaces BeginSubmission and Next to persist opening execution metadata
and support early inline callbacks. Do not overwrite them with old 015 or 006
afterward. The worker requires the persisted opening execution ID in the marker
response and refuses to POST when the database procedure is older. Migration 016
is rerunnable and does not backfill or change previously submitted requests.

Set both `AutoCall:Worker:Enabled` and `AutoCall:Twilio:Enabled` to true. The worker
submits only after personalized TwiML and Parameters have been persisted. The
dedicated typed `TwilioCallClient` uses the documented Twilio Call resource, pinned
to `https://api.twilio.com/2010-04-01/Accounts/<AccountSid>/Calls.json`, with per-request
Basic authentication, form encoding, no redirects/cookies or automatic retries,
and a bounded 64 KiB response. The timeout covers HTTP headers and body.

The worker sends the first step inline in the create-call request's `Twiml` field,
not the complete CallFlow bundle and not a `Url`. It selects the declared initial
step's Response, substitutes its execution UUID, and rejects unresolved placeholders
or unsafe XML before submission. This removes the opening webhook round trip.
The complete personalized bundle remains persisted for subsequent steps.

The sender must be an E.164 Twilio number or verified outgoing caller ID. The
recipient remains the prepared Phase 0 selection, not a queue field. All callback
methods are POST, with the same public HTTPS base/path prefix as the prepared bundle:

- Opening endpoint, retained for URL-based callers/replay: `/webhooks/twilio/voice/start?callId=<id>`; not requested by normal inline submission.
- Progress: `/webhooks/twilio/voice/status?callId=<id>`, events initiated/ringing/answered/completed.
- Recording: `/webhooks/twilio/recordings/status?callId=<id>`, event completed.

`RecordCalls` defaults false: whole-call recording requires explicit opt-in and
appropriate consent/compliance. The recording callback is configured either way;
flow `<Record>` callbacks retain their persisted bundle configuration. Match these
public URLs, account SID and auth token to the feedback API's Twilio signature
validation configuration, including any reverse-proxy path prefix.

`automatedcalls_BeginSubmission` checks the persisted preparation and current exact
worker/lease ownership, then commits SUBMITTING, a unique submission token, timestamp
and nonsecret request snapshot **before** the HTTP POST. Only that successful first
marker authorizes sending. Replays, legacy attempts and already-bound SIDs cannot
authorize a second call. The snapshot includes phone numbers and callback/account
identities; restrict database access and retention as personal data.

For inline requests, the same transaction also persists the opening StepIssued
event (`voice:start`) with its step ID, execution UUID and exact rendered response.
SQL verifies that response against the stored personalized bundle. Issuing the
opening does not bind a SID or mark the call answered. `/voice/start` can replay
this exact response without issuing another execution. The first signed `/voice/next`
may bind the SID before any progress callback or response save, but only for that
pre-issued inline execution after the existing transition/context checks pass.

`automatedcalls_SaveSubmission` fences the immutable attempt/lease/submission token,
not the queue's current lease. It can save a returned SID after an early `/start`
or `/status` callback, even after a terminal callback cleared queue ownership.
It preserves an identical existing SID; a different SID is retained as a sanitized
SID_CONFLICT/UNKNOWN outcome without overwriting callback identity. Submission
state/timestamps never overwrite CallStatus, lifecycle fields or queue status.
Twilio acceptance **does not complete the queue job**; terminal callbacks do that.

A validated 201 response becomes ACCEPTED; 4xx responses require a matching
Twilio JSON error code/status to become REJECTED. Unvalidated 4xx bodies, 408,
timeouts, transport failures, 5xx, redirects and malformed/oversized
responses become UNKNOWN. Shutdown after POST still uses a separate five-second
SQL persistence deadline to save the SID/outcome. If that save fails, SUBMITTING
remains durable. Phase 4 handles restart recovery and missing terminal callbacks.
Do not reset a marker or create a fresh attempt after uncertain acceptance:
that can place two calls. Alternative-number retries remain unsupported.

## Phase 4: Recovery And Retry Policy

Deploy `database/017.sql` after 016 before updating/enabling the worker. It is
rerunnable and replaces claim, preparation, submission and status procedures;
do not redeploy older versions of those procedures afterward. Recovery is part
of the existing opt-in worker, not a separate process. Both Worker and Twilio
remain disabled by default in application settings.

`AutoCall:Recovery` defaults are `RenewalSeconds=60`, `MaxSubmissionAttempts=3`,
`MaxPreparationAttempts=3`, `InitialBackoffSeconds=30`, `MaxBackoffSeconds=900`,
`ReconcileSeconds=120`, and `MaxRecoveryHours=24`. Renewal must occur sooner than
half the configured lease duration. Five-minute leases remain the default.
Backoff uses capped exponential ceilings with jitter between half and all of
the ceiling, persisted as SQL UTC `AvailableAt`; it does not occupy a worker slot.

Expired PROCESSING claims can be reclaimed atomically. The current queue token
changes; the attempt's original `QueueLeaseToken`, prepared snapshot and submission
token never do. `CurrentAttemptId` identifies the authoritative attempt. Renewal,
preparation, submission authorization, recovery disposition and provider observations
require the current unexpired token and exact worker identity. Renewal failure or
uncertain ownership cancels new work. An already in-flight HTTP request cannot be
undone; its late response still uses the immutable attempt/submission identity and
an independent persistence deadline. Callback finalization does not require a lease.

Validation failures dead-letter immediately with sanitized codes. Pre-submission
infrastructure failures retry within the preparation budget. Only a validated
HTTP 429 / Twilio `20429` rejection permits automatic resubmission; Twilio documents
that these requests were not processed. Each permitted retry creates a fresh attempt
using the original recipient, preserving the rejected attempt and opening execution.
SubmissionCount, not claim/reconciliation count, enforces the provider budget; the
queue's MaxAttempts is also an upper bound. Other definite rejections dead-letter.
No alternative-number fallback is attempted.

SUBMITTING/UNKNOWN without a SID wait for late responses/callbacks and never redial.
SID-bound attempts use an authenticated GET for that exact SID and original account,
recipient and sender. Missing callbacks, active calls, 404s, provider failures and
credential/account changes never authorize another POST. Terminal GET observations
finalize the authoritative queue and emit `CallReconciled`, without inventing callback
sequence numbers or recording metadata. Terminal busy/no-answer/failed/canceled
results end the job as FAILED, not as retryable submissions. Unresolved or still-active
calls dead-letter after the recovery age limit; confirmed terminal observations can
resolve them. A valid authoritative late callback can also resolve DEAD_LETTER.

Existing modern attempts with recorded original lease identity are linked only when
earlier submission history does not imply another unresolved/accepted call. Legacy
or conflicting history requires operator disposition, never guessed identities.
Inspect DEAD_LETTER rows and stored attempt/SID/request history; do not blindly clear
tokens or reset jobs to READY. Verify provider state before any operator-approved redial.
The worker does not log credentials, phone numbers, provider bodies or request snapshots.

## Configuration

The service's `appsettings.json` includes disabled-by-default AutoCall sections.
Replace its example FeedbackBaseUrl and fill routing/account/sender settings through
deployment configuration; provide credentials through secrets/environment variables.
The equivalent configuration structure is:

```json
{
  "AutoCall": {
    "Routing": {
      "RedisConnectionString": "redis.example:6380,ssl=true,defaultDatabase=0",
      "LookupTimeoutSeconds": 5
    },
    "Worker": {
      "Enabled": false,
      "MaxConcurrency": 1,
      "LeaseMinutes": 5,
      "PollSeconds": 5,
      "PreparationTimeoutSeconds": 60,
      "FeedbackBaseUrl": "https://feedback.example.com"
    },
    "Twilio": {
      "Enabled": false,
      "AccountSid": "<account_sid>",
      "FromNumber": "<account_no>",
      "TimeoutSeconds": 30,
      "RecordCalls": false
    }
  },
  "ConnectionStrings": {
    "AutoCallDB": "Server=central.example;Database=AutoCallDB;Integrated Security=true;Encrypt=true",
    "DEV1": "Server=tenant-a.example;Database=TenantA;Integrated Security=true;Encrypt=true"
  }
}
```

Redis credentials and SQL credentials belong in secret-backed configuration, not
committed files. Environment keys include `AutoCall__Routing__RedisConnectionString`
and `ConnectionStrings__DEV1`. Existing `ConnectionStrings:connstr` continues to serve
the legacy workflow parser; it is never an implicit routing default.
Action 19 requires exactly `ConnectionStrings:AutoCallDB` for central flow/queue
access, with no tenant/default fallback. AutoCallDB must already contain the OID's
`Client` row required by the queue foreign key.
Worker concurrency must be 1-100, lease minutes 1-60, poll seconds 1-300 and
preparation timeout positive and strictly shorter than the lease. FeedbackBaseUrl
must be absolute HTTPS without credentials, query or fragment. WorkerId defaults
to a generated UUID-based identity; any configured value must be unique per live
worker, nonblank and at most 100 characters. Environment activation is
`AutoCall__Worker__Enabled=true`; keep it false until migrations through 017 are deployed.
Submission additionally requires migrations 015/016/017, `AutoCall__Twilio__Enabled=true`
and secret-backed `AutoCall__Twilio__AuthToken`. Do not commit the auth token.
Twilio timeout must be 1-120 seconds; preparation timeout plus Twilio timeout plus
five seconds must remain strictly shorter than the lease. Keep submission disabled
until live tenant routing, public callbacks, signature validation and sender ownership
are verified. Capacity limits concurrent preparation/submission requests, not active
phone calls or Twilio account Calls Per Second; accepted calls may remain provider-queued.

Redis uses one lazy shared StackExchange.Redis connection with reconnection enabled.
The lookup deadline includes connection initialization and GET, defaults to five
seconds, and must be 1-60 seconds. Canceling a lookup does not cancel another caller's
shared connection. SQL commands have a 30-second timeout and propagate host cancellation.
Connections, commands and readers are disposed after each SQL lookup.

Missing Redis entries, invalid routing and unknown database keys are explicit
`AutoCallRoutingException` outcomes. Redis failures are masked as Unavailable;
requested cancellation propagates. SQL failures propagate to the future worker,
which owns retry policy. These services do not retry queries or log connection
strings, Redis payloads or phone numbers.

The procedure must return a string column named `phone_numbers`; each row's value
is retained as supplied, without splitting, normalizing, deduplicating or reordering.
NULL/blank values are omitted. An empty list means no available numbers; a missing
column or unexpected type is a contract error, not an empty success. E.164 validation,
priority rules and alternative-number retries remain separate worker policy.
Phase 2 adds the explicit first-valid-E.164 policy without changing this lookup contract.

## Usage And Verification

Inject `AutoCallContactService` and call `ResolveAsync(oid, stoppingToken)`.
The result does not expose a connection string and does not initiate a call.

```powershell
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter FullyQualifiedName~AutoCallContactsTests
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter FullyQualifiedName~AutoCallPreparationTests
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter FullyQualifiedName~AutoCallSubmissionTests
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter 'FullyQualifiedName~AutoCallEnqueueTests|FullyQualifiedName~WorkflowActionExecutorTests|FullyQualifiedName~WorkflowReferenceDataTests|FullyQualifiedName~WorkflowSelectionCharacterizationTests'
pnpm --dir apps/notification-feedback-api exec mocha --timeout 10000 test/autoCall.test.js
sqlcmd -S "(localdb)\NotificationWorkflowVoiceTests" -E -I -b -d NotificationWorkflowVoiceTests -i database/test/automatedcalls_QueueContract.test.sql
sqlcmd -S "(localdb)\NotificationWorkflowVoiceTests" -E -I -b -d NotificationWorkflowVoiceTests -i database/test/automatedcalls_Preparation.test.sql
sqlcmd -S "(localdb)\NotificationWorkflowVoiceTests" -E -I -b -d NotificationWorkflowVoiceTests -i database/test/automatedcalls_Submission.test.sql
```

Offline tests use injected Redis/SQL boundaries, the real Redis adapter with mocked
client interfaces, and data readers to verify SQL parameter/result contracts.
Validate the deployed Redis entry and procedure with a tenant smoke test before
enabling the calling worker; live tenant infrastructure is not exercised by tests.

## Phase 5: Verification And Rollout

Start with exactly one worker replica and `AutoCall:Worker:MaxConcurrency=1`
(the application and options default). Each request selects only the first valid
E.164 recipient; additional returned numbers are not dialed. The existing worker
capacity check bounds preparation/submission/reconciliation tasks. This limit is
per worker, not a fleet-wide limit, an active-call limit, or Twilio Calls Per Second.
Keep Worker and Twilio disabled until the staging prerequisites are satisfied.

### Verification Coverage

| Risk | Repeatable verification |
| --- | --- |
| Concurrent READY/expired claims | `AutoCallSqlConcurrencyTests`: two independent SQL connections released together, exactly one owner/token; expired reclaim does not increment submission attempts. |
| Duplicate enqueueing | Concurrent SQL enqueue test returns one ID and unchanged variables; `automatedcalls_QueueContract.test.sql` also covers replay and conflicting snapshots. |
| XML escaping | `AutoCallPreparationTests.CreatesIdentityThenPersonalizesAndPersistsBeforeReturning` round-trips `&` and `<` through XML, without injecting markup. |
| Missing variables | Enqueue validation and `MissingVariablesAndMismatchedGraphNeverSaveAPartialBundle` refuse incomplete snapshots before saving/sending. |
| Early callbacks | Submission/Start/Next/Status SQL suites cover callback-before-SID-save, inline execution replay and both SID-save timing orders. |
| Stale leases | Recovery SQL suite and C# heartbeat tests reject expired/replaced ownership and stop new work on lost or uncertain renewal. |
| Shutdown during submission | Submission tests retain UNKNOWN or accepted SID using an independent persistence deadline after host cancellation. |
| Acceptance followed by SQL failure | `PersistenceFailureDoesNotTriggerAnotherPostAndPreCancellationDoesNotMark` leaves the durable SUBMITTING marker, then replays without another POST or changing its identity. |
| One recipient and bounded capacity | Preparation tests select the first of multiple valid contacts and claim only free slots; the rollout default is one concurrent request. |

SQL race tests are opt-in and skipped explicitly when the connection environment
variable is absent. They only accept the owned LocalDB instance/database below,
integrated security, migrations through 017, and no existing READY/PROCESSING jobs.
They create committed, uniquely named fixtures so separate connections can race,
then remove only those fixtures in `finally`. Do not run alongside other database
suites or a worker. No test in this suite places a Twilio call.

```powershell
sqllocaldb start NotificationWorkflowVoiceTests
$env:AUTOCALL_TEST_SQL_CONNECTION = 'Server=(localdb)\NotificationWorkflowVoiceTests;Database=NotificationWorkflowVoiceTests;Integrated Security=true;TrustServerCertificate=true'
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter 'FullyQualifiedName~AutoCall' --verbosity minimal
Remove-Item Env:AUTOCALL_TEST_SQL_CONNECTION
sqllocaldb stop NotificationWorkflowVoiceTests
```

Also run the existing SQL suites and signed Start/Next/Status webhook tests before
deployment. A skipped race test is not evidence that the concurrency gate passed.

### Staging Twilio Gate

1. Use a dedicated staging database/tenant, an approved Twilio account/sender,
  secret-backed credentials, and one consenting test recipient. Apply migrations
  through 017, verify tenant routing, and configure the exact public HTTPS callback
  URLs and signature credentials on the feedback API. Isolate staging alarm inputs
  so unrelated requests cannot enter the queue during the canary.
2. Run one worker replica with `MaxConcurrency=1`. Enable Worker/Twilio only for the
  controlled canary. Enqueue one request with a unique key and complete variables;
  replay that same key and verify that it returns the same queue ID.
3. Confirm one provider call/SID to the intended recipient. Check the spoken opening,
  escaped variable text, Gather/Redirect transitions, signed status callbacks and
  final queue state. Test recording callbacks only with recording consent enabled.
  Inspect technical IDs/state without copying credentials or personal payloads into logs.
4. In isolated staging, verify restart/shutdown after submission does not create a
  second call. Suppress only the canary's terminal status delivery and verify known-SID
  GET reconciliation finalizes it. Restore callback delivery afterward. For an unknown
  SID outcome, verify no automatic redial and eventual operator/dead-letter handling;
  use fault injection in the automated suite rather than provoking real duplicate calls.
5. Record the deployment/migration version, queue/call IDs, SID, callback and
  reconciliation observations, submission count and final outcome in the controlled
  test record. Do not scale until the live canary passes without unexplained duplicate
  calls, SID conflicts, invalid signatures or unresolved outcomes.
6. Increase one setting at a time within the validated 1-100 per-worker bound,
  taking account of worker count and Twilio account limits. Monitor queue age,
  submission rate/20429 rejection, recovery failures and DEAD_LETTER rows at each step.
  Stop new enqueueing and disable the worker if the gate regresses; disabling it
  does not cancel provider calls already accepted. Keep the callback API available
  so those calls can finish, and reconcile their existing SIDs before resuming.

Mocked HTTP tests do not certify live Twilio credentials, sender authorization,
network reachability, spoken TwiML or callback delivery. Live validation remains an
operator gate; the repository does not automatically scale or mark that gate passed.