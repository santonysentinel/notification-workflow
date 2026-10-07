CREATE OR ALTER PROCEDURE dbo.activealarms_call_GetNextSetOfJobs
    @BatchSize int,
    @WorkerId varchar(100),
    @LeaseMinutes int
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

    ;WITH JobsToClaim AS
    (
        SELECT TOP (@BatchSize)
            *
        FROM dbo.AutomatedCallQueue WITH
        (
            UPDLOCK,
            READPAST,
            ROWLOCK
        )
        WHERE Status = 'READY'
          AND AvailableAt <= @Now
          AND AttemptCount < MaxAttempts
        ORDER BY
            Priority DESC,
            AvailableAt,
            SystemID
    )
    UPDATE JobsToClaim
    SET
        Status        = 'PROCESSING',
        LockedBy      = @WorkerId,
        LockedAt      = @Now,
        LockedUntil   = DATEADD(MINUTE, @LeaseMinutes, @Now),
        LeaseToken    = NEWID(),

        AttemptCount  = AttemptCount + 1,
        LastAttemptAt = @Now,

        StartedAt     = COALESCE(StartedAt, @Now),

        UpdatedAt     = @Now
    OUTPUT
        inserted.SystemID,
        inserted.OID,
        inserted.FlowId,
        inserted.AttemptCount,
        inserted.LeaseToken,
        inserted.LockedUntil;
END;