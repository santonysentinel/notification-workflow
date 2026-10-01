using System.Data;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Repository;
using ActiveAlarmsParser.Service.NotificationService;
using Xunit;

namespace NotificationWorkflowService.Tests;

public class NotificationRepositoryTests
{
    [Fact]
    public void SettingsInitializeFromFakeRepositoryAndDoNotReloadImmediately()
    {
        var repository = new FakeNotificationRepository();
        var config = new ConfigurationBuilder().Build();
        NotificationServiceSetting.Initialize(config, NullLogger.Instance, repository);
        Assert.Equal("token", NotificationServiceSetting.GetNotificationServiceSetting("v", "e")!.PushNotificationToken);
        var reminder = NotificationServiceSetting.GetAppReminderSetting("e");
        Assert.True(NotificationServiceSetting.CheckVictimReminderSetting("o", reminder));
        Assert.Equal(1, repository.AccountReads);
        Assert.Equal(1, repository.ReminderReads);
        Assert.Equal(1, repository.VictimReads);
    }

    [Fact]
    public void HistoryCommandPreservesProcedureParametersAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var command = NotificationRepository.CreateHistoryCommand(123, "v", "note", 2, cancellation.Token);
        Assert.Equal("ActiveAlarms_InsertIntoHistory", command.CommandText);
        Assert.Equal(CommandType.StoredProcedure, command.CommandType);
        Assert.Equal(30, command.CommandTimeout);
        Assert.Equal(cancellation.Token, command.CancellationToken);
        var parameters = Assert.IsType<DynamicParameters>(command.Parameters);
        Assert.Equal(123, parameters.Get<int>("HistoryID"));
        Assert.Equal("v", parameters.Get<string>("victimid"));
        Assert.Equal("note", parameters.Get<string>("emails"));
        Assert.Equal(2, parameters.Get<int>("type"));
    }

    [Fact]
    public async Task RepositoryRetryBackoffCanBeCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NotificationRepository.ExecuteHistoryWithRetryAsync(
            _ => throw new InvalidOperationException("must not execute"), NullLogger.Instance, cancellation.Token));
    }

    [Fact]
    public void RepositoryRequiresConfiguredClientDatabase()
    {
        Assert.Throws<InvalidOperationException>(() => new NotificationRepository(new ConfigurationBuilder().Build(), NullLogger<NotificationRepository>.Instance));
    }
}