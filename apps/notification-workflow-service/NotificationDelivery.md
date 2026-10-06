# Notification delivery policy

`PostNotificationAsync` owns the only automatic delivery retry loop. `PushNotificationAsync`
and the synchronous compatibility methods do not add another retry layer. Named HTTP clients
must not be configured with an additional POST retry handler.

## Attempts and delays

- At most three notification POST attempts per call, including any resend after HTTP 401.
- HTTP 429 is treated as a rejected/throttled request. Retry with exponential backoff
  (1 then 2 seconds) plus 0–1 second jitter. Verify this rejection behavior with the API.
- `Retry-After` delta-seconds and HTTP-date forms are honored as minimum delays.
- Delays above 30 seconds defer delivery instead of retrying early. A cooldown also applies
  to subsequent calls after retry exhaustion. Requests have a 30-second HTTP timeout.
- One 401 may invalidate the rejected cached token and cause one reauthentication.
  A second 401 stops. The refresh shares the same three-POST budget.
- Permanent errors, unexpected success status codes, malformed/partial acknowledgements,
  and application-level failures are not automatically retried.
- Caller cancellation interrupts HTTP/backoff and propagates to the caller.

## Idempotency must be confirmed externally

`NotificationDelivery:IdempotencyConfirmed` defaults to `false` in appsettings.
Only set it to `true` after verifying that the remote API deduplicates repeated notification
identities/payloads, including across timeouts, partial delivery, and subsequent calls.
This flag does not implement remote deduplication or add an invented idempotency header.

When confirmed, network failures, HTTP timeouts, and HTTP 408/500/502/503/504 may be retried
within the same bounded policy. Without confirmation, these outcomes need review because
the remote service might already have delivered the notification.

## Retention and review

Unacknowledged notifications remain in memory. A permanent rejection or ambiguous failure
sets `RequiresDeliveryReview` and pauses automatic sending, including later parser batches.
Inspect/reconcile the remote outcome and correct any rejected payload before explicitly
calling `ResumePendingDeliveryAfterReview`. Resuming without reconciliation can duplicate
deliveries. New notifications accumulate while the sender is paused.

This is an in-memory safety mechanism, not a durable outbox or an operator UI. Restarting
the process loses both pending notifications and cooldown/review state. Confirmed deliveries
are removed before history persistence, so history-write failures do not cause resends.