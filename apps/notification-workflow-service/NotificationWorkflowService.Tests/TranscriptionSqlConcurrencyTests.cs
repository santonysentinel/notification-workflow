using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.Transcription;
using Xunit;

namespace NotificationWorkflowService.Tests;

[Collection("AutoCall SQL rollout")]
public sealed class TranscriptionSqlConcurrencyTests
{
    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task ConcurrentWorkersReserveOneOutstandingSlotAndCallbacksReleaseIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenAsync();
        await first.ExecuteAsync("""
            INSERT dbo.AutomatedCallTranscriptionJobs(RecordingId,ConfigurationVersion,ConfigurationJSON)
            SELECT RecordingId,2,ConfigurationJSON FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId;
            INSERT dbo.AutomatedCallTranscriptionJobs(RecordingId,ConfigurationVersion,ConfigurationJSON)
            SELECT RecordingId,3,ConfigurationJSON FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId;
            """, new { fixture.JobId });
        var claims = await RaceAsync(
            async () => (await first.QueryAsync<Claim>(Command("dbo.transcription_ClaimJobs", new { WorkerId = "transcription-race", BatchSize = 2, MaxOutstandingSubmissions = 1 }))).ToList(),
            async () => (await second.QueryAsync<Claim>(Command("dbo.transcription_ClaimJobs", new { WorkerId = "competing-worker", BatchSize = 2, MaxOutstandingSubmissions = 1 }))).ToList());
        var winner = Assert.Single(claims.SelectMany(group => group));
        Assert.Equal(fixture.JobId, winner.SystemID);
        Assert.Equal(1, await first.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AutomatedCallTranscriptionJobs WHERE Status IN('PROCESSING','WAITING_CALLBACK')"));
        var repository = Adapter();
        string owner = await first.ExecuteScalarAsync<string>("SELECT LockedBy FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId });
        var claim = new TranscriptionClaim { SystemID = winner.SystemID, LeaseToken = winner.LeaseToken };
        var work = await repository.GetWorkAsync(claim, owner, default);
        Assert.NotNull(work);
        Assert.True(await repository.RenewAsync(claim, owner, 300, default));
        Assert.False(await repository.RenewAsync(claim, "other-owner", 300, default));
        var identity = new TranscriptionSubmissionIdentity(fixture.JobId, Guid.NewGuid(), Guid.NewGuid());
        await repository.CreateAsync(work, owner, identity, RaceCredential(), default);
        Assert.True(await repository.BeginAsync(work, owner, identity, default));
        string request = Guid.NewGuid().ToString("D");
        Assert.Equal("saved", await repository.SaveAcceptanceAsync(identity, request, default));
        Assert.Empty(await repository.ClaimAsync(2, "third-worker", 300, 1, default));
        await fixture.CompleteAsync(first, identity.CorrelationId, request);
        Assert.Single(await repository.ClaimAsync(2, "third-worker", 300, 1, default));
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task RestartRecoversMarkedOrAcceptedFinalAttemptWithoutUploadingAgain()
    {
        foreach (bool accepted in new[] { false, true })
        {
            await using var fixture = await Fixture.CreateAsync();
            await using var connection = await fixture.OpenAsync();
            await connection.ExecuteAsync("UPDATE dbo.AutomatedCallTranscriptionJobs SET MaxAttempts=1 WHERE SystemID=@JobId", new { fixture.JobId });
            var claim = await fixture.ClaimAsync(connection);
            Guid correlation = Guid.NewGuid(), identity = Guid.NewGuid();
            var submission = await fixture.CreateSubmissionAsync(connection, claim, correlation, identity, DateTime.UtcNow.AddHours(2));
            Assert.True(await fixture.BeginAsync(connection, claim, correlation, identity));
            if (accepted) await fixture.AcceptAsync(connection, correlation, identity, Guid.NewGuid().ToString());
            else await connection.ExecuteAsync("UPDATE dbo.AutomatedCallTranscriptionJobs SET LockedUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@JobId", new { fixture.JobId });
            DateTime deadline = await connection.ExecuteScalarAsync<DateTime>("SELECT CallbackDeadlineDateTime FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", submission);
            var uploads = new LifecycleSubmission((_, _, _) => throw new InvalidOperationException("A pending send cannot upload again."));
            using var worker = SqlWorker(Adapter(), uploads);
            Assert.Equal(0, await worker.RunCycleAsync(default));
            await worker.DrainAsync();
            Assert.Equal(0, uploads.Calls);
            Assert.Equal("WAITING_CALLBACK", await fixture.StatusAsync(connection));
            Assert.Equal(deadline, await connection.ExecuteScalarAsync<DateTime>("SELECT CallbackDeadlineDateTime FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", submission));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT AttemptCount FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
        }
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task RestartReplacesUnsentIdentityWithoutChangingItsAuditHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = await fixture.OpenAsync();
        var claim = await fixture.ClaimAsync(connection);
        Guid correlation = Guid.NewGuid(), identity = Guid.NewGuid();
        var original = await fixture.CreateSubmissionAsync(connection, claim, correlation, identity, DateTime.UtcNow.AddHours(2));
        await connection.ExecuteAsync("UPDATE dbo.AutomatedCallTranscriptionJobs SET LockedUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@JobId", new { fixture.JobId });
        var repository = Adapter();
        Guid replacementCorrelation = Guid.NewGuid();
        var uploads = new LifecycleSubmission(async (work, owner, token) =>
        {
            var replacement = new TranscriptionSubmissionIdentity(work.JobId, replacementCorrelation, Guid.NewGuid());
            Assert.Equal("created", (await repository.CreateAsync(work, owner, replacement, RaceCredential(), token)).Outcome);
            Assert.True(await repository.BeginAsync(work, owner, replacement, token));
            Assert.Equal("saved", await repository.SaveAcceptanceAsync(replacement, Guid.NewGuid().ToString(), default));
            return new("accepted");
        });
        using var worker = SqlWorker(repository, uploads);
        Assert.Equal(0, await worker.RunCycleAsync(default));
        Assert.Equal(0, uploads.Calls);
        Assert.Equal("TIMED_OUT", await connection.ExecuteScalarAsync<string>("SELECT Status FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", original));
        await connection.ExecuteAsync("UPDATE dbo.AutomatedCallTranscriptionJobs SET AvailableAt=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@JobId", new { fixture.JobId });
        await worker.RunCycleAsync(default);
        await worker.DrainAsync();
        Assert.Equal(1, uploads.Calls);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT AttemptCount FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
        Assert.Equal(correlation, await connection.ExecuteScalarAsync<Guid>("SELECT CorrelationId FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", original));
        Assert.Equal(identity, await connection.ExecuteScalarAsync<Guid>("SELECT SubmissionToken FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", original));
        Assert.Null(await connection.ExecuteScalarAsync<DateTime?>("SELECT SendStartedDateTime FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", original));
        Assert.Equal("WAITING_CALLBACK", await fixture.StatusAsync(connection));
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task WorkerAcceptanceAfterEarlyCallbackPreservesCompletedWinner()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = await fixture.OpenAsync();
        var repository = Adapter();
        var uploads = new LifecycleSubmission(async (work, owner, token) =>
        {
            var identity = new TranscriptionSubmissionIdentity(work.JobId, Guid.NewGuid(), Guid.NewGuid());
            await repository.CreateAsync(work, owner, identity, RaceCredential(), token);
            Assert.True(await repository.BeginAsync(work, owner, identity, token));
            string request = Guid.NewGuid().ToString();
            await fixture.CompleteAsync(connection, identity.CorrelationId, request);
            byte[] version = await connection.ExecuteScalarAsync<byte[]>("SELECT Version FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId });
            Assert.Equal("saved", await repository.SaveAcceptanceAsync(identity, request, default));
            Assert.Equal(version, await connection.ExecuteScalarAsync<byte[]>("SELECT Version FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
            return new("accepted", request);
        });
        using var worker = SqlWorker(repository, uploads);
        await worker.RunCycleAsync(default);
        await worker.DrainAsync();
        Assert.Equal(1, uploads.Calls);
        Assert.Equal("COMPLETED", await fixture.StatusAsync(connection));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AutomatedCallTranscripts WHERE RecordingId=(SELECT RecordingId FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId)", new { fixture.JobId }));
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task WorkerEmptyCompletionSpendsNoSubmissionAttempt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = await fixture.OpenAsync();
        await connection.ExecuteAsync("UPDATE dbo.AutomatedCallRecordings SET DurationSeconds=0 WHERE SystemID=(SELECT RecordingId FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId)", new { fixture.JobId });
        var uploads = new LifecycleSubmission((_, _, _) => throw new InvalidOperationException("Empty completion must not upload."));
        using var worker = SqlWorker(Adapter(), uploads);
        await worker.RunCycleAsync(default);
        await worker.DrainAsync();
        Assert.Equal(0, uploads.Calls);
        Assert.Equal("COMPLETED", await fixture.StatusAsync(connection));
        Assert.Equal("EMPTY", await connection.ExecuteScalarAsync<string>("SELECT CompletionOrigin FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT AttemptCount FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task WorkerUsesRetryOnlyForDefiniteRejectionAndWaitForUncertainSends()
    {
        foreach (var expected in new[] { (TranscriptionFailure.DefiniteRejection, true, "READY"), (TranscriptionFailure.DefiniteRejection, false, "FAILED"), (TranscriptionFailure.UncertainAcceptance, false, "WAITING_CALLBACK"), (TranscriptionFailure.Persistence, false, "WAITING_CALLBACK") })
        {
            await using var fixture = await Fixture.CreateAsync();
            await using var connection = await fixture.OpenAsync();
            var repository = Adapter();
            var uploads = new LifecycleSubmission(async (work, owner, token) =>
            {
                var identity = new TranscriptionSubmissionIdentity(work.JobId, Guid.NewGuid(), Guid.NewGuid());
                await repository.CreateAsync(work, owner, identity, RaceCredential(), token);
                Assert.True(await repository.BeginAsync(work, owner, identity, token));
                return new("submission-failed", Failure: expected.Item1, Retryable: expected.Item2);
            });
            using var worker = SqlWorker(repository, uploads);
            await worker.RunCycleAsync(default);
            await worker.DrainAsync();
            Assert.Equal(1, uploads.Calls);
            Assert.Equal(expected.Item3, await fixture.StatusAsync(connection));
        }
    }

    private static TranscriptionSubmissionRepository Adapter() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:AutoCallDB"] = Environment.GetEnvironmentVariable(AutoCallSqlConcurrencyTests.ConnectionVariable) }).Build());
    private static CallbackCredential RaceCredential() => new("transcription-race-token", DateTime.UtcNow.AddHours(2));
    private static TranscriptionWorker SqlWorker(ITranscriptionLifecycleRepository repository, ITranscriptionSubmissionService uploads)
        => new(repository, uploads, new() { Enabled = true, MaxOutstandingSubmissions = 1 }, new(), NullLogger<TranscriptionWorker>.Instance);
    private sealed class LifecycleSubmission(Func<TranscriptionWork, string, CancellationToken, Task<TranscriptionResult>> submit) : ITranscriptionSubmissionService
    {
        public int Calls { get; private set; }
        public Task<TranscriptionResult> SubmitAsync(TranscriptionWork work, string workerId, CancellationToken cancellationToken)
        { Calls++; return submit(work, workerId, cancellationToken); }
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task SubmissionAdapterPersistsExactCredentialHashAndOneShotSendIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = await fixture.OpenAsync();
        var claim = await fixture.ClaimAsync(connection);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:AutoCallDB"] = Environment.GetEnvironmentVariable(AutoCallSqlConcurrencyTests.ConnectionVariable) }).Build();
        var repository = new TranscriptionSubmissionRepository(configuration);
        var work = new TranscriptionWork { JobId = fixture.JobId, LeaseToken = claim.LeaseToken };
        var identity = new TranscriptionSubmissionIdentity(fixture.JobId, Guid.NewGuid(), Guid.NewGuid());
        var credential = new CallbackCredential("sql-adapter-test-token", DateTime.UtcNow.AddHours(2));
        var creation = await repository.CreateAsync(work, "transcription-race", identity, credential, default);
        Assert.Equal("created", creation.Outcome);
        Assert.Equal(1, creation.AttemptNumber);
        Assert.True(creation.SubmissionId > 0);
        Assert.Equal(credential.Hash, await connection.ExecuteScalarAsync<byte[]>("SELECT CallbackTokenHash FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", creation));
        Assert.Equal(identity.CorrelationId, await connection.ExecuteScalarAsync<Guid>("SELECT CorrelationId FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", creation));
        Assert.Null(await connection.ExecuteScalarAsync<DateTime?>("SELECT SendStartedDateTime FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", creation));
        Assert.Equal("duplicate", (await repository.CreateAsync(work, "transcription-race", identity, credential, default)).Outcome);
        Assert.True(await repository.BeginAsync(work, "transcription-race", identity, default));
        Assert.False(await repository.BeginAsync(work, "transcription-race", identity, default));
        string request = Guid.NewGuid().ToString("D");
        Assert.Equal("saved", await repository.SaveAcceptanceAsync(identity, request, default));
        Assert.Equal("duplicate", await repository.SaveAcceptanceAsync(identity, request, default));
        Assert.Equal("WAITING_CALLBACK", await fixture.StatusAsync(connection));
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public Task ConcurrentReadyClaimsHaveOneOwner() => VerifyClaimsAsync(false);

    [AutoCallSqlConcurrencyTests.SqlFact]
    public Task ConcurrentExpiredClaimsHaveOneReplacementOwner() => VerifyClaimsAsync(true);

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task ConcurrentCreationAndSendMarkerReplaysSpendOneAttempt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenAsync();
        var claim = await fixture.ClaimAsync(first);
        Guid correlation = Guid.NewGuid(), identity = Guid.NewGuid();
        DateTime expiry = DateTime.UtcNow.AddHours(2);
        var creations = await RaceAsync(
            () => fixture.CreateSubmissionAsync(first, claim, correlation, identity, expiry),
            () => fixture.CreateSubmissionAsync(second, claim, correlation, identity, expiry));
        Assert.Single(creations, result => result.Outcome == "created");
        Assert.Single(creations, result => result.Outcome == "duplicate");
        Assert.Equal(creations[0].SubmissionId, creations[1].SubmissionId);
        var sends = await RaceAsync(
            () => fixture.BeginAsync(first, claim, correlation, identity),
            () => fixture.BeginAsync(second, claim, correlation, identity));
        Assert.Single(sends, allowed => allowed);
        Assert.Equal(1, await first.ExecuteScalarAsync<int>("SELECT AttemptCount FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
        Assert.Equal(1, await first.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AutomatedCallTranscriptionSubmissions WHERE TranscriptionJobId=@JobId", new { fixture.JobId }));
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task ConcurrentAcceptanceAndCallbackPreserveWinner()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenAsync();
        var claim = await fixture.ClaimAsync(first);
        Guid correlation = Guid.NewGuid(), identity = Guid.NewGuid();
        var submission = await fixture.CreateSubmissionAsync(first, claim, correlation, identity, DateTime.UtcNow.AddHours(2));
        Assert.True(await fixture.BeginAsync(first, claim, correlation, identity));
        string request = Guid.NewGuid().ToString();
        await RaceAsync(
            async () => { await fixture.AcceptAsync(first, correlation, identity, request); return true; },
            async () => { await fixture.CompleteAsync(second, correlation, request); return true; });
        Assert.Equal("COMPLETED", await fixture.StatusAsync(first));
        Assert.Equal(submission.SubmissionId, await first.ExecuteScalarAsync<int>("SELECT CompletedSubmissionId FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
        Assert.Equal("COMPLETED", await first.ExecuteScalarAsync<string>("SELECT Status FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", submission));
        Assert.NotNull(await first.ExecuteScalarAsync<DateTime?>("SELECT SubmittedDateTime FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", submission));
        Assert.Equal(1, await first.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AutomatedCallTranscripts WHERE SubmissionId=@SubmissionId", submission));
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task ConcurrentTimeoutAndSuccessCannotRegressCompletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenAsync();
        var claim = await fixture.ClaimAsync(first);
        Guid correlation = Guid.NewGuid(), identity = Guid.NewGuid();
        var submission = await fixture.CreateSubmissionAsync(first, claim, correlation, identity, DateTime.UtcNow.AddHours(2));
        Assert.True(await fixture.BeginAsync(first, claim, correlation, identity));
        string request = Guid.NewGuid().ToString();
        await fixture.AcceptAsync(first, correlation, identity, request);
        await fixture.ExpireCallbackAsync(first, submission.SubmissionId);
        await RaceAsync(
            async () => { await first.QueryAsync(Command("dbo.transcription_RecoverTimeouts", new { BatchSize = 1 })); return true; },
            async () => { await fixture.CompleteAsync(second, correlation, request); return true; });
        Assert.Equal("COMPLETED", await fixture.StatusAsync(first));
        Assert.Equal("COMPLETED", await first.ExecuteScalarAsync<string>("SELECT Status FROM dbo.AutomatedCallTranscriptionSubmissions WHERE SystemID=@SubmissionId", submission));
        Assert.Equal(1, await first.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AutomatedCallTranscripts WHERE SubmissionId=@SubmissionId", submission));
    }

    [AutoCallSqlConcurrencyTests.SqlFact]
    public async Task LateAcceptanceDoesNotRegressReplacementSubmission()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = await fixture.OpenAsync();
        var claim = await fixture.ClaimAsync(connection);
        Guid oldCorrelation = Guid.NewGuid(), oldIdentity = Guid.NewGuid();
        var original = await fixture.CreateSubmissionAsync(connection, claim, oldCorrelation, oldIdentity, DateTime.UtcNow.AddHours(2));
        Assert.True(await fixture.BeginAsync(connection, claim, oldCorrelation, oldIdentity));
        await connection.ExecuteAsync("UPDATE dbo.AutomatedCallTranscriptionJobs SET LockedUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@JobId", new { fixture.JobId });
        await fixture.ExpireCallbackAsync(connection, original.SubmissionId);
        await connection.QueryAsync(Command("dbo.transcription_RecoverTimeouts", new { BatchSize = 1 }));
        await connection.ExecuteAsync("UPDATE dbo.AutomatedCallTranscriptionJobs SET AvailableAt=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@JobId", new { fixture.JobId });
        claim = await fixture.ClaimAsync(connection);
        Guid newCorrelation = Guid.NewGuid(), newIdentity = Guid.NewGuid();
        var replacement = await fixture.CreateSubmissionAsync(connection, claim, newCorrelation, newIdentity, DateTime.UtcNow.AddHours(2));
        byte[] version = await connection.ExecuteScalarAsync<byte[]>("SELECT Version FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId });
        string request = Guid.NewGuid().ToString();
        Assert.Equal("saved", await fixture.AcceptAsync(connection, oldCorrelation, oldIdentity, request));
        Assert.Equal(version, await connection.ExecuteScalarAsync<byte[]>("SELECT Version FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
        Assert.Equal(replacement.SubmissionId, await connection.ExecuteScalarAsync<int>("SELECT CurrentSubmissionId FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
        Assert.True(await fixture.BeginAsync(connection, claim, newCorrelation, newIdentity));
        await fixture.CompleteAsync(connection, oldCorrelation, request);
        Assert.Equal(original.SubmissionId, await connection.ExecuteScalarAsync<int>("SELECT CompletedSubmissionId FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
        Assert.False(await fixture.BeginAsync(connection, claim, newCorrelation, newIdentity));
    }

    private static async Task VerifyClaimsAsync(bool expired)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenAsync();
        Guid original = Guid.NewGuid();
        if (expired)
            await first.ExecuteAsync("""
                UPDATE dbo.AutomatedCallTranscriptionJobs SET Status='PROCESSING',LockedBy='old-owner',
                    LeaseToken=@original,LockedUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE SystemID=@JobId;
                """, new { original, fixture.JobId });
        var claims = await RaceAsync(
            async () => (await first.QueryAsync<Claim>(Command("dbo.transcription_ClaimJobs", new { WorkerId = "race-first", BatchSize = 1, LeaseSeconds = 300 }))).ToList(),
            async () => (await second.QueryAsync<Claim>(Command("dbo.transcription_ClaimJobs", new { WorkerId = "race-second", BatchSize = 1, LeaseSeconds = 300 }))).ToList());
        var winner = Assert.Single(claims.SelectMany(result => result));
        Assert.Equal(fixture.JobId, winner.SystemID);
        Assert.NotEqual(original, winner.LeaseToken);
        Assert.Equal(0, winner.AttemptCount);
        Assert.Equal(winner.LeaseToken, await first.ExecuteScalarAsync<Guid>("SELECT LeaseToken FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { fixture.JobId }));
    }

    private static async Task<T[]> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<T> RunAsync(Func<Task<T>> action) { await start.Task; return await action(); }
        var tasks = new[] { RunAsync(first), RunAsync(second) };
        start.SetResult();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static CommandDefinition Command(string procedure, object parameters) => new(procedure, parameters, commandType: CommandType.StoredProcedure, commandTimeout: 10);

    private sealed class Claim
    {
        public int SystemID { get; set; }
        public int AttemptCount { get; set; }
        public Guid LeaseToken { get; set; }
    }

    private sealed class Creation
    {
        public string Outcome { get; set; } = "";
        public int SubmissionId { get; set; }
    }

    private sealed class Fixture(string connectionString) : IAsyncDisposable
    {
        private static readonly byte[] TokenHash = System.Security.Cryptography.SHA256.HashData("transcription-race-token"u8);
        private static readonly byte[] PayloadHash = System.Security.Cryptography.SHA256.HashData("transcription-race-payload"u8);
        private int CallId { get; set; }
        private int RecordingId { get; set; }
        public int JobId { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            string connectionString = Environment.GetEnvironmentVariable(AutoCallSqlConcurrencyTests.ConnectionVariable)
                ?? throw new InvalidOperationException("An owned scratch SQL connection is required.");
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (builder.InitialCatalog != "NotificationWorkflowVoiceTests" ||
                !builder.DataSource.Equals(@"(localdb)\NotificationWorkflowVoiceTests", StringComparison.OrdinalIgnoreCase) || !builder.IntegratedSecurity)
                throw new InvalidOperationException("Only the owned LocalDB test instance/database is permitted.");
            var fixture = new Fixture(connectionString);
            try
            {
                await using var connection = await fixture.OpenAsync();
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AutomatedCallTranscriptionJobs WHERE Status IN('READY','PROCESSING','WAITING_CALLBACK')"));
                fixture.CallId = await connection.QuerySingleAsync<int>("INSERT dbo.AutomatedCalls(Provider,providerCallId,CallStatus) OUTPUT inserted.SystemID VALUES('Twilio',@sid,'completed')", new { sid = "CA" + Guid.NewGuid().ToString("N") });
                fixture.RecordingId = await connection.QuerySingleAsync<int>("""
                    INSERT dbo.AutomatedCallRecordings(CallId,Provider,AccountSid,RecordingSid,RecordingStatus,DurationSeconds,Channels)
                    OUTPUT inserted.SystemID VALUES(@CallId,'Twilio','ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',@sid,'completed',10,1)
                    """, new { fixture.CallId, sid = "RE" + Guid.NewGuid().ToString("N") });
                fixture.JobId = await connection.QuerySingleAsync<int>("INSERT dbo.AutomatedCallTranscriptionJobs(RecordingId,ConfigurationVersion,ConfigurationJSON) OUTPUT inserted.SystemID VALUES(@RecordingId,1,N'{\"model\":\"nova-3\",\"utterances\":true}')", new { fixture.RecordingId });
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task<SqlConnection> OpenAsync()
        {
            var connection = new SqlConnection(connectionString);
            try { await connection.OpenAsync().WaitAsync(TimeSpan.FromSeconds(10)); return connection; }
            catch { await connection.DisposeAsync(); throw; }
        }

        public Task<Claim> ClaimAsync(SqlConnection connection) => connection.QuerySingleAsync<Claim>(Command("dbo.transcription_ClaimJobs", new { WorkerId = "transcription-race", BatchSize = 1, LeaseSeconds = 300 }));
        public Task<Creation> CreateSubmissionAsync(SqlConnection connection, Claim claim, Guid correlation, Guid identity, DateTime expiry) => connection.QuerySingleAsync<Creation>(Command("dbo.transcription_CreateSubmission",
            new { JobId, WorkerId = "transcription-race", claim.LeaseToken, CorrelationId = correlation, SubmissionToken = identity, CallbackTokenHash = TokenHash, CallbackTokenExpiresAt = expiry }));
        public Task<bool> BeginAsync(SqlConnection connection, Claim claim, Guid correlation, Guid identity) => connection.QuerySingleAsync<bool>(Command("dbo.transcription_BeginSubmission",
            new { JobId, WorkerId = "transcription-race", claim.LeaseToken, CorrelationId = correlation, SubmissionToken = identity }));
        public Task<string> AcceptAsync(SqlConnection connection, Guid correlation, Guid identity, string request) => connection.QuerySingleAsync<string>(Command("dbo.transcription_SaveAcceptance",
            new { JobId, CorrelationId = correlation, SubmissionToken = identity, RequestId = request }));
        public Task CompleteAsync(SqlConnection connection, Guid correlation, string request) => connection.QueryAsync(Command("dbo.transcription_CompleteCallback",
            new { CorrelationId = correlation, RecordingId, TokenHash, RequestId = request, PayloadHash, Success = true, ResponseJSON = "{}",
                TranscriptsJSON = "[{\"sentence\":0,\"channel\":0,\"startTimeMilliseconds\":0,\"endTimeMilliseconds\":1000,\"transcriptText\":\"Hello\"}]" }));
        public Task<string> StatusAsync(SqlConnection connection) => connection.ExecuteScalarAsync<string>("SELECT Status FROM dbo.AutomatedCallTranscriptionJobs WHERE SystemID=@JobId", new { JobId });
        public Task ExpireCallbackAsync(SqlConnection connection, int submissionId) => connection.ExecuteAsync("""
            UPDATE dbo.AutomatedCallTranscriptionSubmissions SET CreatedDateTime=DATEADD(hour,-2,SYSUTCDATETIME()),
                CallbackDeadlineDateTime=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE SystemID=@submissionId;
            UPDATE dbo.AutomatedCallTranscriptionJobs SET AvailableAt=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE SystemID=@JobId;
            """, new { submissionId, JobId });

        public async ValueTask DisposeAsync()
        {
            await using var connection = await OpenAsync();
            await connection.ExecuteAsync("""
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                UPDATE dbo.AutomatedCallTranscriptionJobs SET Status='FAILED',CurrentSubmissionId=NULL,
                    CompletedSubmissionId=NULL,CompletedDateTime=NULL,CompletionOrigin=NULL WHERE RecordingId=@RecordingId;
                DELETE dbo.AutomatedCallTranscripts WHERE RecordingId=@RecordingId;
                DELETE dbo.AutomatedCallTranscriptionSubmissions WHERE RecordingId=@RecordingId;
                DELETE dbo.AutomatedCallTranscriptionJobs WHERE RecordingId=@RecordingId;
                DELETE dbo.AutomatedCallRecordings WHERE CallId=@CallId;
                DELETE dbo.AutomatedCalls WHERE SystemID=@CallId;
                COMMIT;
                """, new { RecordingId, CallId }, commandTimeout: 10);
        }
    }
}