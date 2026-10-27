SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE OR ALTER PROCEDURE dbo.activealarms_ResolveRoboCallOfficer
    @oid         varchar(20),
    @OfficerType varchar(20),  -- Primary | RoleBased
    @RoleID      int = NULL,
    @RolePOGroup varchar(20) = NULL
AS
BEGIN
    /*
	    Returns exactly one row:

        Column	Meaning
        officer_id	Stable officer identifier using the actual database key type
        officer_name	Display name for history notes and reporting
    
    */

    if(@OfficerType = 'Primary')
    begin

        select a.SystemID as officer_id, a.POLname + ', ' + a.POFname as officer_name from Account a 
        inner join Client c on c.POAcc = a.SystemID and c.OID = @oid

    end
    else if(@OfficerType = 'RoleBased')
    begin

        select top 1 a.SystemID as officer_id, a.POLname + ', ' + a.POFname as officer_name 
        from Account a 
        inner join Client c on c.OID = @oid
        inner join POGroupRoles pr on pr.POGroup = @RolePOGroup
        inner join Roles r on r.SystemID = pr.RoleID and pr.RoleID = @RoleID and a.SystemID = pr.UserSysID

    end
END
GO
