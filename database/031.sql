GO
/****** Object:  StoredProcedure [dbo].[automatedcalls_GetAllCallFlowsByPlatform]    Script Date: 10/15/2026 2:29:50 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

ALTER  PROC [dbo].[automatedcalls_GetAllCallFlowsByPlatform] (@platform VARCHAR(20))

AS

BEGIN

	


	IF (@platform IS NOT NULL)

	BEGIN

		SELECT 
			SystemID,
			FlowID , 
			Name, 
			Version, 
			POGroupNum, 
			[Platform],
			[TemplateJSON],[TwiML],[CreatedDateTime],[UpdatedDateTime],[DeletedDateTime]
			[CreatedBy],[UpdatedBy],[DeletedBy]

		FROM [dbo].[CallFlowTemplates]

		WHERE [DeletedDateTime] is null 
		and [Platform] = @platform 
		
	END

END