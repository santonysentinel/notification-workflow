-- Hot path: workers looking for available jobs
CREATE INDEX IX_WorkerQueue_Ready
ON dbo.WorkerQueue
(
    AvailableAt,
    Priority DESC,
    SystemID
)
INCLUDE
(
    AttemptCount,
    MaxAttempts
)
WHERE Status = 'READY';


CREATE INDEX IX_WorkerQueue_ExpiredLease
ON dbo.WorkerQueue
(
    LockedUntil,
    QueueName,
    SystemID
)
INCLUDE
(
    LockedBy,
    LeaseToken,
    AttemptCount,
    MaxAttempts
)
WHERE Status = 'PROCESSING';