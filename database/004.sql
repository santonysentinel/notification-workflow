CREATE OR ALTER PROCEDURE dbo.activealarms_call_InsertJob
	@ActiveAlarmID int,
	@OID varchar(20) = NULL,
	@FlowId int = NULL,
	@CallStatus int = NULL,
	@Priority int = 0,
	@AvailableAt datetime2(3) = NULL,
	@MaxAttempts int = 10
AS
BEGIN
	SET NOCOUNT ON;

	INSERT INTO dbo.AutomatedCallQueue
	(
		ActiveAlarmID,
		OID,
		FlowId,
		CallStatus,
		Priority,
		AvailableAt,
		MaxAttempts
	)
	OUTPUT inserted.SystemID
	VALUES
	(
		@ActiveAlarmID,
		@OID,
		@FlowId,
		@CallStatus,
		@Priority,
		COALESCE(@AvailableAt, SYSUTCDATETIME()),
		@MaxAttempts
	);
END;
