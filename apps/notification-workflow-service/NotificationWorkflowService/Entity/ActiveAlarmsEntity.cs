using System;
using System.Collections.Generic;
using System.Text;

namespace NotificationWorkflowService.Entity
{
    public class ActiveAlarm
    {
        public ActiveAlarm()
        {
            Active = 1;
            EmailAddresses = "";
            StateNo = -1;
            StateTime = -1;
            NextStateNo = -1;
            ProcessNextStep = 1;
            LoopStartState = -1;
            CurrentLoopNumber = -1;
            RoleAction = -1;
            FeedbackREQ = false;
        }

        public int SystemID { get; set; }
        public int HistoryID { get; set; }
        public int AgencyID { get; set; }
        public int ClientSystemID { get; set; }
        public String ClientID { get; set; }
        public String ClientTZ { get; set; }
        public int AlarmSystemID { get; set; }
        public String AlarmID { get; set; }
        public String DeviceID { get; set; }
        public int StateID { get; set; }
        public long ReceivedDateTime { get; set; }
        public long EventDateTime { get; set; }
        public int Active { get; set; }
        public String AlarmText { get; set; }
        public int Priority { get; set; }
        public String EmailAddresses { get; set; }
        public int EmailJoin { get; set; }
        public String POGroupNum { get; set; }
        public String POGroup1 { get; set; }
        public String POGroup2 { get; set; }
        public String POGroup3 { get; set; }
        public int CurrentStateNo { get; set; }
        public int StateNo { get; set; }
        public int StateTime { get; set; }
        public int NextStateNo { get; set; }
        public String ProfileName { get; set; }
        public String Instruction { get; set; }
        public int ProfileID { get; set; }
        public string? OptionsJson { get; set; }
        public int LoopStartState { get; set; }
        public int CurrentLoopNumber { get; set; }
        public int ProcessNextStep { get; set; }
        public int RoleAction { get; set; }
        public int RoleID { get; set; }
        public Boolean FeedbackREQ { get; set; }
        public Boolean IsAlarmClearingEnabled { get; set; }
        public String ExpiryTime { get; set; }
        /// <summary>Queue job the next step is waiting on; set by a Robo Call action with WaitForCallCompletion.</summary>
        public int? RoboCallWaitQueueId { get; set; }
        public int RoboCallWaitSeconds { get; set; }
        /// <summary>Queue job still recorded on an expired state row, meaning the Robo Call wait timed out.</summary>
        public int? ExpiredRoboCallWaitQueueId { get; set; }

        /// <summary>
        /// Next-state expiry: the parser's normal expiry, or for a Robo Call wait now + max(StateTime, call timeout)
        /// so the step waits for the call (released early by the TenantNotes worker when the call ends).
        /// </summary>
        public DateTime NextStateExpiry(DateTime normalExpiry, DateTime utcNow)
        {
            if (RoboCallWaitQueueId is null) return normalExpiry;
            var stateTime = TimeSpan.FromMinutes(StateTime);
            var timeout = TimeSpan.FromSeconds(RoboCallWaitSeconds);
            return utcNow + (stateTime > timeout ? stateTime : timeout);
        }
        public String EventDateTimeLocal { get; set; }
        public String EventDateTimeUTC { get; set; }
        public String ZoneID { get; set; }
        public String ZoneCategory { get; set; }
        public String ZoneName { get; set; }
        public String ZoneAddress { get; set; }
        public String PolyZoneName { get; set; }
        public String MEZEventVictimID { get; set; }
        public String OffenderName { get; set; }

        public String MessageReceivedDateTime()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            epoch = epoch.AddSeconds((int)ReceivedDateTime);
            return epoch.ToString("MMM dd yyyy hh:mm tt");
        }
        public String EventRecievedDateTime()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            epoch = epoch.AddSeconds((int)EventDateTime);
            return epoch.ToString("MMM dd yyyy hh:mm tt");
        }
    }

    public class Profile
    {
        public int ProfileID { get; set; }
        public String ProfileName { get; set; }
        public Dictionary<String, Dictionary<int, List<ProfileItem>>> Events { get; set; }
    }

    public class Holiday
    {
        public String HolidayName { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
    }

    public class ProfileItem
    {
        public int ProfileID { get; set; }
        public string? OptionsJson { get; set; }
        public String EventCode { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int Day { get; set; }
        public int Action { get; set; }
        public int HoldDuration { get; set; }
        public int GracePeriod { get; set; }
        public String Instruction { get; set; }
        public String Email { get; set; }
        public int EmailJoin { get; set; }
        public int StateNo { get; set; }
        public int StateTime { get; set; }
        public Boolean FeedBackRequired { get; set; }
        public int ProfileType { get; set; }
        public int TimeIntervalsID { get; set; }
        public int NextState { get; set; }
        public int LoopStartState { get; set; }
        public int NumberOfLoops { get; set; }
        public int RoleID { get; set; }
        public int RoleAction { get; set; }
    }

    public class ProfileItemClear
    {
        public String EventCode { get; set; }
        public String ClearingEvent { get; set; }
    }

    public class Victim
    {
        public String OID { get; set; }
        public String Email { get; set; }
        public String CellPhone { get; set; }
        public String VictimType { get; set; }
    }
}
