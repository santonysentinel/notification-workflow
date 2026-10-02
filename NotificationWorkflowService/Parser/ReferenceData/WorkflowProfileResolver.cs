using NotificationWorkflowService.Entity;

namespace NotificationWorkflowService.Parser.ReferenceData;

/// <summary>
/// In-memory selection without logging or fallback policy. Exceptions are left to
/// the caller's existing wrapper; only step selection changes alarm loop state.
/// </summary>
internal static class WorkflowProfileResolver
{
    public static bool IsHoliday(
        ActiveAlarm alarm, DateTime current, IReadOnlyDictionary<string, List<Holiday>> holidays)
    {
        bool holiday = false;
        CheckGroup(alarm.POGroupNum);
        CheckGroup(alarm.POGroup1);
        CheckGroup(alarm.POGroup2);
        CheckGroup(alarm.POGroup3);
        return holiday;

        void CheckGroup(string group)
        {
            // Trim only tests emptiness. Do not short circuit after a match:
            // later null groups, lists or items must still throw as before.
            if (group.Trim() != "" && holidays.ContainsKey(group))
            {
                foreach (Holiday item in holidays[group])
                {
                    if (item.StartDate.Date <= current.Date && item.EndDate.Date >= current.Date)
                    {
                        holiday = true;
                    }
                }
            }
        }
    }

    public static Profile? ResolveProfile(
        ActiveAlarm alarm,
        DateTime current,
        IReadOnlyDictionary<string, int> normalMapping,
        IReadOnlyDictionary<string, int> holidayMapping,
        IReadOnlyDictionary<int, Profile> profiles,
        IReadOnlyDictionary<string, List<Holiday>> holidays)
    {
        var mapping = IsHoliday(alarm, current, holidays) ? holidayMapping : normalMapping;
        if (mapping.ContainsKey(alarm.ClientID) && profiles.ContainsKey(mapping[alarm.ClientID]))
        {
            return profiles[mapping[alarm.ClientID]];
        }
        // Holiday mapping failures never fall back to the normal profile.
        return null;
    }

    public static ProfileItem? FindInitialProfileItem(List<ProfileItem> profileItems, DateTime current)
    {
        var indexes = new Dictionary<int, int>();
        for (int i = 0; i < profileItems.Count; i++)
        {
            if (profileItems[i].StartTime.TimeOfDay <= current.TimeOfDay
                && profileItems[i].EndTime.TimeOfDay >= current.TimeOfDay)
            {
                if (!indexes.ContainsKey(profileItems[i].StateNo))
                {
                    indexes.Add(profileItems[i].StateNo, i);
                }
            }
        }
        if (indexes.Count == 0)
        {
            return null;
        }
        int state = indexes.ContainsKey(0) ? 0 : indexes.Min(item => item.Key);
        return profileItems[indexes[state]];
    }

    /// <summary>
    /// Stable state ordering, inclusive start/exclusive end, and first duplicate
    /// match. Only CurrentLoopNumber is mutated, including when an exhausted loop
    /// has no eligible successor. The caller still applies the selected item.
    /// </summary>
    public static ProfileItem? FindStepProfileItem(
        ActiveAlarm alarm, List<ProfileItem> profileItems, DateTime current)
    {
        var matching = new List<ProfileItem>();
        foreach (ProfileItem item in profileItems)
        {
            if (item.StartTime.TimeOfDay <= current.TimeOfDay && item.EndTime.TimeOfDay > current.TimeOfDay)
            {
                matching.Add(item);
            }
        }
        matching = matching.OrderBy(item => item.StateNo).ToList();
        foreach (ProfileItem item in matching)
        {
            if (item.StartTime.TimeOfDay <= current.TimeOfDay
                && item.EndTime.TimeOfDay > current.TimeOfDay && alarm.StateNo == item.StateNo)
            {
                if (alarm.StateNo == item.LoopStartState)
                {
                    alarm.CurrentLoopNumber = alarm.CurrentLoopNumber + 1;
                }
                if (alarm.CurrentLoopNumber >= item.NumberOfLoops)
                {
                    foreach (ProfileItem next in matching)
                    {
                        if (next.StartTime.TimeOfDay <= current.TimeOfDay
                            && next.EndTime.TimeOfDay > current.TimeOfDay
                            && next.StateNo > item.StateNo
                            && (next.LoopStartState == -1 || next.LoopStartState > item.LoopStartState))
                        {
                            alarm.CurrentLoopNumber = -1;
                            return next;
                        }
                    }
                    return null;
                }
                return item;
            }
        }
        return null;
    }
}