SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
CREATE OR ALTER VIEW dbo.CallFlowTemplatesActive
AS
    SELECT Template.SystemID, Revision.SystemID AS VersionSystemID,
        Template.FlowID, Template.Name, Revision.Version, Template.POGroupNum, Template.Platform,
        Revision.TemplateJSON, Revision.TwiML, Template.CreatedDateTime, Template.UpdatedDateTime,
        Template.DeletedDateTime, Template.CreatedBy, Template.UpdatedBy, Template.DeletedBy
    FROM dbo.CallFlowTemplates AS Template
    JOIN dbo.CallFlowTemplateVersions AS Revision ON Revision.CallFlowTemplateID = Template.SystemID
    WHERE Template.DeletedDateTime IS NULL AND Revision.DeletedDateTime IS NULL;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetAgencyCallFlow
    @pogroupnum varchar(20)
AS
BEGIN
    SET NOCOUNT ON;
    IF NULLIF(LTRIM(RTRIM(@pogroupnum)), '') IS NULL
        THROW 51003, 'PO group is required.', 1;
    SELECT SystemID, VersionSystemID, FlowID, Name, Version, POGroupNum, Platform,
        TemplateJSON, TwiML, CreatedDateTime, UpdatedDateTime, DeletedDateTime, CreatedBy, UpdatedBy, DeletedBy
    FROM dbo.CallFlowTemplatesActive
    WHERE LEFT(POGroupNum, LEN(@pogroupnum)) = @pogroupnum
    ORDER BY Name, SystemID;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetAllCallFlowsByPlatform
    @platform varchar(20)
AS
BEGIN
    SET NOCOUNT ON;
    IF NULLIF(LTRIM(RTRIM(@platform)), '') IS NULL
        THROW 51003, 'Platform is required.', 1;
    SELECT SystemID, VersionSystemID, FlowID, Name, Version, POGroupNum, Platform
    FROM dbo.CallFlowTemplatesActive WHERE Platform = @platform
    ORDER BY Name, SystemID;
END;
GO
CREATE OR ALTER PROCEDURE dbo.automatedcalls_GetAllAvailableFlowsForPOGroup
    @platform varchar(20),
    @pogroupnum varchar(20)
AS
BEGIN
    SET NOCOUNT ON;
    IF NULLIF(LTRIM(RTRIM(@platform)), '') IS NULL OR NULLIF(LTRIM(RTRIM(@pogroupnum)), '') IS NULL
        THROW 51003, 'Platform and PO group are required.', 1;
    SELECT Template.SystemID, Template.VersionSystemID, Template.FlowID, Template.Name,
        Template.Version, Template.POGroupNum, Template.Platform
    FROM dbo.CallFlowTemplatesActive AS Template
    WHERE Template.Platform = @platform AND EXISTS (
        SELECT 1 FROM dbo.CallFlowTemplatePOGroups AS Assignment
        WHERE Assignment.FlowID = Template.SystemID AND Assignment.POGroupNum = @pogroupnum
          AND Assignment.DeletedDate IS NULL)
    ORDER BY Template.Name, Template.SystemID;
END;
GO