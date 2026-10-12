# Asynchronous Deepgram Transcription

## Scope And Flow

Audio stays at Twilio. An accepted recording-completed callback creates a READY
transcription job. A future worker will claim it and use the submission service to stream audio
to Deepgram's prerecorded API, supplies a callback URL, and records
the returned request ID. Deepgram later posts the result to this API. The callback
does not download audio, contact Deepgram, schedule retries, or perform business actions.
Enqueueing, provider clients and durable submission coordination are implemented;
the hosted worker and runtime retry orchestration remain deferred.

Migration 018 now supplies the database claim/submission/recovery lifecycle described
under Phase 1 below. Phase 2 implements the C# provider clients and revised JWT
contract; neither deploying the API nor installing the migration enables a worker.

Schema migration `database/010.sql` defines logical jobs, independent submissions,
and recording/submission-linked utterance rows with explicit millisecond timing.
Deploy `database/011.sql` for callback persistence and a nullable callback payload hash.
Deploy `database/012.sql` after 011 to replace the recording-status procedure with
atomic transcription enqueueing. Do not redeploy the older procedure from 008 afterward.

## Job Creation

`dbo.automatedcalls_RecordingStatus` creates the job in the same transaction as
recording metadata and its audit event, only when a `completed` status is applied.
Each recording gets its own job, including a completion-first callback or a zero-duration
recording. Call completion itself does not enqueue work. Invalid callbacks,
in-progress/absent/failed statuses, and ignored terminal conflicts do not enqueue.
If job creation fails, metadata and the event roll back, and the webhook returns
an error so Twilio can retry rather than acknowledging unscheduled work.

The API freezes the following immutable configuration snapshot as version 2 for
new recording-completed jobs:

```json
{"model":"nova-3","version":"latest","language":"en","utterances":true,"multichannel":true,"diarize":false,"punctuate":false,"smart_format":false,"mip_opt_out":true}
```

The SQL procedure accepts optional trusted `@TranscriptionConfigurationVersion`
and `@TranscriptionConfigurationJSON` arguments; the HTTP callback does not accept
these settings from Twilio. Use a new version when intentionally changing configuration.
Direct SQL callers must explicitly supply complete versioned options: migration 012's
legacy procedure defaults remain version 1. Existing jobs are never rewritten or
backfilled. The Phase 2 client rejects incomplete snapshots before creating a submission;
review legacy jobs explicitly before enabling the Phase 3 worker.
Jobs start READY with zero attempts, no lease or submission, and availability at
creation time. Existing jobs with the same recording/provider/version are preserved.
Repeated completed callbacks neither reset jobs nor create another configuration
version. Retranscription requires a separate explicit operation, not replaying a
recording callback. Historical completed recordings are not backfilled by migration 012.

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

Required JWT claims are `iss`, `aud`, `exp` and `permissions`. Permissions are
comma-separated positive decimal IDs; complete trimmed entries must match the
configured feature permission ID exactly. No custom identity claims, `scope` or
`jti` are required. The issuer, audience and signing algorithm are
configured explicitly; only RS256 or ES256 are accepted. A configured public key
or trusted JWKS endpoint supplies verification keys. HTTPS is required unless the
explicit identity HTTP exception below is enabled. Token-provided key URLs
are never used. The signing private key stays at the identity server.

Before contacting Deepgram, the submission service stores SHA-256 of the exact JWT text
in `CallbackTokenHash`. The API verifies the JWT, looks up the Basic correlation UUID,
requires the exact stored hash, and derives recording identity from the trusted database
row, not token or payload claims. Tokens remain reusable for authenticated retries
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
processing. COMPLETED winners are immutable; FAILED jobs are not reopened. With
migration 018, valid late success may complete DEAD_LETTER; credential expiry applies.
Nonwinning late responses are retained on their submissions but do not add displayed
transcripts or change the selected result. A provider failure marks the current
active attempt for retry with backoff when allowlisted as transient (or DEAD_LETTER
when budgets expire); other failures become permanently FAILED. Failures from older
attempts do not terminate newer work. Call, queue, and recording statuses are unchanged.

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
`DEEPGRAM_CALLBACK_PERMISSION_ID` (required; no inferred/default permission),
`DEEPGRAM_CALLBACK_JWT_ALGORITHM` (default `RS256`), and either
`DEEPGRAM_CALLBACK_JWT_PUBLIC_KEY` (PEM) or `DEEPGRAM_CALLBACK_JWKS_URL` (HTTPS).
Alternatively set `DEEPGRAM_IDENTITY_BASE_URL` and `DEEPGRAM_IDENTITY_JWKS_PATH`
(default `/.well-known/jwks.json`). `DEEPGRAM_IDENTITY_ALLOW_HTTP=true` explicitly
permits only the exact base host/port and configured JWKS path; other HTTP URLs and
redirects are rejected. Configure issuer and audience as `sentineldna.com` for the
agreed identity server, independently of its network address.
`AUTO_CALL_DATABASE` selects the strict database pool and defaults to AutoCallDB.
Missing trust configuration fails closed. Deepgram's outbound API key is not needed
by the callback endpoint.
JWT verification permits five seconds of clock skew. JWKS fetches use a five-second
timeout, a 64 KiB response bound and cached keys; publish overlapping keys during identity-server rotation.
An unknown signing key or invalid signature is rejected, never accepted on key-service failure.

Equivalent configuration-file keys are under `deepgram.callback`: `issuer`, `audience`,
`permissionId`, `algorithm`, `publicKey`, `jwksUrl`, `identityBaseUrl`, `jwtJwksPath`
and `allowInsecureIdentityHttp`. Environment settings take precedence.
Database selection falls back to `twilio.database` and then AutoCallDB; a missing
named pool never falls back to another database.

Deploy migration 011 after 010, configure identity trust, and expose the route through
TLS/rate-limited ingress. The submission service persists identity, JWT hash/expiry and
the one-shot send marker before uploading audio. The future worker must claim jobs,
renew leases and interpret submission outcomes. Until then,
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

## Phase 0 Worker Contract

Status: agreed design decisions. Phase 1 implements the database portion and Phase 2
implements provider configuration/authentication and submission coordination. The hosted
worker, retry orchestration and retention cleanup are not implemented or enabled.

### Authentication

- Configure an identity base URL and `jwtJwksPath`, initially
	`http://172.27.112.243:8085` and `/.well-known/jwks.json`. Resolve the configured
	path against the trusted base URL, never against token-provided URLs.
- Configure the fixed permission ID granted by the identity server. Its display
	mapping may be `deepgram-transcription-feature`; authorization checks the actual
	ID, not its display text. The JWT `permissions` claim is a comma-separated string
	of positive decimal IDs. Split on commas and match complete trimmed IDs exactly;
	permission `12` must not match `112`. Missing/malformed permissions or a missing
	required permission fail closed. The actual feature permission ID is deployment
	configuration; do not infer it from the example token or use a permissive default.
- JWTs do not contain `submission_id` or `recording_id`. Keep signature, issuer,
	audience, expiry and permission validation. Use the Basic username correlation
	UUID to look up the submission; require SHA-256 of the exact JWT to match its
	stored credential hash, and derive recording identity from that trusted row.
	Do not trust a recording ID from the callback body as authentication evidence.
- Require `iss`, `aud`, `exp` and `permissions`; the supplied token has no `jti`,
	so it is not a required claim. Validate issuer and audience as `sentineldna.com`,
	configurable independently of the identity server's HTTP base URL. A `scope`
	claim is not required. Keep an explicit asymmetric algorithm allowlist (RS256
	or ES256); confirm the deployed algorithm/key compatibility during integration.
- Target configurable JWT validity of 120 minutes. Identity-server issuance must
	support this; a worker setting cannot extend a signed token's expiry. Obtain a
	token with sufficient remaining validity before marking a submission sendable.
- Reuse POST `accounts/authenticate2` relative to the configured identity base URL,
	with JSON `{signInName,password}` and response `{isSuccessful,jwt}`. Require
	`isSuccessful: true` and a nonempty JWT. Supply credentials through secrets, not
	committed application settings. The JWKS path supplies verification keys, not tokens.
- Internal HTTP token issuance and JWKS retrieval are explicitly approved for now.
	Setting `AllowInsecureIdentityHttp` defaults false and must be enabled
	for this deployment. Restrict the exception to the configured identity host,
	port and paths; reject redirects and token-provided key URLs. Do not apply it to
	Deepgram or public callback ingress, which remain HTTPS. The accepted residual
	risks are credential interception and verification-key substitution; a private
	IP alone does not prevent them. Prefer HTTPS when available. Both the C# client and
	callback API require independent explicit opt-in for this exception.
- Replace a provably unsent submission after restart with a fresh correlation ID
	and token on the same logical job. Preserve prior audit records and credential
	hashes. Do not change the credential on an already-sent submission.

### Lifecycle And Recovery

- Every completed recording is eligible and gets its own job, including multiple
	recordings for one call. This is a new application; no historical backfill is planned.
- Zero-duration recordings complete with an empty result. Proposed implementation:
	local completion with zero transcript rows and an explicit completion origin,
	without fabricating a Deepgram request ID or response. The existing winner-required
	completion constraint needs a deliberate migration for this path.
- Five maximum submission attempts; claims and renewals do not consume attempts.
	Stop creating submissions after a configurable total job age of 24 hours.
	Preparation retries also need bounded backoff and the total-age guard.
- Accepted submissions wait 60 minutes from acknowledgement for a callback.
	Ambiguous submissions wait 60 minutes from the durable send marker before retry.
	Lease expiry or restart alone never authorizes an immediate ambiguous resend.
- Missing callbacks and ambiguous acceptance may trigger a fresh submission;
	duplicate provider processing and billing are accepted. Keep each submission's
	identity and credential hash so an authenticated older result can still win.
- Retry rate limits, provider outages and transient media failures. Proposed
	exponential backoff with jitter: 30-second initial ceiling, capped at 15 minutes.
	Treat permanently missing media, invalid audio and invalid configuration separately;
	do not blindly retry them. Surface authentication/configuration failures to operators.
- Retryable failures return to READY with a future AvailableAt. FAILED is permanent;
	exhausted retry/age budgets use DEAD_LETTER. A valid late success may complete a
	DEAD_LETTER job, subject to token validity; existing COMPLETED winners are immutable.
	Reopening permanently FAILED jobs is not approved by this decision.
- Callback deadlines do not invalidate otherwise valid late credentials. JWT expiry
	remains enforced, so a 120-minute token does not authorize callbacks indefinitely.
- Deepgram request lookup provides request/usage metadata, not recoverable transcripts.
	Async results are deleted after callback delivery or exhausted retries. No documented
	prerecorded submission idempotency mechanism was found; recovery uses resubmission.

### Tunable Initial Defaults

These are proposed initial operational defaults, not changes to existing snapshots
or active application settings. Freeze effective provider options per job/configuration
version; later tuning must not mutate queued jobs or submissions.

| Setting | Initial value |
| --- | --- |
| Worker enabled | false; enable only for the canary |
| Upload concurrency / outstanding submissions | 1 / 10 |
| Audio | Authenticated Twilio WAV streaming; preserve recorded channels |
| Channel fallback | No silent downmix; fail explicitly on a mismatch |
| Model / model version | nova-3 / provider latest; retain returned model metadata |
| Language | en; configurable fixed language or supported detection mode |
| Utterances / multichannel | true / true |
| Diarization / punctuation / smart formatting | false / false / false |
| Model improvement | Opt out with mip_opt_out=true |
| Deepgram endpoint | https://api.deepgram.com; configurable trusted regional endpoint |
| Maximum recording duration / upload bytes | 60 minutes / 128 MiB |
| Media header timeout / overall submission timeout | 30 seconds / 10 minutes |
| Callback body / utterance limits | Existing 8 MiB / 20,000 |
| Raw response / normalized transcript retention | Proposed 30 days / 90 days |

Retention cleanup must preserve submission identity, credential hashes and winning
references needed by live jobs and late callbacks. Never delete active work to meet
a retention target. These periods do not authorize automatic Twilio audio deletion.
If a required regional endpoint is selected, do not silently fall back to another
region. Provider opt-out can affect pricing; verify it in the live canary.

Phase 0 authentication and lifecycle design decisions are settled. Before enabling
the worker, supply the actual feature permission ID, secret-backed login credentials
and signing-algorithm configuration, and verify issuance lifetime/JWKS compatibility
against the deployed identity server. Do not invent a permission ID from the sample.
Phase 2 replaces the old scope/custom-claim requirements with permission-ID validation
and the restricted HTTP policy. Cleanup and hosted-worker orchestration remain future work.

## Phase 1 Database Lifecycle

Deploy `database/018.sql` after 017 (and the transcription prerequisites 010-012).
Use the same ordered migrations for a fresh database after schema 001. Do not
redeploy 011 afterward: it would restore the old callback terminal/failure policy.
No existing recording, call or enqueue procedure is changed by 018.

The migration adds CurrentSubmissionId with a same-job foreign key, persisted
per-job recovery settings, immutable SubmissionToken/CreatedLeaseToken identity,
send-start and token-expiry timestamps, and an explicit EMPTY completion origin.
On first upgrade, AttemptCount is normalized to the number of stored submissions
and the highest numbered submission is selected as current. Noncontiguous or
over-budget history aborts migration instead of inventing or deleting attempts.
Reruns preserve existing identities, row versions, counters and deadlines.

| Procedure | Contract |
| --- | --- |
| transcription_ClaimJobs | Atomically claim due READY or expired PROCESSING work; return a fresh lease without incrementing attempts |
| transcription_RenewLease | Renew only the exact current worker/token with an unexpired SQL-UTC lease |
| transcription_GetWorkContext | Read fenced configuration, recording media/channels and current submission recovery context; never return raw credentials |
| transcription_CreateSubmission | Store correlation/token/hash/expiry as PREPARED and increment attempts once; same identity replay is duplicate |
| transcription_BeginSubmission | Atomically change current PREPARED to SUBMITTING and persist send time/deadline; only CanSend=1 authorizes one HTTP POST |
| transcription_SaveAcceptance | Save the request ID using immutable job/correlation/submission-token identity, independent of current worker ownership |
| transcription_ScheduleRetry | Fenced RETRY, FAIL or WAIT; jittered bounded backoff; definite rejection required to retry a marked send immediately |
| transcription_RecoverTimeouts | Bounded lock-protected sweep of waiting/expired/aged work; preserve pending deadlines, then schedule retry or dead-letter |
| transcription_CompleteEmpty | Fenced local zero-duration completion without a fabricated provider ID, submission or transcript row |
| transcription_CompleteCallback | Preserve auth/hash/request deduplication and atomic transcript writes; schedule transient failures and permit late DEAD_LETTER success |

Creation returns created, duplicate, stale-lease, pending-submission or exhausted.
The worker must use the durable send procedure even after creation succeeds;
replaying creation or a send marker is never permission to send again. A new lease
cannot send an old PREPARED identity: replace it with fresh correlation/credentials.
Callback credential expiry must exceed the configured wait at both creation and
send acquisition. Never persist the JWT or credential-bearing callback URL.

Acceptance extends the current SUBMITTING deadline from acknowledgement, releases
worker ownership and sets WAITING_CALLBACK. Acknowledgement after callback or timeout
records provider identity without regressing the finalized submission/job. Repeated
acknowledgements cannot extend deadlines. Older acknowledgements cannot change the
current submission; request identity conflicts return request-conflict without mutation.

Use WAIT for uncertain transport outcomes; only a classified definite rejection
permits RETRY/FAIL for SUBMITTING. ACCEPTED work always uses callback timeout recovery.
Expiry of a worker lease is not expiry of the provider callback wait, even on the
final allowed attempt. Recovery never touches an actively owned PROCESSING job.
The age limit stops further work but does not prevent an authenticated late winner.

Callback retry allowlist: REMOTE_CONTENT_ERROR, INTERNAL_SERVER_ERROR,
SERVICE_UNAVAILABLE, TOO_MANY_REQUESTS, RATE_LIMIT_EXCEEDED and TIMEOUT. Unknown
provider error codes fail permanently and require review. REMOTE_CONTENT_ERROR
retries are bounded because this category can include permanent remote media errors.
Failure applies only to CurrentSubmissionId; old manually populated histories with
no current reference retain the legacy AttemptNumber/AttemptCount comparison.

Initial persisted SQL policy is five attempts, 1440-minute job age, 60-minute callback
wait and exponential equal-jitter backoff with a 30-second ceiling capped at 900
seconds. Claim defaults are one job and a 300-second lease. Claim/reclaim/renewal
never spend attempts; new persisted submission identities do, including unsent replacements.
Tune policy before work begins; do not mutate in-flight provider option snapshots.

Run all SQL scripts under `database/test` in the owned test database after deploying
018. `transcriptionLifecycle.test.sql` covers fencing, budgets, early/late outcomes,
uncertain waits and empty completion. The opt-in C# `TranscriptionSqlConcurrencyTests`
uses two real connections for claim, creation/send, acknowledgement/callback and
timeout/callback races, and checks late acknowledgement after replacement. Set
`AUTOCALL_TEST_SQL_CONNECTION` only to the owned LocalDB instance/database:

```powershell
$env:AUTOCALL_TEST_SQL_CONNECTION = 'Server=(localdb)\NotificationWorkflowVoiceTests;Database=NotificationWorkflowVoiceTests;Integrated Security=true;TrustServerCertificate=true'
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter FullyQualifiedName~TranscriptionSqlConcurrencyTests
```

Race fixtures are committed to exercise genuine concurrency, then removed. Tests
refuse non-owned connections or a non-idle transcription queue; do not run them
concurrently with transactional SQL suites. Without the environment setting they
explicitly skip, which is not sufficient for the Phase 1 exit gate.

## Phase 2 Provider Clients

`Transcription:Enabled` defaults false. Registration adds no hosted transcription worker.
Use secret-backed environment settings or user secrets for `Transcription:DeepgramApiKey`,
`Transcription:Identity:SignInName` / `Password` and Twilio credentials. Environment
keys use double underscores, for example `Transcription__Identity__PermissionId`.
Never commit credentials. The actual permission ID is intentionally unset.

Identity login POSTs `{signInName,password}` to `accounts/authenticate2`, requires
`isSuccessful=true` and verifies the returned JWT against bounded, pinned JWKS keys.
RS256 or ES256, issuer, audience, expiry/not-before and exact CSV permission matching
are enforced. Tokens must outlive the job's callback wait plus the whole submission
timeout and five seconds. `TokenValidityMinutes=120` is the configured validity target,
not a way to extend server-issued tokens. Insufficient expiry fails before a send marker.
The approved internal HTTP deployment requires `Identity:AllowInsecureIdentityHttp=true`
and the callback API's separate HTTP opt-in. Redirects and token-provided key URLs are
never followed. The accepted credential-interception/key-substitution risk remains.

Twilio retrieval uses pinned `https://api.twilio.com` account/recording SID paths and
`.wav?RequestedChannels=1` or `2`, never an untrusted recording URL. Prefer an API key
SID/secret; Account SID/AuthToken is also supported. Only completed nonempty recordings
with known mono/stereo metadata are accepted. Bounded RIFF/WAVE PCM16 headers must
match recorded channels and duration; no conversion or silent downmix is performed.
The complete stream is checked for truncation, excess bytes and the maximum byte bound.
Defaults: 30-second media header timeout, 60-minute duration and 128 MiB audio bound.
Zero-duration completion belongs to the future worker's local EMPTY path, not these clients.

The service validates the immutable snapshot, acquires a fresh callback token and
validates media headers, then calls `transcription_CreateSubmission` (identity/hash/expiry),
`transcription_BeginSubmission` (one-shot send marker), and only then Deepgram POST
`/v1/listen`. A pending/exhausted/stale creation or replayed marker never sends audio.
The upload uses WAV bytes, the exact snapshot options, a Basic-auth HTTPS callback,
POST callback method and correlation tag. The tag is not an idempotency key. No POST
retry policy is installed. A trusted regional Deepgram base URL can be configured;
there is no regional fallback. Default whole-submission deadline is ten minutes.

Validated acceptance is a JSON 200 response with a UUID request ID. The immutable
identity acknowledgement uses an independent five-second persistence token, even
after host cancellation. A failed save leaves the send marker intact for recovery.
JSON provider responses/JWKS/login bodies are bounded to 64 KiB; ambiguous or invalid
responses never authorize immediate resubmission.

| Failure | Phase 3 handling |
| --- | --- |
| Validation | No upload for invalid configuration, token, recording or media header; review permanent failures |
| TransientPreparation | Bounded retry for media 404/429/5xx, transport/header timeout or identity availability |
| DefiniteRejection | Validated provider 4xx error (excluding 408); 429 is retryable, other rejection is permanent |
| UncertainAcceptance | Interrupted/timeout/cancelled upload, 408/5xx, redirect, malformed/oversized or contradictory acknowledgement; WAIT for callback recovery |
| Persistence | Never send after failed preparation/marker persistence; failed acceptance save preserves possible acceptance and must not immediately retry |

The coordinator returns these classifications; Phase 3 must apply fenced SQL dispositions,
callback waiting, budget checks and recovery. SQL timeout recovery already protects
marked sends if the process exits. Never use a validation label to bypass the SQL
definite-rejection requirement for a send that might have reached Deepgram.

Named identity/Twilio/Deepgram HTTP clients disable request loggers, redirects, cookies,
proxy use and automatic decompression. Exceptions/results use fixed safe codes without
raw response bodies, URLs, credentials or transcripts. Do not enable URI/header/body
logging or export unredacted HTTP diagnostic payloads for these endpoints; preserve
.NET's URI query redaction. The JWT itself and credential-bearing callback URL are
never persisted. Only the exact SHA-256 hash and expiry are stored.

```powershell
dotnet test apps/notification-workflow-service/NotificationWorkflowService.Tests/NotificationWorkflowService.Tests.csproj --filter FullyQualifiedName~TranscriptionHttpTests
pnpm --dir apps/notification-feedback-api exec mocha --timeout 10000 test/deepgramCallback.test.js test/twilioRecordingStatus.test.js test/twilioRecordings.test.js
```

Verified Phase 2 gate: 52 C# HTTP/JWT/streaming tests, 89 Node callback/recording tests
and seven owned-database transcription integration/concurrency tests. Coverage includes
mono/stereo preservation, missing media, invalid responses, rate limits, interrupted
uploads, timeouts/cancellation, credential redaction, exact permission matching,
insufficient expiry, send ordering and independent acceptance persistence. LocalDB
was stopped after fixture cleanup. No live identity, Twilio or Deepgram call was made;
deployment smoke tests must verify the actual permission, signing key/algorithm,
issuance lifetime, Twilio media format and provider/callback connectivity.

Research references:
- https://developers.deepgram.com/reference/manage/requests/get
- https://developers.deepgram.com/trust-security/your-data
- https://developers.deepgram.com/docs/callback
- https://developers.deepgram.com/reference/speech-to-text/listen-pre-recorded