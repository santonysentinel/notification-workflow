SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

ALTER PROCEDURE dbo.dash_UpdateProfileSteps
    @session varchar(32),
    @ProfileID int = NULL,
    @ProfileName varchar(100),
    @doc xml
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @potz varchar(5),
        @poname varchar(100),
        @pogroupnum varchar(10),
        @WebAccessRight int;

    SELECT @potz = UserTZ,
        @poname = UserID,
        @pogroupnum = UserPOGroup,
        @WebAccessRight = WebAccessRight
    FROM dbo.dash_validateUser(@session);

    IF @potz IS NOT NULL AND @WebAccessRight != 0
    BEGIN
        DECLARE @EventCode varchar(10);
        DECLARE @DOW smallint;
        DECLARE @ACT smallint;
        DECLARE @Instruction varchar(1500);
        DECLARE @EmailAddress varchar(640);
        DECLARE @EmailJoin tinyint;
        DECLARE @ProfileType int;
        DECLARE @TimeIntervalID int;
        DECLARE @NextState varchar(10);
        DECLARE @StateNo int;
        DECLARE @StateName varchar(150);
        DECLARE @NextStateName varchar(150);
        DECLARE @LoopStartState varchar(10);
        DECLARE @NumberOfLoops varchar(10);
        DECLARE @FeedBackRequired bit;
        DECLARE @StateTime int;
        DECLARE @RoleID int;
        DECLARE @RoleAction int;
        DECLARE @FlowId int;

        IF @ProfileID IS NULL
        BEGIN
            INSERT INTO Profiles (
                ProfileName, CreatedBy, ModifiedBy, CreatedTime, ModifiedTime, DeletedTime
            )
            VALUES (
                RTRIM(@ProfileName), @poname, @poname, GETUTCDATE(), GETUTCDATE(), NULL
            );

            SELECT @ProfileID = SCOPE_IDENTITY();

            INSERT INTO POGroupNumProfiles (
                ProfileID, POGroupNum, CreatedBy, ModifiedBy, CreatedTime, DeletedTime
            )
            VALUES (
                @ProfileID, @pogroupnum, @poname, NULL, GETUTCDATE(), NULL
            );
        END
        ELSE
        BEGIN
            UPDATE Profiles
            SET ProfileName = @ProfileName,
                ModifiedBy = @poname,
                ModifiedTime = GETUTCDATE()
            WHERE ProfileID = @ProfileID;

            UPDATE ProfileItems
            SET DeletedDate = GETUTCDATE()
            WHERE ProfileID = @ProfileID
                AND DeletedDate IS NULL;
        END;

        DECLARE @XMLDoc int;
        EXEC sp_xml_preparedocument @XMLDoc OUTPUT, @doc;

        DECLARE myCursor CURSOR FOR
        SELECT EventCode,
            ProfileType,
            DOW,
            ACT,
            INSTRUCTION,
            EMAILADDRESS,
            EmailJoin,
            TimeInterval,
            FeedBackRequired,
            StateName,
            NULLIF(NextState, ''),
            StateTime,
            StateNo,
            NULLIF(NextStateName, ''),
            NULLIF(LoopStartState, ''),
            NULLIF(NumberOfLoops, ''),
            NULLIF(RoleID, ''),
            NULLIF(RoleAction, ''),
            FlowId
        FROM OPENXML(@XMLDoc, '/ROOT/Item', 1) WITH (
            EventCode varchar(10),
            ProfileType int,
            DOW int,
            ACT int,
            INSTRUCTION varchar(1500),
            EMAILADDRESS varchar(640),
            EmailJoin int,
            TimeInterval int,
            FeedBackRequired bit,
            StateName varchar(150),
            NextState varchar(10),
            StateTime int,
            StateNo int,
            NextStateName varchar(150),
            LoopStartState varchar(10),
            NumberOfLoops varchar(10),
            RoleID int,
            RoleAction int,
            FlowId int '@FlowId'
        );

        OPEN myCursor;

        FETCH NEXT FROM myCursor INTO
            @EventCode, @ProfileType, @DOW, @ACT, @Instruction,
            @EmailAddress, @EmailJoin, @TimeIntervalID, @FeedBackRequired,
            @StateName, @NextState, @StateTime, @StateNo, @NextStateName,
            @LoopStartState, @NumberOfLoops, @RoleID, @RoleAction, @FlowId;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            EXEC dbo.dash_UpdateProfileItemSteps
                @ProfileID,
                @EventCode,
                @ProfileType,
                @DOW,
                @ACT,
                @Instruction,
                @poname,
                @EmailAddress,
                @EmailJoin,
                @StateNo,
                @StateTime,
                NULL,
                NULL,
                @FeedBackRequired,
                @TimeIntervalID,
                @NextState,
                @StateName,
                @NextStateName,
                @LoopStartState,
                @NumberOfLoops,
                @RoleID,
                @RoleAction,
                @FlowId;

            FETCH NEXT FROM myCursor INTO
                @EventCode, @ProfileType, @DOW, @ACT, @Instruction,
                @EmailAddress, @EmailJoin, @TimeIntervalID, @FeedBackRequired,
                @StateName, @NextState, @StateTime, @StateNo, @NextStateName,
                @LoopStartState, @NumberOfLoops, @RoleID, @RoleAction, @FlowId;
        END;

        IF @ProfileID IS NOT NULL
        BEGIN
            DECLARE @Version int;

            SELECT @Version = COALESCE(MAX(VersionHistory) + 1, 1)
            FROM ProfileItems
            WHERE ProfileID = @ProfileID;

            UPDATE ProfileItems
            SET VersionHistory = @Version
            WHERE ProfileID = @ProfileID
                AND DeletedDate IS NULL;
        END;

        CLOSE myCursor;
        DEALLOCATE myCursor;
        EXEC sp_xml_removedocument @XMLDoc;

        SELECT 1 AS success, '' AS msg;
    END
    ELSE
    BEGIN
        SELECT 0 AS success, 'Session Invalid' AS msg;
    END;
END;
GO