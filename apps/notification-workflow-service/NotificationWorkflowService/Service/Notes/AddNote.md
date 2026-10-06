# Add Note — profile priority 18

Both initial and step workflows support profile action 18. The selected profile's
`Instruction` is the template and `ActiveAlarm.ClientID` is the participant OID.
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

`INoteLocationProvider` reads one location snapshot per template invocation, and
`INoteAddressResolver` is invoked only if address is requested and both coordinates
are available and valid. Literal notes need neither provider.

Default providers deliberately return unavailable data. Consequently a location template
currently saves `N/A` values unless an implementation or mock is registered. No location
SQL procedure or external geocoding endpoint has been invented. Replace these providers
through DI when their real contracts are available. `AddNoteServices` preserves providers
registered beforehand.

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
behavior. Full suite: 1756 passed, no failures or skips. Live procedure execution, location
lookup, address resolution and database collation/ANSI OID handling remain unverified.