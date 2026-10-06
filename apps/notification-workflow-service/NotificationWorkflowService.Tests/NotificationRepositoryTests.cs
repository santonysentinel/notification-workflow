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
    public async Task SettingsInitializeFromFakeRepositoryAndDoNotReloadImmediately()
    {
        var repository = new FakeNotificationRepository();
        var config = new ConfigurationBuilder().Build();
        NotificationServiceSetting.Initialize(config, NullLogger.Instance, repository);
        Assert.Equal(0, repository.AccountReads);
        Assert.Equal(0, repository.ReminderReads);
        Assert.Equal(0, repository.VictimReads);

        using var cancellation = new CancellationTokenSource();
        var account = await NotificationServiceSetting.GetNotificationServiceSettingAsync("v", "e", cancellation.Token);
        Assert.Equal("token", account!.PushNotificationToken);
        Assert.Equal(0, repository.ReminderReads);
        Assert.Equal(0, repository.VictimReads);
        var reminder = await NotificationServiceSetting.GetAppReminderSettingAsync("e", cancellation.Token);
        Assert.Equal(0, repository.VictimReads);
        Assert.True(await NotificationServiceSetting.CheckVictimReminderSettingAsync("o", reminder, cancellation.Token));
        Assert.Equal(cancellation.Token, repository.AccountReadToken);
        Assert.Equal(cancellation.Token, repository.ReminderReadToken);
        Assert.Equal(cancellation.Token, repository.VictimReadToken);

        Assert.Same(account, await NotificationServiceSetting.GetNotificationServiceSettingAsync("v", "e"));
        Assert.Same(reminder, await NotificationServiceSetting.GetAppReminderSettingAsync("e"));
        Assert.True(await NotificationServiceSetting.CheckVictimReminderSettingAsync("o", reminder));
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