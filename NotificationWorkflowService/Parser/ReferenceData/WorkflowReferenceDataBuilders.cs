using System.Data;
using System.Globalization;
using NotificationWorkflowService.Entity;

namespace NotificationWorkflowService.Parser.ReferenceData;

/// <summary>
/// Reader-only mappings: no reader ownership, database access, retries, logging or
/// publication. Exceptions escape without exposing partially built dictionaries.
/// </summary>
internal static class WorkflowReferenceDataBuilders
{
    public static Dictionary<string, int> BuildClientProfiles(IDataReader reader, int profileType)
    {
        var result = new Dictionary<string, int>();
        while (reader.Read())
        {
            // Legacy invalid types still consume rows, but never access columns.
            if (profileType == 0 || profileType == 1)
            {
                if (!result.ContainsKey(reader["OID"].ToString()!))
                {
                    result.Add(reader["OID"].ToString()!, Convert.ToInt32(reader["ProfileID"]));
                }
            }
        }
        return result;
    }

    public static Dictionary<int, Profile> BuildProfiles(IDataReader reader)
    {
        var result = new Dictionary<int, Profile>();
        while (reader.Read())
        {
            int profileId = (int)reader["ProfileID"];
            string profileName = reader["ProfileName"].ToString()!;
            int day = Convert.ToInt32(reader["Day"].ToString());
            string eventCode = reader["EventCode"].ToString()!;

            // The four legacy grouping branches map exactly the same fields.
            var item = new ProfileItem
            {
                ProfileID = profileId,
                EventCode = eventCode,
                StartTime = DateTime.ParseExact(reader["StartTime"].ToString()!, "HH:mm:ss", CultureInfo.InvariantCulture),
                EndTime = DateTime.ParseExact(reader["EndTime"].ToString()!, "HH:mm:ss", CultureInfo.InvariantCulture),
                Day = day,
                Action = Convert.ToInt32(reader["Action"]),
                HoldDuration = (int)reader["HoldDuration"],
                GracePeriod = (int)reader["GracePeriod"],
                Instruction = reader["Instruction"].ToString()!,
                Email = reader["EmailAddress"].ToString()!,
                EmailJoin = Convert.ToInt32(reader["EmailJoin"]),
                StateNo = Convert.ToInt32(reader["StateNo"]),
                StateTime = (int)reader["StateTime"],
                FeedBackRequired = Convert.ToBoolean(reader["FeedbackRequired"]),
                ProfileType = (int)reader["ProfileType"],
                TimeIntervalsID = (int)reader["TimeIntervalsID"],
                NextState = Convert.ToInt32(reader["NextState"]),
                LoopStartState = Convert.ToInt32(reader["LoopStartState"]),
                NumberOfLoops = Convert.ToInt32(reader["NumberOfLoops"]),
                RoleID = Convert.ToInt32(reader["RoleID"]),
                RoleAction = Convert.ToInt32(reader["RoleAction"])
            };

            if (!result.TryGetValue(profileId, out var profile))
            {
                profile = new Profile
                {
                    ProfileID = profileId,
                    ProfileName = profileName,
                    Events = new Dictionary<string, Dictionary<int, List<ProfileItem>>>()
                };
                result.Add(profileId, profile);
            }
            if (!profile.Events.TryGetValue(eventCode, out var days))
            {
                days = new Dictionary<int, List<ProfileItem>>();
                profile.Events.Add(eventCode, days);
            }
            if (!days.TryGetValue(day, out var items))
            {
                items = new List<ProfileItem>();
                days.Add(day, items);
            }
            // First profile name wins; items (including duplicates) retain reader order.
            items.Add(item);
        }
        return result;
    }

    public static Dictionary<string, List<Holiday>> BuildHolidays(IDataReader reader)
    {
        var result = new Dictionary<string, List<Holiday>>();
        var holidays = new List<Holiday>();
        string currentGroup = "";
        while (reader.Read())
        {
            if (currentGroup != reader["POGroup"].ToString())
            {
                // Deliberately preserve the empty-group sentinel and contiguous
                // grouping: returning to an already flushed group throws on Add.
                if (currentGroup != "")
                {
                    result.Add(currentGroup, holidays);
                    holidays = new List<Holiday>();
                }
                currentGroup = reader["POGroup"].ToString()!;
            }

            DateTime start = Convert.ToDateTime(reader["StartDate"].ToString());
            DateTime end = Convert.ToDateTime(reader["EndDate"].ToString());
            string name = reader["HolidayName"].ToString()!;
            holidays.Add(new Holiday { HolidayName = name, StartDate = start, EndDate = end });
        }
        if (holidays.Count > 0)
        {
            result.Add(currentGroup, holidays);
        }
        return result;
    }

    /// <summary>
    /// All three parsers reset roles on refresh and use strict Add, not first-wins.
    /// Omit the seed for that behavior; an explicit seed is copied, never mutated,
    /// and duplicate keys against it also throw.
    /// </summary>
    public static Dictionary<string, string> BuildRoles(
        IDataReader reader, IReadOnlyDictionary<string, string>? existingRoles = null)
    {
        var result = existingRoles == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(existingRoles);
        while (reader.Read())
        {
            result.Add(reader["SystemID"].ToString()!, reader["RoleName"].ToString()!);
        }
        return result;
    }

    /// <summary>
    /// Common/initiator rebuild both dictionaries; first type per victim wins.
    /// Steps rebuild only victims and never access the VictimType column, leaving
    /// Victim.VictimType at its legacy null default. Offenders need not be contiguous.
    /// </summary>
    public static WorkflowVictimReferenceData BuildVictims(IDataReader reader, bool step = false)
    {
        var victims = new Dictionary<string, List<Victim>>();
        var types = new Dictionary<string, string>();
        while (reader.Read())
        {
            var victim = new Victim
            {
                OID = reader["Victim"].ToString()!,
                Email = reader["EmailAddress"].ToString()!,
                CellPhone = reader["CellPhone"].ToString()!
            };
            if (!step)
            {
                victim.VictimType = reader["VictimType"].ToString()!;
            }
            string offender = reader["Offender"].ToString()!;
            if (!step && !types.ContainsKey(victim.OID))
            {
                types.Add(victim.OID, victim.VictimType);
            }
            if (!victims.TryGetValue(offender, out var list))
            {
                list = new List<Victim>();
                victims.Add(offender, list);
            }
            list.Add(victim);
        }
        return new WorkflowVictimReferenceData(victims, types);
    }

    public static Dictionary<string, HashSet<string>> BuildMezVictims(IDataReader reader)
    {
        var result = new Dictionary<string, HashSet<string>>();
        while (reader.Read())
        {
            string offender = reader["Offender"].ToString()!;
            string victim = reader["Victim"].ToString()!;
            if (!result.TryGetValue(offender, out var victims))
            {
                victims = new HashSet<string>();
                result.Add(offender, victims);
            }
            victims.Add(victim);
        }
        return result;
    }

    public static Dictionary<string, Dictionary<string, HashSet<string>>> BuildAttachedVictimZones(IDataReader reader)
    {
        var result = new Dictionary<string, Dictionary<string, HashSet<string>>>();
        while (reader.Read())
        {
            string zoneId = reader["ZoneID"].ToString()!;
            string category = reader["ZoneCategory"].ToString()!;
            string offender = reader["OffenderID"].ToString()!;
            string victim = reader["VictimID"].ToString()!;
            string key = zoneId + "|" + category;
            if (!result.TryGetValue(offender, out var zones))
            {
                zones = new Dictionary<string, HashSet<string>>();
                result.Add(offender, zones);
            }
            if (!zones.TryGetValue(key, out var victims))
            {
                victims = new HashSet<string>();
                zones.Add(key, victims);
            }
            victims.Add(victim);
        }
        return result;
    }

    public static Dictionary<int, List<ProfileItemClear>> BuildClearEvents(IDataReader reader)
    {
        var result = new Dictionary<int, List<ProfileItemClear>>();
        while (reader.Read())
        {
            int profileId = Convert.ToInt32(reader["ProfileID"]);
            string clearingEvent = reader["ClearingEvent"].ToString()!;
            string eventCode = reader["EventCode"].ToString()!;
            var item = new ProfileItemClear { ClearingEvent = clearingEvent, EventCode = eventCode };
            if (!result.TryGetValue(profileId, out var items))
            {
                items = new List<ProfileItemClear>();
                result.Add(profileId, items);
            }
            items.Add(item);
        }
        return result;
    }
}

internal sealed record WorkflowVictimReferenceData(
    Dictionary<string, List<Victim>> Victims,
    Dictionary<string, string> VictimTypeDict);