CREATE OR ALTER PROCEDURE [dbo].[activealarms_GetAllParticipantPhonenumbers] 
@oid varchar(20)
AS
BEGIN

	SELECT p.phone_numbers
    FROM Client AS c
    CROSS APPLY (
        VALUES
            (1, ISNULL(c.OHCountryCode, '')
              + ISNULL(c.OPhoneAreaCode, '')
              + ISNULL(c.OPhoneNum, '')),

            (2, ISNULL(c.OCellCountryCode, '')
              + ISNULL(c.OAreacode, '')
              + ISNULL(c.OCellNum2, '')),

            (3, ISNULL(c.OWCountryCode, '')
              + ISNULL(c.OWorkPhoneAreaCode, '')
              + ISNULL(c.OWorkPhone, ''))
    ) AS p(sort_order, phone_numbers)
    WHERE c.OID = @oid
      AND c.Deleted IS NULL
      AND NULLIF(LTRIM(RTRIM(p.phone_numbers)), '') IS NOT NULL
    ORDER BY p.sort_order;
END