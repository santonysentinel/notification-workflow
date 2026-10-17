# Add Note — profile priority 18

Both initial and step workflows support profile action 18. The selected profile's
`Instruction` is the template, `ActiveAlarm.ClientID` is the participant OID, and
`ActiveAlarm.SystemID` is the active alarm ID passed to participant lookup.
The shared executor awaits `INoteService.AddNoteAsync`, then the parser performs its
existing final audit, archive and conditional state writes. Normal checkpoint behavior
is unchanged. The action does not change `ProcessNextStep` or add notification writes.

## Template and providers

Supported variables are case-sensitive: `{{lat}}`, `{{lon}}` and
`{{current_location_address}}`. Whitespace inside delimiters is permitted. Missing or
invalid coordinates and unavailable/blank addresses render as `N/A`; zero coordinates
are valid. Decimal coordinates are formatted using invariant culture.

Unknown variables, expressions and malformed braces reject the template before provider
access or persistence. Rendered provider strings are not recursively interpreted. Blank
notes, note text over 1000 UTF-16 code units and blank/OID values over 20 characters are
rejected, not silently truncated. Unexpected provider errors propagate rather than being
converted to unavailable data. Cancellation propagates through every awaited operation.

`ParticipantNoteLocationProvider` uses the shared `IParticipantCurrentDetailsRepository`
to read one snapshot from `ClientDatabase` per location-template invocation:

```sql
EXEC dbo.activeAlarms_GetParticipantCurrentDetails @OID = @oid, @ActiveAlarmID = @activeAlarmId;
```

The application sends `@OID` as `varchar(20)` and `@ActiveAlarmID` as `int`.
The procedure must return exactly one row with `offenderName`, `lat`, `lon`,
`alarmname`, and `last_location_time`; additional scalar columns are supported by
the shared lookup. Note consumes `lat` and `lon` for its existing location variables.
SQL NULL coordinates render as `N/A`; absent columns, malformed coordinate values,
missing/ambiguous rows and database failures are explicit errors, not missing-data
fallbacks. Database failures are logged without participant values or credentials.

Configuration uses top-level `ClientDatabase` first, then
`ConnectionStrings:ClientDatabase`, matching notification database access.
It never falls back to `connstr` or `AutoCallDB`.
`INoteAddressResolver` is invoked only if address is requested and both coordinates
are available and valid. Literal notes need neither provider.

The default address resolver still returns unavailable data, so
`current_location_address` renders as `N/A` until a real geocoder is registered.
`AddNoteServices` preserves pre-registered participant repositories and providers.
The participant lookup procedure must already exist in ClientDatabase; this change
does not create or alter its definition. Verify the deployed SQL parameter names
against the contract above.

## Persistence contract

`IRepository.PrepareAddNote` constructs a parameterized stored-procedure command:

| Procedure | Parameter | SQL type |
| --- | --- | --- |
| activealarms_AddNote | @Note | nvarchar(1000) |
| activealarms_AddNote | @OID | varchar(20) |

The procedure name and types/lengths were supplied by the user. **Parameter names `@Note`
and `@OID` are inferred and must be verified against the deployed procedure.** No stored
procedure is created or altered by this implementation. Successful completion without an
exception is treated as success; affected-row counts, including `-1` under `SET NOCOUNT ON`,
are not success predicates. A procedure that reports failure only through a return/output
value needs an additional contract before that signal can be handled.

Each invocation attempts a new insert. Duplicates are allowed: there is no invocation key,
deduplication or encompassing transaction. The service does not add automatic retries.
A later state/checkpoint failure, cancellation after SQL commit, or caller replay can write
the note again; a replay may hydrate a newer location. Allowing duplicates is not a durable
at-least-once delivery guarantee. Existing normal batch-break and step continuation behavior
remain in place; note/provider errors are not swallowed inside the note service. The existing
normal outer handler still logs/swallow errors after stopping the batch, and step failure
does not itself schedule a durable retry.

## Validation

Offline tests cover template syntax, unavailable data, bounds, invariant formatting,
one-snapshot/provider ordering, cancellation, SQL parameter metadata, disposal, repeated
writes, and all three parser entry points' action/state/checkpoint ordering and failure
behavior. Shared participant-reader tests cover typed SQL parameters, case-insensitive
column access, future scalar columns, NULLs and missing/ambiguous rows. Live procedure
execution, address resolution and database collation/ANSI OID handling remain unverified.