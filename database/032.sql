GO
/****** Object:  StoredProcedure [dbo].[automatedcalls_GetAllAvailableFlowsForPOGroup]    Script Date: 10/15/2026 2:30:46 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

--[dbo].[automatedcalls_GetAllAvailableFlowsForPOGroup] '123'
ALTER  PROC [dbo].[automatedcalls_GetAllAvailableFlowsForPOGroup] (@pogroupnum VARCHAR(20))

AS

BEGIN	


	IF (@pogroupnum IS NOT NULL)

	BEGIN

		SELECT 
			cf.SystemID, cf.FlowID , 
			cf.Name, 
			cf.Version, 
			cf.POGroupNum, 
			cf.[Platform],
			cf.[TemplateJSON],cf.[TwiML],cf.[CreatedDateTime],cf.[UpdatedDateTime],cf.[DeletedDateTime],
			cf.[CreatedBy],cf.[UpdatedBy],cf.[DeletedBy]

		FROM [dbo].[CallFlowTemplates] cf
		inner join [dbo].[CallFlowTemplatePOGroups] cfp on cfp.[FlowID] = cf.SystemID

		WHERE [DeletedDateTime] is null 
		and cfp.[PoGroupNum] = @pogroupnum 
		and cfp.DeletedDate is null
		
	END

END