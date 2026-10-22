SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
-- Tenant database: deploy in every routed tenant before restarting the AutoCall worker.
-- Returns one row per participant phone with its device type (HomePhone, CellPhone, WorkPhone)
-- so a Robo Call can dial the device chosen in OptionsJSON.participantRoboCall.
CREATE OR ALTER PROCEDURE [dbo].[activealarms_GetAllParticipantPhonenumbers]
    @oid varchar(20)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT p.phone_type, p.phone_number
    FROM Client AS c
    CROSS APPLY (
        VALUES
            (1, 'HomePhone', ISNULL(c.OHCountryCode, '')
              + ISNULL(c.OPhoneAreaCode, '')
              + ISNULL(c.OPhoneNum, '')),

            (2, 'CellPhone', ISNULL(c.OCellCountryCode, '')
              + ISNULL(c.OAreacode, '')
              + ISNULL(c.OCellNum2, '')),

            (3, 'WorkPhone', ISNULL(c.OWCountryCode, '')
              + ISNULL(c.OWorkPhoneAreaCode, '')
              + ISNULL(c.OWorkPhone, ''))
    ) AS p(sort_order, phone_type, phone_number)
    WHERE c.OID = @oid
      AND c.Deleted IS NULL
      AND NULLIF(LTRIM(RTRIM(p.phone_number)), '') IS NOT NULL
    ORDER BY p.sort_order;
END;
GO