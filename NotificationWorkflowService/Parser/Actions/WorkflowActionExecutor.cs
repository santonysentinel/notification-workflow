using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Entity;
using System.Text;

namespace NotificationWorkflowService.Parser.Actions;

internal static class WorkflowActionExecutor
{
    internal static WorkflowActionResult Execute(ActiveAlarm alarm, WorkflowActionContext context)
    {
        ActiveAlarm a = alarm;
        var operations = context.Operations;
        bool step = context.Mode == WorkflowActionMode.Step;
        bool insert = true;
        var sb = new StringBuilder();

        switch (a.Priority)
        {
            case 1:
                sb.Append("Do Nothing");
                WriteAction(a, context, "Do Nothing");
                insert = false;
                break;
            case 2:
                sb.Append("Auto Email");
                WriteAction(a, context, "Auto Email");
                operations.SendNotificationsToOfficersInSameGroup(a);
                operations.AddToNotificationQueue(a, 3);
                string emailAdresses = Truncate("Pages sent to: " + a.EmailAddresses);
                operations.CreateAlarmAudit(1, emailAdresses, a.HistoryID, step ? a.CurrentStateNo : 1);
                operations.AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                break;
            case 3:
                sb.Append("McApp");
                WriteAction(a, context, "McApp");
                operations.SendNotificationsToOfficersInSameGroup(a);
                operations.PushAlertToMcApp(a);
                a.ProcessNextStep = 0;
                break;
            case 4:
                sb.Append("Auto Fax");
                WriteAction(a, context, "Auto Fax");
                operations.SendNotificationsToOfficersInSameGroup(a);
                break;
            case 5:
                sb.Append("Auto Page");
                WriteAction(a, context, "Auto Page");
                operations.SendNotificationsToOfficersInSameGroup(a);
                operations.AddToNotificationQueue(a, 1);
                string autoPageEmails = Truncate("Pages sent to: " + operations.getInsertEmails(a));
                operations.CreateAlarmAudit(3, autoPageEmails, a.HistoryID, step ? a.CurrentStateNo : 1);
                operations.AddActiveAlarmActionToActivity(a.HistoryID, autoPageEmails, 0);
                break;
            case 6:
                sb.Append("McApp and Auto Fax");
                WriteAction(a, context, "McApp and Auto Fax");
                operations.SendNotificationsToOfficersInSameGroup(a);
                operations.PushAlertToMcApp(a);
                a.ProcessNextStep = 0;
                break;
            case 7:
                sb.Append("McApp and Auto Email");
                WriteAction(a, context, "McApp and Auto Email");
                operations.PushAlertToMcApp(a);
                operations.SendNotificationsToOfficersInSameGroup(a);
                operations.AddToNotificationQueue(a, 3);
                string emailAdresses7 = Truncate("Pages sent to: " + a.EmailAddresses);
                operations.CreateAlarmAudit(step ? 3 : 1, emailAdresses7, a.HistoryID, step ? a.CurrentStateNo : 1);
                operations.AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                a.ProcessNextStep = 0;
                break;
            case 9:
                sb.Append("McApp and Auto Page");
                WriteAction(a, context, "McApp and Auto Page");
                operations.SendNotificationsToOfficersInSameGroup(a);
                operations.PushAlertToMcApp(a);
                operations.AddToNotificationQueue(a, 1);
                string autoPageMcAppEmails = Truncate("Pages sent to: " + operations.getInsertEmails(a));
                operations.CreateAlarmAudit(3, autoPageMcAppEmails, a.HistoryID, step ? a.CurrentStateNo : 1);
                operations.AddActiveAlarmActionToActivity(a.HistoryID, autoPageMcAppEmails, 0);
                a.ProcessNextStep = 0;
                break;
            case 11:
                sb.Append("Delay");
                WriteAction(a, context, "Delay");
                break;
            case 12:
                sb.Append("Role Based - ");
                sb.Append(context.RoleActions[a.RoleAction] + " ");
                sb.Append(context.Roles[a.RoleID.ToString()]);
                switch (a.RoleAction)
                {
                    case 1:
                        if (a.Instruction != "")
                        {
                            WriteAction(a, context, step ? "McApp Call Officers" : "McApp - Call Officers");
                            operations.PushAlertToMcApp(a);
                            a.ProcessNextStep = 0;
                        }
                        break;
                    case 2:
                    case 3:
                        if (a.EmailAddresses != "")
                        {
                            WriteAction(a, context, "Auto Email");
                            operations.AddToNotificationQueue(a, 3);
                            string emails = Truncate("Pages sent to: " + a.EmailAddresses);
                            operations.CreateAlarmAudit(14, emails, a.HistoryID, step ? a.CurrentStateNo : 1);
                            operations.AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                        }
                        break;
                }
                break;
            case 13:
                if (!step) sb.Append("Email-Victims");
                WriteAction(a, context, "Email-Victims");
                string victimsMail = "";
                try
                {
                    List<Victim> offenderVictims = context.Victims[a.ClientID];
                    var victimEmail = new StringBuilder();
                    foreach (Victim vi in offenderVictims)
                    {
                        if (!string.IsNullOrEmpty(vi.Email))
                        {
                            victimEmail.Append(vi.Email);
                            victimEmail.Append(";");
                        }
                    }
                    victimsMail = victimEmail.ToString();
                    operations.insertNotificationQueueVictim(a, 3, victimsMail, true);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(ex, step ? "victimsMail" : "Read Victim Emails");
                    victimsMail = "";
                }
                string victimEmails = Truncate("Pages sent to: " + victimsMail);
                operations.CreateAlarmAudit(14, victimEmails, a.HistoryID, 1);
                operations.AddActiveAlarmActionToActivity(a.HistoryID, victimEmails, 1);
                break;
            case 14:
                sb.Append(step ? "Contact Victim" : "Contact Victims");
                WriteAction(a, context, "Contact Victim");
                operations.SendNotificationsToOfficersInSameGroup(a);
                operations.PushAlertToMcApp(a);
                a.ProcessNextStep = 0;
                break;
            case 15:
                if (!step) sb.Append("Alert Client - Email");
                WriteAction(a, context, step ? "Alert Client" : "Alert Client Email");
                string clientMail = "";
                try
                {
                    clientMail = operations.getClientEmail(a).Trim();
                    if (clientMail != string.Empty)
                    {
                        operations.insertNotificationQueueVictim(a, 3, clientMail);
                    }
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(ex, "clientMail");
                    clientMail = "";
                }
                string cMail = Truncate("Pages sent to: " + clientMail);
                operations.CreateAlarmAudit(14, cMail, a.HistoryID, 1);
                operations.AddActiveAlarmActionToActivity(a.HistoryID, cMail, 1);
                break;
            case 16:
                if (!step) sb.Append("Alert Client - Text");
                WriteAction(a, context, "Alert Client Text");
                string clientText = "";
                try
                {
                    clientText = operations.getClientText(a).Trim();
                    if (clientText != string.Empty)
                    {
                        operations.insertNotificationQueueVictim(a, 4, clientText);
                    }
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(ex, step ? "clientText" : "getClientText");
                    clientText = "";
                }
                string cText = Truncate("Pages sent to: " + clientText);
                operations.CreateAlarmAudit(14, cText, a.HistoryID, 1);
                operations.AddActiveAlarmActionToActivity(a.HistoryID, cText, 1);
                break;
            case 17:
                sb.Append("Text All Victims");
                WriteAction(a, context, "Text All Victims");
                string victimsText = "";
                try
                {
                    List<Victim> offenderVictims = context.Victims[a.ClientID];
                    var victimText = new StringBuilder();
                    foreach (Victim vi in offenderVictims)
                    {
                        if (!string.IsNullOrEmpty(vi.CellPhone))
                        {
                            victimText.Append(vi.CellPhone);
                            victimText.Append(";");
                        }
                    }
                    victimsText = victimText.ToString();
                    operations.insertNotificationQueueVictim(a, 4, victimsText, true);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(ex, "victimsText");
                    victimsText = "";
                }
                string Msg = "Text sent to: " + victimsText;
                if (Msg.Length > 4096)
                {
                    if (step) Msg = Msg.Substring(0, 4096);
                    // Deliberately preserve the unused assignment and its possible exception.
                    else victimEmails = victimsText.Substring(0, 4096);
                }
                operations.CreateAlarmAudit(14, Msg, a.HistoryID, 1);
                operations.AddActiveAlarmActionToActivity(a.HistoryID, Msg, 1);
                break;
            default:
                sb.Append("Do Nothing");
                if (!step)
                {
                    WriteAction(a, context, "Do Nothing");
                    insert = false;
                }
                break;
        }

        return new WorkflowActionResult(insert, sb.ToString());
    }

    private static string Truncate(string value) => value.Length > 4096 ? value.Substring(0, 4096) : value;

    private static void WriteAction(ActiveAlarm a, WorkflowActionContext context, string action)
    {
        bool step = context.Mode == WorkflowActionMode.Step;
        bool suffix = step ? a.Priority <= 12 : a.Priority == 11;
        Console.ForegroundColor = step && a.Priority != 12 && a.Priority < 13 ? ConsoleColor.Cyan : ConsoleColor.Green;
        // Step priorities 1/2 retain AAID; subsequent console messages use platform instead.
        Console.WriteLine(FormatAction(a, context, action, suffix, step && a.Priority != 1 && a.Priority != 2));
        context.Logger.LogInformation(FormatAction(a, context, action, suffix, false));
    }

    private static string FormatAction(ActiveAlarm a, WorkflowActionContext context, string action, bool suffix, bool platformPrefix)
    {
        // Evaluate timestamp, alarm fields and suffix separately for console and logger, as before.
        return (platformPrefix ? "[" + context.Platform + "] " : "")
            + DateTime.UtcNow.ToString("HH:mm:ss")
            + (platformPrefix ? " " : " AAID:" + a.SystemID.ToString() + " ")
            + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]"
            + " Action: " + action + (suffix ? " " + "(Step " + a.StateNo + ")" : "");
    }
}