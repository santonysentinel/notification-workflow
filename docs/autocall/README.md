# Auto-Call Worker

## Phase 0: Tenant Routing And Phone Lookup

The C# service registers `AutoCallContactService` for use by the future auto-call
worker. Registration does not start another hosted worker or contact Redis/SQL.
Queue claiming, variable snapshots, recipient selection, attempt creation and
Twilio submission remain subsequent phases. No schema changes are needed for Phase 0.

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
    "DEV1": "Server=tenant-a.example;Database=TenantA;Integrated Security=true;Encrypt=true"
  }
}
```

Redis credentials and SQL credentials belong in secret-backed configuration, not
committed files. Environment keys include `AutoCall__Routing__RedisConnectionString`
and `ConnectionStrings__DEV1`. Existing `ConnectionStrings:connstr` continues to serve
the legacy workflow parser; it is never an implicit routing default.

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
```

Offline tests use injected Redis/SQL boundaries, the real Redis adapter with mocked
client interfaces, and data readers to verify SQL parameter/result contracts.
Validate the deployed Redis entry and procedure with a tenant smoke test before
enabling a future calling worker; live tenant infrastructure is not exercised by tests.