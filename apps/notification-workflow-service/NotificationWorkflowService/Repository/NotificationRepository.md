# Notification repository

`INotificationRepository` isolates the four notification database operations:

| Method | Stored procedure |
| --- | --- |
| `ReadAccountNotificationSettingsAsync` | `ActiveAlarms_ReadAccountPushNotificationSettings` |
| `ReadReminderSettingsAsync` | `ActiveAlarms_ReadAccountPushNotificationTypes` |
| `ReadVictimSettingsAsync` | `ActiveAlarms_ReadVictimNotificationSettings` |
| `InsertNotificationHistoryAsync` | `ActiveAlarms_InsertIntoHistory` |

The implementation uses Dapper typed queries and commands, a fresh disposable connection
per operation/attempt, and the existing `ClientDatabase` configuration or connection-string
fallback. It holds no mutable per-operation state and is registered as a singleton.

Read transactions and the 600-second read timeout are retained. Procedure definitions are
not available in the workspace, so SQL parameter discovery is temporarily retained: the
legacy empty `session` input and ANSI input types are preserved. Replace discovery with
explicit verified parameter definitions once the SQL contracts are available. Discovery
itself is synchronous (SqlClient has no async API), with cancellation registered on its command.

The settings cache, not the repository, schedules failed read retries after 30 seconds.
It publishes complete snapshots only. Public static settings getters remain synchronous
compatibility adapters over the async repository.

History parameters preserve integer IDs/types and ANSI sizes (1024 for note, 30 for victim).
History writes retain a 30-second command timeout and three deadlock-only attempts with
cancellable 2-/4-second delays. Other errors propagate. Confirmed HTTP deliveries are removed
before history persistence, so a history failure cannot resend them.

HTTP, authentication, notification eligibility, and snapshot caching remain outside this
repository. Parser SQL and the original generic repository stubs are unchanged.