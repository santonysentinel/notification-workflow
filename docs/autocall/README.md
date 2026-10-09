# Auto-Call Worker

## Phase 0: Tenant Routing And Phone Lookup

The C# service registers `AutoCallContactService` for use by the future auto-call
worker. Registration does not start another hosted worker or contact Redis/SQL.
Phase 1 adds queue snapshots and workflow enqueueing below. Phase 2 adds an opt-in
claim loop and attempt preparation. Twilio submission remains future work.
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
Lease-expiry recovery is not implemented. The opt-in claiming worker is described below.

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
`MaxConcurrency` minus its in-flight preparation tasks, starts every claimed item
immediately, and polls again when capacity becomes available. There is no local
backlog of already leased jobs. Preparation has a cancellation deadline shorter
than the lease; host shutdown cancels outstanding work and drains those tasks.

`AutoCallProcessingService` uses Phase 0 to resolve the original OID and selects
the first number, in returned order, already matching E.164 (`+`, a nonzero first
digit, and 2-15 digits total). It never guesses country codes, splits strings or
normalizes numbers. Missing valid numbers fail preparation. Alternative-number
retry and provider submission policies remain later phases.

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
Automatic retry, lease renewal/reclamation and restart recovery remain deferred.

This stage does **not** dial, download recordings or submit transcription jobs.
Prepared jobs remain PROCESSING with a queued, unsubmitted attempt and their
original lease. Enable only for controlled preparation until the dialing/recovery
stage is available; expired or shutdown-interrupted claims require explicit
operator handling rather than guessed ownership. Logs contain technical IDs and
failure types, not phone numbers, variables, XML or connection strings.

## Configuration

Add these sections to the service configuration, replacing the example hosts:

```json
{
  "AutoCall": {
    "Routing": {
      "RedisConnectionString": "redis.example:6380,ssl=true,defaultDatabase=0",
      "LookupTimeoutSeconds": 5
    },
    "Worker": {
      "Enabled": false,
      "MaxConcurrency": 4,
      "LeaseMinutes": 5,
      "PollSeconds": 5,
      "PreparationTimeoutSeconds": 60,
      "FeedbackBaseUrl": "https://feedback.example.com"
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
`AutoCall__Worker__Enabled=true`; keep it false until migration 014 is deployed.

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
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter 'FullyQualifiedName~AutoCallEnqueueTests|FullyQualifiedName~WorkflowActionExecutorTests|FullyQualifiedName~WorkflowReferenceDataTests|FullyQualifiedName~WorkflowSelectionCharacterizationTests'
pnpm --dir apps/notification-feedback-api exec mocha --timeout 10000 test/autoCall.test.js
sqlcmd -S "(localdb)\NotificationWorkflowVoiceTests" -E -I -b -d NotificationWorkflowVoiceTests -i database/test/automatedcalls_QueueContract.test.sql
sqlcmd -S "(localdb)\NotificationWorkflowVoiceTests" -E -I -b -d NotificationWorkflowVoiceTests -i database/test/automatedcalls_Preparation.test.sql
```

Offline tests use injected Redis/SQL boundaries, the real Redis adapter with mocked
client interfaces, and data readers to verify SQL parameter/result contracts.
Validate the deployed Redis entry and procedure with a tenant smoke test before
enabling a future calling worker; live tenant infrastructure is not exercised by tests.