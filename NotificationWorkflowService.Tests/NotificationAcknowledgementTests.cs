using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;
using static ActiveAlarmsParser.Service.NotificationService.NotificationServiceData;
using Sender = ActiveAlarmsParser.Service.NotificationService.NotificationService;

namespace NotificationWorkflowService.Tests;

public class NotificationAcknowledgementTests
{
    [Fact]
    public void FullSuccessRemovesAllSubmittedNotifications()
    {
        var queue = CreateQueue("1", "2");
        var delivered = queue.ApplyAcknowledgements(
            [Ack("1", true), Ack("2", true)], queue.GetSnapshot());

        Assert.Equal(2, delivered.Count);
        Assert.Equal(0, queue.Count());
    }

    [Fact]
    public void PartialResponseRetainsOmittedNotifications()
    {
        var queue = CreateQueue("1", "2");
        var delivered = queue.ApplyAcknowledgements([Ack("1", true)], queue.GetSnapshot());

        Assert.Single(delivered);
        Assert.Equal("2", Assert.Single(queue.GetSnapshot().Values).activityid);
    }

    [Fact]
    public void MixedResponseRetainsOnlyFailedNotifications()
    {
        var queue = CreateQueue("1", "2");
        queue.ApplyAcknowledgements([Ack("1", true), Ack("2", false)], queue.GetSnapshot());

        Assert.Equal("2", Assert.Single(queue.GetSnapshot().Values).activityid);
        queue.ApplyAcknowledgements([Ack("2", true)], queue.GetSnapshot());
        Assert.Equal(0, queue.Count());
    }

    [Fact]
    public void FailureResponseDoesNotRemoveNotifications()
    {
        var queue = CreateQueue("1");
        Assert.Empty(queue.ApplyAcknowledgements([Ack("1", false)], queue.GetSnapshot()));
        Assert.Equal(1, queue.Count());
    }

    [Fact]
    public void EmptyOrNullResponseLeavesQueueUnchanged()
    {
        var queue = CreateQueue("1");
        Assert.Throws<JsonSerializationException>(() => queue.ApplyAcknowledgements([], queue.GetSnapshot()));
        Assert.Throws<JsonSerializationException>(() => queue.ApplyAcknowledgements(null, queue.GetSnapshot()));
        Assert.Equal(1, queue.Count());
    }

    [Fact]
    public void UnknownIdentityRejectsEntireResponseBeforeRemovingValidSuccess()
    {
        var queue = CreateQueue("1");
        Assert.Throws<JsonSerializationException>(() => queue.ApplyAcknowledgements(
            [Ack("1", true), Ack("unknown", true)], queue.GetSnapshot()));
        Assert.Equal(1, queue.Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateOrConflictingIdentityRejectsEntireResponse(bool secondSuccess)
    {
        var queue = CreateQueue("1");
        Assert.Throws<JsonSerializationException>(() => queue.ApplyAcknowledgements(
            [Ack("1", true), Ack("1", secondSuccess)], queue.GetSnapshot()));
        Assert.Equal(1, queue.Count());
    }

    [Fact]
    public void MissingIdentityOrNullEntryRejectsEntireResponse()
    {
        var queue = CreateQueue("1");
        var malformed = Ack("1", true);
        malformed.victimid = "";
        Assert.Throws<JsonSerializationException>(() => queue.ApplyAcknowledgements(
            [Ack("1", true), malformed], queue.GetSnapshot()));
        Assert.Throws<JsonSerializationException>(() => queue.ApplyAcknowledgements(
            [Ack("1", true), null!], queue.GetSnapshot()));
        Assert.Equal(1, queue.Count());
    }

    [Fact]
    public void AcknowledgementCannotRemoveNotificationAddedAfterSubmission()
    {
        var queue = CreateQueue("1");
        var submitted = queue.GetSnapshot();
        queue.BulkAdd(new ArrayList { Item("2") });
        Assert.Throws<JsonSerializationException>(() => queue.ApplyAcknowledgements(
            [Ack("1", true), Ack("2", true)], submitted));
        Assert.Equal(2, queue.Count());
    }

    [Fact]
    public void ExhaustedPostAttemptsRetainBatchAcrossCalls()
    {
        var queue = new NotificationArray();
        var sender = CreateIsolatedSender(queue);

        sender.PushNotification(new ArrayList { Item("1") });
        Assert.Equal(1, queue.Count());
        sender.PushNotification(new ArrayList { Item("1"), Item("2") });
        Assert.Equal(2, queue.Count());
        sender.PushNotification(new ArrayList());
        Assert.Equal(2, queue.Count());
    }

    [Fact]
    public void HandlerDoesNotReportSuccessForPartialOrEmptyResponse()
    {
        var queue = CreateQueue("1", "2");
        var sender = CreateIsolatedSender(queue);
        var handler = typeof(Sender).GetMethod("HandlePostNotificationResponse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var submitted = queue.GetSnapshot();

        // The test-only type avoids history SQL writes while exercising the production handler.
        Assert.False((bool)handler.Invoke(sender, [new NotificationServiceResponse { data = [Ack("1", true)] }, submitted])!);
        Assert.False((bool)handler.Invoke(sender, [new NotificationServiceResponse(), queue.GetSnapshot()])!);
        Assert.True((bool)handler.Invoke(sender, [new NotificationServiceResponse { data = [Ack("2", true)] }, queue.GetSnapshot()])!);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutDiscardingPendingNotifications()
    {
        var queue = CreateQueue("1");
        var sender = CreateIsolatedSender(queue);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.PostNotificationAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.PushNotificationAsync(new ArrayList { Item("2") }, cancellation.Token));
        Assert.Equal("1", Assert.Single(queue.GetSnapshot().Values).activityid);
    }

    private static NotificationArray CreateQueue(params string[] ids)
    {
        var queue = new NotificationArray();
        queue.BulkAdd(new ArrayList(ids.Select(Item).ToArray()));
        return queue;
    }

    private static Notification Item(string id) => new()
    {
        oid = "offender", victimid = "victim", type = "test", activityid = id
    };

    private static NotificationServiceDataResponse Ack(string id, bool success) => new()
    {
        oid = "offender", victimid = "victim", type = "test", activityid = id, isSuccessful = success
    };

    private static Sender CreateIsolatedSender(NotificationArray queue)
    {
        // Bypass the legacy constructor's live settings SQL initialization. An invalid URI
        // fails before HTTP/authentication, making exhausted-retry tests entirely offline.
        var sender = (Sender)RuntimeHelpers.GetUninitializedObject(typeof(Sender));
        SetField(sender, "notifications", queue);
        SetField(sender, "logger", NullLogger<Sender>.Instance);
        SetField(sender, "baseURL", "http://[");
        return sender;
    }

    private static void SetField(Sender sender, string name, object value) =>
        typeof(Sender).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, value);
}