using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using NotificationWorkflowService.Entity;
using WorkflowRepository = NotificationWorkflowService.Repository.Repository;
using Xunit;

namespace NotificationWorkflowService.Tests;

/// <summary>
/// SOURCE CONTRACT tests, not runtime database or delivery tests. SQL is now behind
/// an injectable repository; parser source checks remain lexical. The parameter mapping
/// test also calls pure internal repository builders to inspect unconnected SqlCommands;
/// no SQL is executed. Ordered calls below are direct source occurrences, not transitive
/// writes, successful commits, or proof that a conditional branch executes.
/// </summary>
public class WorkflowActionSourceCharacterizationTests
{
    private static readonly string[] ParserFiles =
        ["WorkFlowInitiator.cs", "ParserCommon.cs", "WorkFlowSteps.cs"];
    private static readonly int[] Priorities = [1, 2, 3, 4, 5, 6, 7, 9, 11, 12, 13, 14, 15, 16, 17];
    private const string Officer = "SendNotificationsToOfficersInSameGroup(a)";
    private const string McApp = "PushAlertToMcApp(a)";
    private const string ActionTargets =
        "SendNotificationsToOfficersInSameGroup|PushAlertToMcApp|AddToNotificationQueue|" +
        "CreateAlarmAudit|AddActiveAlarmActionToActivity|insertNotificationQueueVictim|" +
        "getClientEmail|getClientText|getInsertEmails";

    public static IEnumerable<object[]> Parsers() => ParserFiles.Select(file => new object[] { file });

    public static IEnumerable<object[]> PriorityCases()
    {
        foreach (string file in ParserFiles)
        foreach (int priority in Priorities)
        {
            if (priority == 12)
            {
                foreach (int role in new[] { 1, 2, 3 })
                    yield return [file, priority, role];
            }
            else
                yield return [file, priority, 0];
        }
    }

    [Theory]
    [MemberData(nameof(PriorityCases))]
    public void SourceContract_EveryPriorityAndRoleHasExactOrderedDirectActionCalls(
        string file, int priority, int role)
    {
        string segment = PrioritySegments(Method(ReadSource(file), "parseAlarms"))[priority.ToString()];
        if (priority == 12)
            segment = SwitchSegments(segment, "a.RoleAction")[role.ToString()];

        Assert.Equal(ExpectedActionCalls(file, priority, role), Calls(segment, ActionTargets));

        // These assignments are part of the source contract, not observed state changes.
        bool stopsNextStep = priority is 3 or 6 or 7 or 9 or 14 || priority == 12 && role == 1;
        Assert.Equal(stopsNextStep, Regex.IsMatch(CodeMask(segment), @"a\.ProcessNextStep\s*=\s*0\s*;"));
        if (priority == 12)
            Assert.Contains(role == 1 ? "if (a.Instruction != \"\")" : "if (a.EmailAddresses != \"\")", segment);
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void SourceContract_EnumeratesActualPriorityAndRoleLabelsWithoutNestedCaseLeakage(string file)
    {
        string parse = Method(ReadSource(file), "parseAlarms");
        var segments = PrioritySegments(parse);
        Assert.Equal(Priorities.Select(p => p.ToString()).Append("default"), segments.Keys);
        Assert.Equal(new[] { "1", "2", "3", "default" }, SwitchSegments(segments["12"], "a.RoleAction").Keys);
        Assert.Empty(Calls(segments["default"], ActionTargets));
        Assert.Empty(Calls(SwitchSegments(segments["12"], "a.RoleAction")["default"], ActionTargets));
        var (open, close) = SwitchBounds(parse, "a.Priority");
        // Count actual source occurrences as well as checking individual case slices.
        Assert.Equal(48, Calls(parse[(open + 1)..close], ActionTargets).Length);
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void SourceContract_FinalAuditThenArchiveThenConditionalStateWritesThenNormalCheckpoint(string file)
    {
        string parse = Method(ReadSource(file), "parseAlarms");
        var (switchStart, switchEnd) = SwitchBounds(parse, "a.Priority");
        bool steps = IsSteps(file);
        string step = steps ? "a.CurrentStateNo" : "1";
        Assert.Equal(steps ? Array.Empty<string>() : new[] { "CreateAlarmAudit(15,\"\",a.HistoryID,1)" },
            Calls(parse[..switchStart], ActionTargets));

        string tail = parse[(switchEnd + 1)..];
        string expiry = steps ? "DateTime.UtcNow.AddMinutes(a.StateTime)" : "ExpiryTimeApplied";
        string display = steps ? "a.CurrentStateNo" : "a.StateNo+1";
        string loop = steps ? "a.CurrentLoopNumber" : "0";
        string fallback = steps ? "a.CurrentStateNo+1" : "a.StateNo+1";
        var expected = new List<string>
        {
            $"CreateAlarmAudit(14,sb.ToString(),a.HistoryID,{step})",
            $"insertIntoAlarmNotification(a.SystemID,{step},{expiry},PriorityMapping[a.Priority])",
            $"insertIntoCurrentAlarmNotification(a.SystemID,a.NextStateNo,{expiry},{display},{loop},a.ProcessNextStep)",
            $"insertIntoCurrentAlarmNotification(a.SystemID,{fallback},{expiry},{display},0,1)"
        };
        if (!steps)
            expected.Add("updateParserActivty(sysID)");
        Assert.Equal(expected, Calls(tail,
            "CreateAlarmAudit|insertIntoAlarmNotification|insertIntoCurrentAlarmNotification|updateParserActivty"));
        Assert.Contains("if (a.NextStateNo != -1 && insert)", tail);
        Assert.Contains("else if (insert)", tail);
        if (!steps)
        {
            Assert.Contains("int? sysID = null;", parse);
            AssertOrdered(tail, "insertIntoCurrentAlarmNotification", "sysID = a.SystemID;",
                "Error processing alarm", "if (sysID != null)", "updateParserActivty(sysID)");
        }
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void SourceContract_InsertFlagsAndExpiryRetainNormalStepDifferences(string file)
    {
        string parse = Method(ReadSource(file), "parseAlarms");
        var segments = PrioritySegments(parse);
        Assert.Matches(@"\b(?:bool|Boolean)\s+insert\s*=\s*true\s*;", parse);
        Assert.Contains("insert = false;", segments["1"]);
        Assert.Equal(!IsSteps(file), segments["default"].Contains("insert = false;", StringComparison.Ordinal));
        Assert.Equal(IsSteps(file), segments["11"].Contains("insert = true;", StringComparison.Ordinal));
        Assert.Equal(IsSteps(file) ? 1 : 2, Regex.Matches(CodeMask(parse), @"\binsert\s*=\s*false\s*;").Count);
        if (IsSteps(file))
        {
            Assert.Equal(3, Regex.Matches(CodeMask(parse), @"DateTime\.UtcNow\.AddMinutes\(a\.StateTime\)").Count);
            Assert.DoesNotContain("ExpiryTimeApplied", CodeMask(parse));
        }
        else
        {
            AssertOrdered(parse, "DateTime EventDateTime = DateTime.Parse(a.EventRecievedDateTime());",
                "ExpiryTimeByEventDateTime = EventDateTime.AddMinutes(a.StateTime);",
                "ExpiryTimeByUTCNow = DateTime.UtcNow.AddMinutes(a.StateTime);",
                "if (ExpiryTimeByEventDateTime < ExpiryTimeByUTCNow)",
                "ExpiryTimeApplied = ExpiryTimeByEventDateTime;", "else",
                "ExpiryTimeApplied = ExpiryTimeByUTCNow;", "insertIntoAlarmNotification");
        }
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void SourceContract_AlarmExceptionBreaksNormalLoopButStepCatchHasNoBreak(string file)
    {
        string parse = Method(ReadSource(file), "parseAlarms");
        string catchBody = CatchContaining(parse, IsSteps(file) ? "Step Parser Parse Error" : "Error processing alarm");
        Assert.Contains("log.LogError", catchBody);
        Assert.Equal(!IsSteps(file), Regex.IsMatch(CodeMask(catchBody), @"\bbreak\s*;"));
        Assert.DoesNotMatch(@"\b(?:throw|return|continue)\b", CodeMask(catchBody));
        if (IsSteps(file))
            Assert.DoesNotContain("updateParserActivty", CodeMask(parse));
        else
        {
            string outer = CatchContaining(parse, "FAILED TO PARSE ALARMS");
            Assert.Contains("if (curr != null)", outer);
            Assert.Contains("log.LogInformation", outer);
            Assert.DoesNotMatch(@"\b(?:throw|return)\b", CodeMask(outer));
        }
    }

    public static IEnumerable<object[]> HelperCatchCases()
    {
        foreach (string file in ParserFiles)
        {
            foreach (string method in new[] { "PushAlertToMcApp", "AddToNotificationQueue",
                "insertNotificationQueueVictim", "insertIntoAlarmNotification", "insertIntoCurrentAlarmNotification" })
                yield return [file, method, true];
            foreach (string method in new[] { "CreateAlarmAudit", "AddActiveAlarmActionToActivity" })
                yield return [file, method, false];
            if (!IsSteps(file))
                yield return [file, "updateParserActivty", true];
        }
    }

    [Theory]
    [MemberData(nameof(HelperCatchCases))]
    public void SourceContract_WriteHelperOuterCatchRethrowsOrSwallowsAsWritten(string file, string method, bool rethrows)
    {
        string body = Method(ReadSource(file), method);
        string outer = CatchBodies(body).Last();
        Assert.Contains("log.Log", outer);
        Assert.Equal(rethrows, Regex.IsMatch(CodeMask(outer), @"\bthrow\s*;"));
        Assert.DoesNotMatch(@"\bthrow\s+\w+\s*;", CodeMask(outer));
        if (rethrows || IsSteps(file))
        {
            Assert.Contains("success = false;", outer);
            Assert.Contains("return success;", body);
        }
        // Deliberately do not freeze legacy retry counts/deadlock detection/sleeps.
        // Non-deadlock failures can leave retry loops spinning; exhausted retries can
        // bypass intended throws. Outer catch syntax is not proof those catches run.
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void SourceContract_VictimAndClientLocalCatchesClearRecipientsBeforeAuditAndHistory(string file)
    {
        var segments = PrioritySegments(Method(ReadSource(file), "parseAlarms"));
        foreach (var (priority, recipient) in new[] { (13, "victimsMail"), (15, "clientMail"),
            (16, "clientText"), (17, "victimsText") })
        {
            string segment = segments[priority.ToString()];
            string local = Assert.Single(CatchBodies(segment));
            Assert.Contains("log.LogError", local);
            Assert.Contains(recipient + " = \"\";", local);
            Assert.DoesNotMatch(@"\b(?:throw|break|return)\b", CodeMask(local));
            AssertOrdered(segment, "insertNotificationQueueVictim", "catch (Exception ex)",
                "CreateAlarmAudit", "AddActiveAlarmActionToActivity");
            if (priority is 15 or 16)
                Assert.Matches($@"if\s*\({recipient}\s*!=\s*(?:String|string)\.Empty\)", segment);
        }
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void SourceContract_EmailJoinIsConditionalAndHasItsOwnQueueAuditHistorySequence(string file)
    {
        string body = Method(ReadSource(file), "SendNotificationsToOfficersInSameGroup");
        Assert.Contains("if (a.EmailJoin == 1)", body);
        Assert.Contains("if (AddToNotificationQueue(a, 2))", body);
        // Lexical direct-call order: nested getInsertEmails is NOT an extra history write.
        Assert.Equal(new[] { "AddToNotificationQueue(a,2)", "getInsertEmails(a)",
            "CreateAlarmAudit(3,emails,a.HistoryID,a.CurrentStateNo)",
            "AddActiveAlarmActionToActivity(a.HistoryID,getInsertEmails(a),0)", "getInsertEmails(a)" },
            Calls(body, ActionTargets));
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void SourceContract_StateWriteParametersMapDisplayToCurrentAndTargetToParserState(string file)
    {
        string source = ReadSource(file);
        string state = Method(source, "insertIntoCurrentAlarmNotification");
        Assert.Contains("repository.PrepareInsertIntoCurrentAlarmNotification(AlarmSystemID, CurentStateNo, ExpiryTime, DisplayStateNo, currentLoopNumber, processNext)", state);
        DateTime expiry = new(2026, 10, 1, 12, 34, 56, DateTimeKind.Utc);
        const int alarmSystemID = 11, nextStateNo = 23, displayStateNo = 24, currentLoopNumber = 25, processNext = 1;
        using var stateCommand = WorkflowRepository.BuildInsertIntoCurrentAlarmNotification(
            alarmSystemID, nextStateNo, expiry, displayStateNo, currentLoopNumber, processNext);
        Assert.Equal("ActiveAlarms_InsertIntoCNotificationState", stateCommand.CommandText);
        AssertParameter(stateCommand, "@AlarmSystemID", alarmSystemID);
        AssertParameter(stateCommand, "@CurrentStateNo", displayStateNo);
        AssertParameter(stateCommand, "@ParserStateNo", nextStateNo);
        AssertParameter(stateCommand, "@ExpiryTime", expiry);
        AssertParameter(stateCommand, "@ProcessNextStep", processNext);
        AssertParameter(stateCommand, "@CurrentLoopNumber", currentLoopNumber);
        string archive = Method(source, "insertIntoAlarmNotification");
        Assert.Contains("repository.PrepareInsertIntoAlarmNotification(AlarmSystemID, CurentStateNo, ExpiryTime, Action)", archive);
        using var archiveCommand = WorkflowRepository.BuildInsertIntoAlarmNotification(alarmSystemID, nextStateNo, expiry, "Delay");
        Assert.Equal("ActiveAlarms_InsertIntoAlarmNotification", archiveCommand.CommandText);
        AssertParameter(archiveCommand, "@AlarmSystemID", alarmSystemID);
        AssertParameter(archiveCommand, "@CurrentStateNo", nextStateNo);
        AssertParameter(archiveCommand, "@ExpiryTime", expiry);
        AssertParameter(archiveCommand, "@Action", "Delay");
        var a = new ActiveAlarm
        {
            SystemID = 31, HistoryID = 32, StateNo = 33, EmailAddresses = "officer@example.test",
            ReceivedDateTime = 1_700_000_000L, EventDateTime = 1_700_000_001L
        };
        const int insertType = 4;
        const string victimsEmails = "victim@example.test";
        foreach (string method in new[] { "AddToNotificationQueue", "insertNotificationQueueVictim" })
        {
            string queue = Method(source, method);
            bool officer = method == "AddToNotificationQueue";
            Assert.Contains(officer
                ? "repository.PrepareAddToNotificationQueue(a, insertType)"
                : "repository.PrepareInsertNotificationQueueVictim(a, insertType, victimsEmails, isVictimNotification)", queue);
            using var queueCommand = officer
                ? WorkflowRepository.BuildAddToNotificationQueue(a, insertType)
                : WorkflowRepository.BuildInsertNotificationQueueVictim(a, insertType, victimsEmails, true);
            Assert.Equal("ActiveAlarms_InsertIntoNotificationQueue", queueCommand.CommandText);
            AssertParameter(queueCommand, "@AlarmID", a.SystemID);
            AssertParameter(queueCommand, "@HistoryID", a.HistoryID);
            AssertParameter(queueCommand, "@InsertType", insertType);
            AssertParameter(queueCommand, "@MsgToAddress", officer ? a.EmailAddresses : victimsEmails);
            if (!officer)
                AssertParameter(queueCommand, "@IsVictimNotification", true);
        }
        Assert.Contains("repository.PrepareCreateAlarmAudit(type, action, historyID, StepNo)", Method(source, "CreateAlarmAudit"));
        using var auditCommand = WorkflowRepository.BuildCreateAlarmAudit(3, "audit action", a.HistoryID, displayStateNo);
        Assert.Equal("mcapp_CreateAudit", auditCommand.CommandText);
        AssertParameter(auditCommand, "@Type", 3);
        AssertParameter(auditCommand, "@Action", "audit action");
        AssertParameter(auditCommand, "@historyID", a.HistoryID);
        AssertParameter(auditCommand, "@stepno", displayStateNo);
        Assert.Contains("repository.PreparePushAlertToMcApp(a)", Method(source, "PushAlertToMcApp"));
        using var mcCommand = WorkflowRepository.BuildPushAlertToMcApp(a);
        Assert.Equal("ActiveAlarms_InsertIntoMCAPP", mcCommand.CommandText);
        AssertParameter(mcCommand, "@StepNo", a.StateNo);
        if (!IsSteps(file))
        {
            Assert.Contains("repository.PrepareUpdateParserActivty(systemID, configuration[platForm + \"ParserID\"])",
                Method(source, "updateParserActivty"));
            using var checkpointCommand = WorkflowRepository.BuildUpdateParserActivty(a.SystemID, "0022");
            Assert.Equal("ActiveAlarms_UpdateParserActivity", checkpointCommand.CommandText);
            AssertParameter(checkpointCommand, "@CurrSystemID", a.SystemID);
            AssertParameter(checkpointCommand, "@ParserID", 22);
        }
    }

    private static string[] ExpectedActionCalls(string file, int priority, int role)
    {
        string step = IsSteps(file) ? "a.CurrentStateNo" : "1";
        string Audit(int type, string text, string? auditStep = null) =>
            $"CreateAlarmAudit({type},{text},a.HistoryID,{auditStep ?? step})";
        string History(string text, int type) => $"AddActiveAlarmActionToActivity(a.HistoryID,{text},{type})";
        return priority switch
        {
            1 or 11 => [],
            2 => [Officer, "AddToNotificationQueue(a,3)", Audit(1, "emailAdresses"), History("a.EmailAddresses", 1)],
            3 or 6 or 14 => [Officer, McApp],
            4 => [Officer],
            5 => [Officer, "AddToNotificationQueue(a,1)", "getInsertEmails(a)", Audit(3, "autoPageEmails"), History("autoPageEmails", 0)],
            7 => [McApp, Officer, "AddToNotificationQueue(a,3)", Audit(IsSteps(file) ? 3 : 1, "emailAdresses7"), History("a.EmailAddresses", 1)],
            9 => [Officer, McApp, "AddToNotificationQueue(a,1)", "getInsertEmails(a)", Audit(3, "autoPageMcAppEmails"), History("autoPageMcAppEmails", 0)],
            12 when role == 1 => [McApp],
            12 => ["AddToNotificationQueue(a,3)", Audit(14, role == 2 ? "emails" : "txtMessages"), History("a.EmailAddresses", 1)],
            // Victim/client audits still use literal step 1 even in WorkFlowSteps.
            13 => ["insertNotificationQueueVictim(a,3,victimsMail,true)", Audit(14, "victimEmails", "1"), History("victimEmails", 1)],
            15 => ["getClientEmail(a)", "insertNotificationQueueVictim(a,3,clientMail)", Audit(14, "cMail", "1"), History("cMail", 1)],
            16 => ["getClientText(a)", "insertNotificationQueueVictim(a,4,clientText)", Audit(14, "cText", "1"), History("cText", 1)],
            17 => ["insertNotificationQueueVictim(a,4,victimsText,true)", Audit(14, "Msg", "1"), History("Msg", 1)],
            _ => throw new ArgumentOutOfRangeException(nameof(priority))
        };
    }

    private static bool IsSteps(string file) => file == "WorkFlowSteps.cs";

    private static string ReadSource(string file)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NotificationWorkflowService.slnx")))
                return File.ReadAllText(Path.Combine(directory.FullName, "NotificationWorkflowService", "Parser", file));
        }
        throw new DirectoryNotFoundException("Source-contract tests require the workspace sources and solution above AppContext.BaseDirectory.");
    }

    private static string Method(string source, string name)
    {
        var matches = Regex.Matches(CodeMask(source),
            @"\b(?:public|private|protected)\s+(?:void|bool|string|String)\s+" + Regex.Escape(name) + @"\s*\([^)]*\)\s*\{");
        Match signature = Assert.Single(matches.Cast<Match>());
        int open = signature.Index + signature.Length - 1;
        int close = MatchingDelimiter(CodeMask(source), open, '{', '}');
        return source[(open + 1)..close];
    }

    private static Dictionary<string, string> PrioritySegments(string parse) => SwitchSegments(parse, "a.Priority");

    private static (int Open, int Close) SwitchBounds(string source, string expression)
    {
        Match match = Regex.Match(CodeMask(source), @"\bswitch\s*\(\s*" + Regex.Escape(expression) + @"\s*\)\s*\{");
        Assert.True(match.Success, $"Missing switch ({expression}).");
        int open = match.Index + match.Length - 1;
        return (open, MatchingDelimiter(CodeMask(source), open, '{', '}'));
    }

    private static Dictionary<string, string> SwitchSegments(string source, string expression)
    {
        var (open, close) = SwitchBounds(source, expression);
        string body = source[(open + 1)..close];
        string code = CodeMask(body);
        var labels = Regex.Matches(code, @"\b(?:case\s+(?<label>\d+)|(?<label>default))\s*:")
            .Cast<Match>().Where(match => BraceDepth(code, match.Index) == 0).ToArray();
        var result = new Dictionary<string, string>();
        for (int i = 0; i < labels.Length; i++)
            result.Add(labels[i].Groups["label"].Value,
                body[(labels[i].Index + labels[i].Length)..(i + 1 < labels.Length ? labels[i + 1].Index : body.Length)]);
        return result;
    }

    private static int BraceDepth(string code, int end) => code[..end].Count(c => c == '{') - code[..end].Count(c => c == '}');

    private static string[] Calls(string source, string targets)
    {
        string code = CodeMask(source);
        return Regex.Matches(code, @"(?<![\w.])(?<name>" + targets + @")\s*\(")
            .Cast<Match>().Select(match =>
            {
                int open = match.Index + match.Length - 1;
                int close = MatchingDelimiter(code, open, '(', ')');
                return Regex.Replace(source[match.Index..(close + 1)], @"\s+", "");
            }).ToArray();
    }

    private static IEnumerable<string> CatchBodies(string source)
    {
        string code = CodeMask(source);
        foreach (Match match in Regex.Matches(code, @"\bcatch\s*\(Exception\s+\w+\)\s*\{"))
        {
            int open = match.Index + match.Length - 1;
            yield return source[(open + 1)..MatchingDelimiter(code, open, '{', '}')];
        }
    }

    private static string CatchContaining(string source, string message) =>
        Assert.Single(CatchBodies(source), body => body.Contains(message, StringComparison.Ordinal));

    private static int MatchingDelimiter(string code, int open, char left, char right)
    {
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == left) depth++;
            if (code[i] == right && --depth == 0) return i;
        }
        throw new InvalidDataException($"Unbalanced {left}{right} in source contract.");
    }

    // Offset-preserving lexical mask for the existing C# sources: ignores comments,
    // ordinary/verbatim strings and chars, including braces in logging templates.
    // This is intentionally not a C# semantic model or a general raw-string parser.
    private static string CodeMask(string source) => Regex.Replace(source,
        "//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'",
        match => new string(match.Value.Select(c => c is '\r' or '\n' ? c : ' ').ToArray()));

    private static void AssertOrdered(string source, params string[] fragments)
    {
        int position = 0;
        foreach (string fragment in fragments)
        {
            int found = source.IndexOf(fragment, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Missing or out-of-order source fragment: {fragment}");
            position = found + fragment.Length;
        }
    }

    private static void AssertParameter(SqlCommand command, string parameter, object value)
    {
        Assert.Null(command.Connection);
        Assert.Equal(value, command.Parameters[parameter].Value);
    }
}