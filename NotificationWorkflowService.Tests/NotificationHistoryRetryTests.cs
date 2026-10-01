using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Repository;
using Xunit;
using static ActiveAlarmsParser.Service.NotificationService.NotificationServiceData;
using Sender = ActiveAlarmsParser.Service.NotificationService.NotificationService;

namespace NotificationWorkflowService.Tests;

public class NotificationHistoryRetryTests
{
    [Fact]
    public void SuccessExecutesOnceWithoutDelay()
    {
        int attempts = 0;
        ExecuteHistoryWithRetry(() => attempts++, _ => Assert.Fail("Unexpected delay"));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void DeadlockCanRecoverOnThirdAttempt()
    {
        int attempts = 0;
        var delays = new List<TimeSpan>();
        ExecuteHistoryWithRetry(() =>
        {
            if (++attempts < 3) throw CreateSqlException(1205);
        }, delays.Add);
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, delays);
    }

    [Fact]
    public void PersistentDeadlockPropagatesAfterThreeAttempts()
    {
        int attempts = 0;
        int delays = 0;
        var failure = CreateSqlException(1205);
        var thrown = Assert.Throws<SqlException>(() => ExecuteHistoryWithRetry(() =>
        {
            attempts++;
            throw failure;
        }, _ => delays++));
        Assert.Same(failure, thrown);
        Assert.Equal(3, attempts);
        Assert.Equal(2, delays);
    }

    [Theory]
    [InlineData(2627)] // Duplicate key: permanent failure.
    [InlineData(18456)] // Login failure.
    [InlineData(-2)] // Timeout: insert outcome may be unknown.
    public void NonDeadlockSqlFailurePropagatesImmediately(int number)
    {
        int attempts = 0;
        // Intentionally mentions deadlock: classification must use Number, not message text.
        var failure = CreateSqlException(number);
        var thrown = Assert.Throws<SqlException>(() => ExecuteHistoryWithRetry(() =>
        {
            attempts++;
            throw failure;
        }, _ => Assert.Fail("Unexpected delay")));
        Assert.Equal(number, failure.Number);
        Assert.Same(failure, thrown);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void NonSqlFailurePropagatesImmediately()
    {
        int attempts = 0;
        var failure = new InvalidOperationException("failure");
        var thrown = Assert.Throws<InvalidOperationException>(() => ExecuteHistoryWithRetry(() =>
        {
            attempts++;
            throw failure;
        }, _ => Assert.Fail("Unexpected delay")));
        Assert.Same(failure, thrown);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void HistoryFailureDoesNotRequeueDeliveryOrStopOtherAcknowledgements()
    {
        var queue = new NotificationArray();
        queue.BulkAdd(new ArrayList
        {
            new Notification { oid = "o", victimid = "v", type = "push", activityid = "1-platform-v" },
            new Notification { oid = "o", victimid = "v", type = "reminder", activityid = "2-platform-v" }
        });
        // Avoid constructor initialization; history writes use a failing fake repository.
        var sender = (Sender)RuntimeHelpers.GetUninitializedObject(typeof(Sender));
        SetField(sender, "notifications", queue);
        SetField(sender, "logger", NullLogger<Sender>.Instance);
        var repository = new FakeNotificationRepository { HistoryFailure = new InvalidOperationException("failed history") };
        SetField(sender, "repository", repository);
        var response = new NotificationServiceResponse
        {
            data =
            [
                new() { oid = "o", victimid = "v", type = "push", activityid = "1-platform-v", isSuccessful = true },
                new() { oid = "o", victimid = "v", type = "reminder", activityid = "2-platform-v", isSuccessful = true }
            ]
        };
        var handler = typeof(Sender).GetMethod("HandlePostNotificationResponse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)handler.Invoke(sender, [response, queue.GetSnapshot()])!);
        Assert.Equal(0, queue.Count());
        Assert.Equal(2, repository.History.Count);
    }

    private static void ExecuteHistoryWithRetry(Action execute, Action<TimeSpan> delay)
        => NotificationRepository.ExecuteHistoryWithRetryAsync(_ =>
        {
            execute();
            return Task.CompletedTask;
        }, NullLogger.Instance, default, (duration, _) => { delay(duration); return Task.CompletedTask; }).GetAwaiter().GetResult();

    private static SqlException CreateSqlException(int number)
    {
        // SqlClient doesn't expose exception constructors; create realistic errors without SQL I/O.
        var constructor = typeof(SqlError).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 8 && c.GetParameters()[7].ParameterType == typeof(Exception));
        var error = (SqlError)constructor.Invoke([number, (byte)0, (byte)16, "server", "deadlock test message", "procedure", 1, null]);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(errors, [error]);
        var factory = typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .First(m => m.Name == "CreateException" && m.GetParameters().Length == 2);
        return (SqlException)factory.Invoke(null, [errors, "16.0"])!;
    }

    private static void SetField(Sender sender, string name, object value) =>
        typeof(Sender).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, value);
}