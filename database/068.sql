
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE OR ALTER PROCEDURE dbo.activealarms_GetAllOfficerPhonenumbers
    @OfficerID int
AS
BEGIN
    /*
	    Returns the same two-column format as participant phone lookup:

        phone_type	phone_number
        CellPhone	+15555550100        
    
    */

    SELECT 
    v.phone_type, 
    v.phone_number
    FROM Account 
    CROSS APPLY (
        VALUES 
            ('CellPhone', ISNULL(CellPhone, '')),
            ('OfficePhone', ISNULL(OfficePhone, ''))
    ) v (phone_type, phone_number)
    WHERE SystemID = @OfficerID;


END
GO
