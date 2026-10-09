# Auto-Call Worker

## Phase 0: Tenant Routing And Phone Lookup

The C# service registers `AutoCallContactService` for use by the future auto-call
worker. Registration does not start another hosted worker or contact Redis/SQL.
Phase 1 adds queue snapshots and workflow enqueueing below. Recipient selection,
attempt creation, a hosted claim loop and Twilio submission remain future work.
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
XML escaping and complete transition validation belong to attempt preparation.
Reserved `callId`, `feedbackBaseUrl` and `executionId` are not business snapshot
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
Lease-expiry recovery and the hosted claiming worker are not implemented here.

Legacy rows with no key or incomplete snapshots are never claimed, leased or
incremented. They remain visible in their original state, including READY; there
is no automatic backfill from current alarm/profile data. Operators must explicitly
cancel/dead-letter them, or supply a verified original snapshot, immutable FlowId
and unique occurrence key under a controlled migration. Never guess historical values.

The existing HTTP `POST /api/v1/autocall` caller now requires a `variables` JSON
object and a nonzero `enqueueIdempotencyKey` UUID alongside its existing identity
fields. Preserve both across retries; conflicting snapshots return HTTP 409.
Its existing routing behavior is unchanged. Action 19 uses direct central SQL access.

## Configuration

Add these sections to the service configuration, replacing the example hosts:

```json
{
  "AutoCall": {
    "Routing": {
      "RedisConnectionString": "redis.example:6380,ssl=true,defaultDatabase=0",
      "LookupTimeoutSeconds": 5
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

## Usage And Verification

Inject `AutoCallContactService` and call `ResolveAsync(oid, stoppingToken)`.
The result does not expose a connection string and does not initiate a call.

```powershell
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter FullyQualifiedName~AutoCallContactsTests
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter 'FullyQualifiedName~AutoCallEnqueueTests|FullyQualifiedName~WorkflowActionExecutorTests|FullyQualifiedName~WorkflowReferenceDataTests|FullyQualifiedName~WorkflowSelectionCharacterizationTests'
pnpm --dir apps/notification-feedback-api exec mocha --timeout 10000 test/autoCall.test.js
sqlcmd -S "(localdb)\NotificationWorkflowVoiceTests" -E -I -b -d NotificationWorkflowVoiceTests -i database/test/automatedcalls_QueueContract.test.sql
```

Offline tests use injected Redis/SQL boundaries, the real Redis adapter with mocked
client interfaces, and data readers to verify SQL parameter/result contracts.
Validate the deployed Redis entry and procedure with a tenant smoke test before
enabling a future calling worker; live tenant infrastructure is not exercised by tests.