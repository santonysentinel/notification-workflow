# Asynchronous Deepgram Transcription

## Scope And Flow

Audio stays at Twilio. A future worker creates a transcription job/submission,
streams audio to Deepgram's prerecorded API, supplies a callback URL, and records
the returned request ID. Deepgram later posts the result to this API. The callback
does not download audio, contact Deepgram, schedule retries, or perform business actions.
Enqueueing and the worker are separate, deferred work.

Schema migration `database/010.sql` defines logical jobs, independent submissions,
and recording/submission-linked utterance rows with explicit millisecond timing.
Deploy `database/011.sql` for callback persistence and a nullable callback payload hash.

## Callback Contract

`POST /webhooks/deepgram/transcriptions/completed`

The callback uses HTTP Basic authentication: username is the submission's
`CorrelationId` UUID; password is an asymmetrically signed JWT from the existing
identity server. Do not assume Deepgram can obtain the JWT or attach a custom Bearer
header. Its documented Basic-auth callback URL is:

```text
https://<correlationId>:<JWT>@feedback.example.com/webhooks/deepgram/transcriptions/completed
```

The credential identifies the submission, so no query parameter is necessary.
Query parameters are rejected. Deepgram supports callback ports 80, 443, 8080,
and 8443; use HTTPS at ingress.
Do not log or persist credential-bearing URLs. Deepgram's optional `dg-token` is
supplementary only; it does not replace JWT validation.

Required JWT claims are `iss`, `aud`, `exp`, `jti`, `scope`, `submission_id` (the
CorrelationId), and positive integer `recording_id`. `scope` must contain the
configured callback permission. The issuer, audience and signing algorithm are
configured explicitly; only RS256 or ES256 are accepted. A configured public key
or trusted HTTPS JWKS endpoint supplies verification keys. Token-provided key URLs
are never used. The signing private key stays at the identity server.

Before contacting Deepgram, the future worker stores SHA-256 of the exact JWT text
in `CallbackTokenHash`. The API verifies the JWT and requires its hash and recording
identity to match that submission. Tokens remain reusable for authenticated retries
until expiry. Expiry must cover upload, provider queue/processing, callback retries,
and a margin. SQL callback deadlines and JWT expiry are independent; expired tokens
are rejected even for otherwise eligible late results. Reconciliation is future work.

## Payload And Persistence

Accept `application/json` bodies up to 8 MiB and at most 20,000 utterances.
Success uses Deepgram's prerecorded response:
`metadata.request_id`, `metadata.channels`, `metadata.duration`, `results.channels`, and
`results.utterances` (enable `utterances=true` when submitting). For non-empty speech,
utterances are required rather than inventing segmentation. Each utterance stores
its channel, text, confidence, and word metadata. Fractional-second offsets are
rounded to integer milliseconds. Channel counts must match the recording. A silent
result may have zero utterances and completes with zero transcript rows.
The channel array must match the submitted multichannel option. ProviderModel
comes from the immutable configuration snapshot; detected channel language takes
precedence over configured language. Original provider metadata stays in ResponseJSON.

Provider error callbacks use `request_id`, `err_code` (uppercase letters, digits
and underscores), and `err_msg`. LastErrorCode stores the validated code and
LastErrorMessage uses a generic description; provider error text is not logged.
Raw authenticated
JSON is stored on the submission for diagnosis and retention-controlled access.

Callback persistence locks the job and submission in a consistent order and commits
the response, callback hash/receipt time, submission outcome, transcript rows and
winning job outcome in one transaction. Canonical JSON hashing makes retries with
different object-key order or whitespace equivalent. Duplicate callbacks return 204
without changing saved data; conflicting finalized callbacks return 409 without
overwriting the first result. Provider request IDs must match when already known.
An early callback can populate the request ID before acknowledgement is recorded.
Future worker acknowledgement must not regress a finalized submission/job.

The first valid success may win while the job is READY, PROCESSING or WAITING_CALLBACK,
including from a TIMED_OUT submission. No active worker lease is required for callback
processing. COMPLETED winners are immutable; FAILED/DEAD_LETTER jobs are not reopened.
Nonwinning late responses are retained on their submissions but do not add displayed
transcripts or change the selected result. A provider failure marks the current
active attempt's job FAILED; failures from older attempts do not terminate newer work.
Future worker logic handles retries. Call, queue, and recording statuses are unchanged.

## Responses And Operations

| HTTP | Meaning |
| --- | --- |
| 204 | Persisted success/failure, duplicate, or retained nonwinning late response. |
| 400 | Invalid JSON/callback payload or unexpected query parameters. |
| 401 | Missing/invalid/expired JWT or credentials, claims binding, unknown submission, or stored token mismatch. |
| 409 | Provider request-ID mismatch or conflicting finalized payload. |
| 413 | Callback JSON exceeds 8 MiB. |
| 415 | Unsupported content type. |
| 500/503 | Persistence or verification configuration/key service unavailable; retry. |

Mount this public webhook before general bearer middleware, with its own JWT
authentication and bounded JSON parser. Rate limits and TLS belong at ingress.
Do not log authorization headers, tokens, callback URLs, transcript text, or raw
payloads. Deepgram retries non-2xx callbacks, so return 204 only after durable commit.

Configuration uses `DEEPGRAM_CALLBACK_JWT_ISSUER`, `DEEPGRAM_CALLBACK_JWT_AUDIENCE`,
`DEEPGRAM_CALLBACK_JWT_SCOPE` (default `transcriptions:callback`),
`DEEPGRAM_CALLBACK_JWT_ALGORITHM` (default `RS256`), and either
`DEEPGRAM_CALLBACK_JWT_PUBLIC_KEY` (PEM) or `DEEPGRAM_CALLBACK_JWKS_URL` (HTTPS).
`AUTO_CALL_DATABASE` selects the strict database pool and defaults to AutoCallDB.
Missing trust configuration fails closed. Deepgram's outbound API key is not needed
by the callback endpoint.
JWT verification permits five seconds of clock skew. JWKS fetches use a five-second
timeout and cached keys; publish overlapping keys during identity-server rotation.
An unknown signing key or invalid signature is rejected, never accepted on key-service failure.

Equivalent configuration-file keys are under `deepgram.callback`: `issuer`, `audience`,
`scope`, `algorithm`, `publicKey`, and `jwksUrl`. Environment settings take precedence.
Database selection falls back to `twilio.database` and then AutoCallDB; a missing
named pool never falls back to another database.

Deploy migration 011 after 010, configure identity trust, and expose the route through
TLS/rate-limited ingress. The future worker must first persist the job and submission,
including the exact JWT hash and callback deadline, before sending audio. Until then,
there are no automatic transcription submissions. Raw responses and transcript text
contain sensitive recording content; apply database access and retention controls.

## Verification

```powershell
pnpm --dir apps/notification-feedback-api exec mocha --timeout 10000 test/deepgramCallback.test.js
sqlcmd -S "(localdb)\NotificationWorkflowVoiceTests" -E -I -b -d NotificationWorkflowVoiceTests -i database/test/deepgramCallback.test.sql
```

Tests use generated RSA/ECDSA keys and mocked HTTP repository calls. The SQL suite
runs only in owned scratch databases, exercises winner/retry/terminal policies and
forces transcript-write rollback; it removes its fixtures. Production identity-server
and Deepgram connectivity still require a deployment smoke test.

Provider reference: https://developers.deepgram.com/docs/callback.