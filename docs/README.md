# Documentation

## Transcription

[Asynchronous Deepgram transcription](transcription/README.md) is the current
reference through implemented Phase 4. It covers deployment order, producer/API
configuration, immutable snapshots, authenticated WAV uploads and callbacks, SQL
lifecycle, worker leases/capacity, failure recovery, metrics, operator retranscription,
data selection, security/retention and verification. Provider access and the worker
remain disabled by default; live canary and retention cleanup are separate gates.

## Auto-Call And Recordings

- [Auto-call worker](autocall/README.md): tenant phone lookup, workflow enqueueing,
  attempt preparation, Twilio submission, recovery and rollout.
- [Twilio endpoints](twilio_endpoints.md): voice/recording webhooks and internal
  recording-content access. Recording completion enqueues transcription independently
  of call completion.
- [Flow contract](twilio_flow_contract.md): versioned graph and personalized TwiML
  bundle contracts.

Files under `temp/` are working material, not the deployment or operator reference.