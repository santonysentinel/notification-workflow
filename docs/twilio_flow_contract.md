# Twilio Flow And Transition Contract

## Storage Change

No table columns are added for transitions. `CallFlowTemplates.TemplateJSON` now
uses the versioned schema below to describe the complete executable graph.
`CallFlowTemplates.TwiML` contains the unresolved XML responses for that graph.
The worker resolves ordinary variables into `AutomatedCalls.TwiML` and `Parameters`
before dialing, retaining the template's immutable `FlowId`.

The personalized bundle is not rewritten as the call advances. Its `Gather` actions
and `Redirect` URLs call `/webhooks/twilio/voice/next` with the call ID and the current
`{{executionId}}`. JavaScript selects a destination from TemplateJSON, extracts its
response from the bundle, and substitutes a fresh execution ID. SQL persists events
and exact replay responses atomically; it does not parse XML or evaluate branches.
Business actions are deferred and unsupported in this schema.

## Full TemplateJSON

This complete example supports an opening message, a digit/speech acknowledgement
question, and four terminal outcomes. IDs match the XML bundle below exactly.

```json
{
  "schemaVersion": 1,
  "initialStep": "battery-reminder",
  "steps": [
    {
      "id": "battery-reminder",
      "type": "redirect",
      "transitions": { "next": "acknowledgement" }
    },
    {
      "id": "acknowledgement",
      "type": "gather",
      "transitions": {
        "digits": { "1": "acknowledged", "2": "declined" },
        "speech": {
          "yes": "acknowledged",
          "acknowledge": "acknowledged",
          "no": "declined",
          "not now": "declined"
        },
        "noInput": "no-input",
        "fallback": "unrecognized"
      }
    },
    { "id": "acknowledged", "type": "terminal" },
    { "id": "declined", "type": "terminal" },
    { "id": "no-input", "type": "terminal" },
    { "id": "unrecognized", "type": "terminal" }
  ]
}
```

## Matching Rules

- `schemaVersion` is the integer `1`. `initialStep` identifies a defined step and
  must match the bundle's `initialStep`. Step IDs are unique, nonblank,
  case-sensitive strings of at most 200 UTF-16 code units.
- A `redirect` step requires `transitions.next`. It follows that target without
  evaluating gather input.
- A `gather` step requires explicit `noInput` and `fallback` targets. `digits` and
  `speech` maps are optional; each configured target must identify a defined step.
- Nonempty `Digits` takes precedence over `SpeechResult`. Digit strings match
  exactly, including multi-digit sequences, `*`, and `#`.
- Speech matches the complete phrase after trimming, lowercasing, and collapsing
  whitespace. No substring matching, regular expressions, fuzzy matching, or
  confidence threshold is applied. Duplicate normalized speech phrases are rejected.
- If neither digits nor nonblank speech is supplied, select `noInput`. If input is
  supplied but does not match, select `fallback`. `Confidence` is recorded, not used
  to select a transition.
- A `terminal` step has no transitions. Its response must end the call, normally
  with `<Hangup>`. `/next` does not synthesize a terminal response or automatically
  update completion status; `/status` handles terminal call reporting and guarded
  queue finalization. Worker feedback and business actions remain deferred.
- Unknown transition keys, dangling targets, unsupported step types, and embedded
  `actions` are rejected. Templates must be immutable: publish changes as a new
  template row/version rather than editing a row referenced by existing calls.

Selecting `acknowledged` records a conversational outcome only. It does not
acknowledge an alarm, add a note, snooze, or invoke any tenant-specific action.

## Matching TwiML Bundle

This is a personalized example for attempt 42. A stored template can use worker
variables for the recipient, base URL, and call ID; those must be resolved before
the attempt is submitted to Twilio. Only `{{executionId}}` remains unresolved.

```xml
<CallFlow initialStep="battery-reminder">
  <Step id="battery-reminder">
    <Response>
      <Say>Hello Alex. Your device battery is low.</Say>
      <Redirect method="POST">https://feedback.example.com/webhooks/twilio/voice/next?callId=42&amp;executionId={{executionId}}</Redirect>
    </Response>
  </Step>
  <Step id="acknowledgement">
    <Response>
      <Gather input="dtmf speech" numDigits="1" actionOnEmptyResult="true"
              action="https://feedback.example.com/webhooks/twilio/voice/next?callId=42&amp;executionId={{executionId}}"
              method="POST">
        <Say>Press 1 or say yes to acknowledge. Press 2 or say no to decline.</Say>
      </Gather>
    </Response>
  </Step>
  <Step id="acknowledged">
    <Response><Say>Thank you for your response.</Say><Hangup/></Response>
  </Step>
  <Step id="declined">
    <Response><Say>Your response has been recorded.</Say><Hangup/></Response>
  </Step>
  <Step id="no-input">
    <Response><Say>No response was received. Goodbye.</Say><Hangup/></Response>
  </Step>
  <Step id="unrecognized">
    <Response><Say>Your response was not recognized. Goodbye.</Say><Hangup/></Response>
  </Step>
</CallFlow>
```

`actionOnEmptyResult="true"` ensures silence invokes the explicit no-input branch.
Only the selected `<Response>` is returned to Twilio. Stored XML and graph snapshots
must still match at first transition commit; saved responses replay independently
of later bundle or graph changes.

See [the endpoint guide](twilio_endpoints.md) for authentication, requests, and events.