SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE  PROC [dbo].[automatedcalls_GetAgencyCallFlow] (@pogroupnum VARCHAR(20))

AS

BEGIN

	


	IF (@pogroupnum IS NOT NULL)

	BEGIN

		SELECT 
			FlowID , 
			Name, 
			Version, 
			POGroupNum, 
			[Platform],
			[TemplateJSON],[TwiML],[CreatedDateTime],[UpdatedDateTime],[DeletedDateTime]
			[CreatedBy],[UpdatedBy],[DeletedBy]

		FROM [dbo].[CallFlowTemplates]

		WHERE [DeletedDateTime] is null 
		and( [POGroupNum] = @pogroupnum 
		OR (
		
			POGroupNum like @pogroupnum + '%'
		))
	END

END