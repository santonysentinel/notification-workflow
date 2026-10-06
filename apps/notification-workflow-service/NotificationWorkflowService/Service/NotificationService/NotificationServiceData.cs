using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace ActiveAlarmsParser.Service.NotificationService
{
    public class VictimAppAlarmText
    {
        public string? AlarmID { get; set; }
        public string? AlarmName { get; set; }
        public string? Text { get; set; }
    }

    public class NotificationServiceData
    {
        public class NotificationArray
        {
            private Dictionary<string, Notification> notifications;

            public NotificationArray()
            {
                notifications = new Dictionary<string, Notification>();
            }

            public void BulkAdd(ArrayList list)
            {
                ArgumentNullException.ThrowIfNull(list);

                // Validate the entire batch before mutation so an invalid later item
                // cannot leave a partially queued batch. Identity strings are required
                // at runtime; DTO defaults remain null to preserve JSON serialization.
                foreach (object? item in list)
                {
                    if (item is not Notification notification ||
                        string.IsNullOrWhiteSpace(notification.oid) ||
                        string.IsNullOrWhiteSpace(notification.victimid) ||
                        string.IsNullOrWhiteSpace(notification.type) ||
                        string.IsNullOrWhiteSpace(notification.activityid))
                    {
                        throw new ArgumentException("Every notification must contain a complete identity.", nameof(list));
                    }
                }

                foreach (Notification i in list)
                {
                    string key = i.oid + "#" + i.victimid + "#" + i.type + "#" + i.activityid;
                    if (!notifications.ContainsKey(key))
                    {
                        notifications.Add(key, i);
                    }
                }
            }

            public void RemoveWithKey(string key)
            {
                if (notifications.ContainsKey(key))
                {
                    notifications.Remove(key);
                }
            }

            internal IReadOnlyDictionary<string, Notification> GetSnapshot()
            {
                return new Dictionary<string, Notification>(notifications);
            }

            internal IReadOnlyList<Notification> ApplyAcknowledgements(
                IReadOnlyList<NotificationServiceDataResponse>? acknowledgements,
                IReadOnlyDictionary<string, Notification> submitted)
            {
                if (acknowledgements == null || acknowledgements.Count == 0)
                {
                    throw new JsonSerializationException("The notification response must contain acknowledgements.");
                }

                var seen = new HashSet<string>();
                var delivered = new List<KeyValuePair<string, Notification>>();
                foreach (var acknowledgement in acknowledgements)
                {
                    if (acknowledgement == null ||
                        string.IsNullOrWhiteSpace(acknowledgement.oid) ||
                        string.IsNullOrWhiteSpace(acknowledgement.victimid) ||
                        string.IsNullOrWhiteSpace(acknowledgement.type) ||
                        string.IsNullOrWhiteSpace(acknowledgement.activityid))
                    {
                        throw new JsonSerializationException("A notification acknowledgement is missing its identity.");
                    }

                    string key = acknowledgement.oid + "#" + acknowledgement.victimid + "#" + acknowledgement.type + "#" + acknowledgement.activityid;
                    if (!submitted.TryGetValue(key, out var notification) ||
                        notification.oid != acknowledgement.oid ||
                        notification.victimid != acknowledgement.victimid ||
                        notification.type != acknowledgement.type ||
                        notification.activityid != acknowledgement.activityid ||
                        !seen.Add(key))
                    {
                        throw new JsonSerializationException("A notification acknowledgement has an unknown or duplicate identity.");
                    }

                    if (acknowledgement.isSuccessful)
                    {
                        delivered.Add(new KeyValuePair<string, Notification>(key, notification));
                    }
                }

                // No queue mutation until all acknowledgement identities have been validated.
                var confirmed = new List<Notification>();
                foreach (var delivery in delivered)
                {
                    if (notifications.TryGetValue(delivery.Key, out var pending) && ReferenceEquals(pending, delivery.Value))
                    {
                        notifications.Remove(delivery.Key);
                        confirmed.Add(delivery.Value);
                    }
                }
                return confirmed;
            }

            public int Count()
            {
                return notifications.Count;
            }

            public void Clear()
            {
                notifications.Clear();
            }

            public void Retried(string key)
            {
                if (notifications.ContainsKey(key))
                {
                    notifications[key].retries++;
                    if(notifications[key].retries >= 3)
                    {
                        notifications.Remove(key);
                    }
                }
            }

            public String ToJSON()
            {
                ArrayList list = new ArrayList();

                foreach (var n in notifications)
                {
                    list.Add(n.Value);
                }

                return JsonConvert.SerializeObject(list.ToArray());
            }
        }
        public class Notification
        {
            // Required identities are validated by BulkAdd, not by caller-facing
            // required modifiers or empty-string defaults that change the wire data.
            public string oid { get; set; } = null!;
            public string victimid { get; set; } = null!;
            public string type { get; set; } = null!;
            public string? remindertype { get; set; }
            public string? subtype { get; set; }
            public string? messageto { get; set; }
            public string? messagetoname { get; set; }
            public string? messagedescription { get; set; }
            public string? messagetitle { get; set; }
            public string? messagesubtitle { get; set; }
            public bool delayed { get; set; }
            public string? deliverytime { get; set; }
            public string? additionalinfo { get; set; }
            public string? source { get; set; }
            public string activityid { get; set; } = null!;
            public string? eventdatetime { get; set; }
            public string? pogroup { get; set; }
            public bool feedbackrequired { get; set; }
            public bool seenbyparticipant { get; set; }
            public bool victimgenerated { get; set; }
            public string? timezone { get; set; }
            public string? eventdatetimelocal { get; set; }

            // ignored
            [JsonIgnore]
            public int retries { get; set; }

            public String ToJSON()
            {
                return JsonConvert.SerializeObject(this);
            }
        }
        public class AccountPushNotificationSetting
        {
            // Mapping validates these settings keys at runtime.
            public string UserName { get; set; } = null!;
            public string EventCode { get; set; } = null!;
            public string? EventType { get; set; }
            public string? FieldName { get; set; }
            public string? NotificationSubType { get; set; }
            public string? ReminderType { get; set; }
            public string? PushNotificationToken { get; set; }
            public string? UserFullname { get; set; }
            public string? AlternativeText { get; set; }
        }

        public class ReminderSetting
        {
            public string? EventName { get; set; }
            public string? NotificationSubType { get; set; }
            public string? ReminderType { get; set; }
            public string? AlternativeText { get; set; }
            public string? EventNotificationType { get; set; }
        }

        public class VictimSetting
        {
            // OID is not currently populated by the settings mapping.
            public string? OID { get; set; }
            public string? victimproximity { get; set; }
            public string? offbattery { get; set; }
            public string? offtamper { get; set; }
            public string? offcellgpstatus { get; set; }
        }

        public class NotificationServiceResponse
        {
            private const string InvalidResponseMessage = "The notification response is invalid.";

            public string? code { get; set; }
            public string? type { get; set; }
            public List<NotificationServiceDataResponse> data { get; set; }

            [JsonExtensionData]
            private IDictionary<string, JToken> _additionalData;

            internal static NotificationServiceResponse ParseResponse(string json)
            {
                try
                {
                    if (JToken.Parse(json, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    }) is not JObject envelope)
                    {
                        throw new JsonSerializationException(InvalidResponseMessage);
                    }

                    return envelope.ToObject<NotificationServiceResponse>()
                        ?? throw new JsonSerializationException(InvalidResponseMessage);
                }
                catch (JsonException)
                {
                    // Never retain parser messages or inner exceptions containing payload data.
                    throw new JsonSerializationException(InvalidResponseMessage);
                }
                catch (ArgumentException)
                {
                    throw new JsonSerializationException(InvalidResponseMessage);
                }
                catch (System.Reflection.TargetInvocationException)
                {
                    // Newtonsoft invokes deserialization callbacks through reflection.
                    throw new JsonSerializationException(InvalidResponseMessage);
                }
            }

            [OnDeserialized]
            private void OnDeserialized(StreamingContext context)
            {
                try
                {
                    if (!_additionalData.TryGetValue("object", out JToken? objectToken) || objectToken == null)
                    {
                        throw new JsonSerializationException(InvalidResponseMessage);
                    }

                    // Support both the array contract and the legacy JSON-encoded array
                    // without coercing scalar values into acknowledgement fields.
                    if (objectToken.Type == JTokenType.String)
                    {
                        objectToken = JToken.Parse(objectToken.Value<string>()!, new JsonLoadSettings
                        {
                            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                        });
                    }

                    if (objectToken is not JArray acknowledgements)
                    {
                        throw new JsonSerializationException(InvalidResponseMessage);
                    }

                    var validated = new List<NotificationServiceDataResponse>();
                    foreach (JToken item in acknowledgements)
                    {
                        if (item is not JObject acknowledgement ||
                            !acknowledgement.TryGetValue("isSuccessful", out JToken? success) ||
                            success.Type != JTokenType.Boolean)
                        {
                            throw new JsonSerializationException(InvalidResponseMessage);
                        }

                        validated.Add(new NotificationServiceDataResponse
                        {
                            isSuccessful = success.Value<bool>(),
                            oid = ReadIdentity(acknowledgement, "oid"),
                            victimid = ReadIdentity(acknowledgement, "victimid"),
                            type = ReadIdentity(acknowledgement, "type"),
                            activityid = ReadIdentity(acknowledgement, "activityid")
                        });
                    }

                    // Keep the public data property and extension-data wire mapping intact.
                    data = validated;
                }
                catch (JsonException)
                {
                    throw new JsonSerializationException(InvalidResponseMessage);
                }
            }

            private static string ReadIdentity(JObject acknowledgement, string name)
            {
                if (!acknowledgement.TryGetValue(name, out JToken? token) ||
                    token.Type != JTokenType.String ||
                    string.IsNullOrWhiteSpace(token.Value<string>()))
                {
                    throw new JsonSerializationException(InvalidResponseMessage);
                }

                return token.Value<string>()!;
            }

            public NotificationServiceResponse()
            {
                _additionalData = new Dictionary<string, JToken>();
                data = new List<NotificationServiceDataResponse>();
            }

        }

        public class NotificationServiceDataResponse
        {
            public bool isSuccessful { get; set; }
            // Response parsing validates all four identities before mapping tokens.
            public string victimid { get; set; } = null!;
            public string type { get; set; } = null!;
            public string activityid { get; set; } = null!;
            public string oid { get; set; } = null!;
        }
    }
}
