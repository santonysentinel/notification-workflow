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
        public string AlarmID { get; set; }
        public string AlarmName { get; set; }
        public string Text { get; set; }
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
            public string oid { get; set; }
            public string victimid { get; set; }
            public string type { get; set; }
            public string remindertype { get; set; }
            public string subtype { get; set; }
            public string messageto { get; set; }
            public string messagetoname { get; set; }
            public string messagedescription { get; set; }
            public string messagetitle { get; set; }
            public string messagesubtitle { get; set; }
            public bool delayed { get; set; }
            public string deliverytime { get; set; }
            public string additionalinfo { get; set; }
            public string source { get; set; }
            public string activityid { get; set; }
            public string eventdatetime { get; set; }
            public string pogroup { get; set; }
            public bool feedbackrequired { get; set; }
            public bool seenbyparticipant { get; set; }
            public bool victimgenerated { get; set; }
            public string timezone { get; set; }
            public string eventdatetimelocal { get; set; }

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
            public string UserName { get; set; }
            public string EventCode { get; set; }
            public string EventType { get; set; }
            public string FieldName { get; set; }
            public string NotificationSubType { get; set; }
            public string ReminderType { get; set; }
            public string PushNotificationToken { get; set; }
            public string UserFullname { get; set; }
            public string AlternativeText { get; set; }
        }

        public class ReminderSetting
        {
            public string EventName { get; set; }
            public string NotificationSubType { get; set; }
            public string ReminderType { get; set; }
            public string AlternativeText { get; set; }
            public string EventNotificationType { get; set; }
        }

        public class VictimSetting
        {
            public string OID { get; set; }
            public string victimproximity { get; set; }
            public string offbattery { get; set; }
            public string offtamper { get; set; }
            public string offcellgpstatus { get; set; }
        }

        public class NotificationServiceResponse
        {
            public string code { get; set; }
            public string type { get; set; }
            public List<NotificationServiceDataResponse> data { get; set; }

            [JsonExtensionData]
            private IDictionary<string, JToken> _additionalData;

            [OnDeserialized]
            private void OnDeserialized(StreamingContext context)
            {
                var objectString = _additionalData["object"].ToString();
                data = JsonConvert.DeserializeObject<List<NotificationServiceDataResponse>>(objectString)
                    ?? throw new JsonSerializationException("The notification response 'object' must contain a data array.");
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
            public string victimid { get; set; }
            public string type { get; set; }
            public string activityid { get; set; }
            public string oid { get; set; }
        }
    }
}
