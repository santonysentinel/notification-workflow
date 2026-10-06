# Initial and step orchestration

The two normal implementations were compared before consolidation. Their executable
method bodies were equivalent after normalizing self-types and indentation; constructor
syntax and logger types were the relevant differences.

`WorkFlowCommon` now owns the initial workflow implementation. `WorkFlowInitiator`
remains an internal constructor-only subclass in its original namespace, preserving
both original constructor signatures, DI registration and supplied logger category.
Its public methods (now async I/O entry points) and internal reference fields are inherited; private implementation
members remain private in the base class. Reflection-based tests traverse the hierarchy
explicitly rather than expecting inherited private members to be returned by reflection.

## Callers and separation

- The normal worker uses `WorkFlowInitiatorService` and its `Func<WorkFlowCommon>` factory.
- The host still registers both normal types. No production caller currently requests
  the compatibility type, but that entry point is retained rather than removed.
- Step mode still constructs `WorkFlowSteps` and polls expired alarms. It does not inherit
  initial orchestration or acquire initial victim-forwarding/checkpoint behavior.
- Worker-mode selection, initial versus step exception behavior, expiry/state parameters,
  reference publication and shared action execution are unchanged by consolidation.
- No transaction or retry-policy changes are included.

## Validation

Existing selection, victim, repository-adapter and reference-refresh suites still exercise
both normal entry points. Source contracts inspect the inherited implementation and verify
the compatibility class has no duplicated execution methods. Eleven new compatibility
cases verify constructor/public surface, initialization, logger categories, actual host
worker selection and registrations, and the independent expired-alarm step entry point.

Host tests build without starting services. They do not construct the real notification
sender or invoke its factory. Live SQL/API outcomes and external reflection callers that
depend on private members being declared on the compatibility type are not verified.