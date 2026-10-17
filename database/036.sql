SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE OR ALTER PROCEDURE [dbo].[activeAlarms_GetParticipantCurrentDetails] (
	@OID VARCHAR(20),
	@ActiveAlarmID int
	)
AS
BEGIN
	SET NOCOUNT ON;

	-- Return offenderName, lat, lon, alarmname and last_location_time

	select 
	c.T_LAT as lat, 
	c.T_LON as lon, 
	dbo.fUnixToLocal(c.T_Time, c.OTZ, 1) as last_location_time,
	h.EventDescription as alarmname
	from Client c 
	inner join ActiveAlarms aa on aa.SystemID = @ActiveAlarmID and aa.ClientID = c.SystemID 
	inner join History h on h.SystemID = aa.HistoryID
	where c.OID = @oid 


END