using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using NotificationWorkflowService.Entity;
using WorkflowRepository = NotificationWorkflowService.Repository.Repository;
using Xunit;

namespace NotificationWorkflowService.Tests;

/// <summary>
/// Parser shell and helper SOURCE CONTRACT tests, not runtime delivery tests.
/// Priority behavior is covered by WorkflowActionExecutorTests instead of lexical switches.
/// The parameter mapping
/// test also calls pure internal repository builders to inspect unconnected SqlCommands;
/// no SQL is executed. Ordered calls below are direct source occurrences, not transitive
/// writes, successful commits, or proof that a conditional branch executes.
/// </summary>
public class WorkflowActionSourceCharacterizationTests
{
    private static readonly string[] ParserFiles =
        ["WorkFlowInitiator.cs", "ParserCommon.cs", "WorkFlowSteps.cs"];
    private const string ActionTargets =
        "SendNotificationsToOfficersInSameGroup|PushAlertToMcApp|AddToNotificationQueue|" +
        "CreateAlarmAudit|AddActiveAlarmActionToActivity|insertNotificationQueueVictim|" +
        "getClientEmail|getClientText|getInsertEmails";

    public static IEnumerable<object[]> Parsers() => ParserFiles.Select(file => new object[] { file });

    [Fact]
    public void SourceContract_CompatibilityWrapperHasOnlyThreeForwardingConstructorsAndNoExecutableOverrides()
    {
        string source = ReadRawSource("WorkFlowInitiator.cs");
        string code = CodeMask(source);
        Assert.Matches(@"\binternal\s+class\s+WorkFlowInitiator\s*:\s*WorkFlowCommon", code);
        Assert.Equal(3, Regex.Matches(code, @"\bpublic\s+WorkFlowInitiator\s*\(").Count);
        Assert.Equal(3, Regex.Matches(code, @"\)\s*:\s*(?:this|base)\s*\([^{}]*\)\s*\{\s*\}").Count);
        Assert.Contains(": this(logger, configuration, notificationService, new Repository(configuration))", source);
        Assert.Contains(": base((ILogger)logger, configuration, notificationService, repository)", source);
        Assert.Contains(": base((ILogger)logger, configuration, notificationService, repository, noteService)", source);
        Assert.DoesNotMatch(@"\b(?:override|virtual)\b", code);
        Assert.DoesNotMatch(@"\b(?:public|private|protected|internal)\s+(?:readonly\s+)?[\w<>?]+\s+\w+\s*(?:\(|[=;])", code);
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void SourceContract_FinalAuditThenArchiveThenConditionalStateWritesThenNormalCheckpoint(string file)
    {
        string parse = Method(ReadSource(file), "parseAlarms");
        int execute = parse.IndexOf("WorkflowActionResult result = await WorkflowActionExecutor.ExecuteAsync", StringComparison.Ordinal);
        Assert.True(execute >= 0);
        bool steps = IsSteps(file);
        string step = steps ? "a.CurrentStateNo" : "1";
        Assert.Equal(steps ? Array.Empty<string>() : new[] { "CreateAlarmAuditAsync(15,\"\",a.HistoryID,1,cancellationToken)" },
            Calls(parse[..execute], ActionTargets));

        Assert.Equal(new[] { $"WorkflowActionExecutor.ExecuteAsync(a,newWorkflowActionContext(WorkflowActionMode.{(steps ? "Step" : "Normal")},platForm,log,newActionOperations(this),RoleActionMapping,roles,victims),cancellationToken)" },
            Calls(parse, "WorkflowActionExecutor\\.Execute"));
        Assert.DoesNotMatch(@"\bswitch\s*\(", CodeMask(parse));
        AssertOrdered(parse, "sb.Append(\"Action as per the profile assigned:\");",
            "await WorkflowActionExecutor.ExecuteAsync", "insert = result.Insert;", "sb.Append(result.Summary);",
            "await CreateAlarmAuditAsync(14, sb.ToString()", "await insertIntoAlarmNotificationAsync", "if (a.NextStateNo != -1 && insert)");
        if (steps)
        {
            Assert.Empty(Calls(parse[..execute], "PushAlertsToVictims|PushNotificationToVictim|ClearMcAppAlarm"));
        }
        else
        {
            AssertOrdered(parse, "curr = a;", "await PushAlertsToVictimsAsync(a, cancellationToken)", "await PushNotificationToVictimAsync(a, cancellationToken)",
                "await CreateAlarmAuditAsync(15,", "if (a.IsAlarmClearingEnabled)", "await ClearMcAppAlarmAsync(clearEvents, a.ClientID, cancellationToken)",
                "await WorkflowActionExecutor.ExecuteAsync");
            foreach (string message in new[] { "PushAlertsToVictims Error", "PushNotificationToVictim Error" })
            {
                string local = CatchContaining(parse, message);
                Assert.Contains("log.LogError", local);
                Assert.DoesNotMatch(@"\b(?:throw|break|return|continue)\b", CodeMask(local));
            }
        }

        string tail = parse[parse.IndexOf("sb.Append(result.Summary);", StringComparison.Ordinal)..];
        string expiry = steps ? "DateTime.UtcNow.AddMinutes(a.StateTime)" : "ExpiryTimeApplied";
        string display = steps ? "a.CurrentStateNo" : "a.StateNo+1";
        string loop = steps ? "a.CurrentLoopNumber" : "0";
        string fallback = steps ? "a.CurrentStateNo+1" : "a.StateNo+1";
        var expected = new List<string>
        {
            $"CreateAlarmAuditAsync(14,sb.ToString(),a.HistoryID,{step},cancellationToken)",
            $"insertIntoAlarmNotificationAsync(a.SystemID,{step},{expiry},PriorityMapping[a.Priority],cancellationToken)",
            $"insertIntoCurrentAlarmNotificationAsync(a.SystemID,a.NextStateNo,{expiry},{display},{loop},a.ProcessNextStep,cancellationToken)",
            $"insertIntoCurrentAlarmNotificationAsync(a.SystemID,{fallback},{expiry},{display},0,1,cancellationToken)"
        };
        if (!steps)
            expected.Add("updateParserActivtyAsync(sysID,cancellationToken)");
        Assert.Equal(expected, Calls(tail,
            "CreateAlarmAudit|insertIntoAlarmNotification|insertIntoCurrentAlarmNotification|updateParserActivty"));
        Assert.Contains("if (a.NextStateNo != -1 && insert)", tail);
        Assert.Contains("else if (insert)", tail);
        if (!steps)
        {
            Assert.Contains("int? sysID = null;", parse);
            AssertOrdered(tail, "await insertIntoCurrentAlarmNotificationAsync", "sysID = a.SystemID;",
                "Error processing alarm", "if (sysID != null)", "await updateParserActivtyAsync(sysID, cancellationToken)");
        }
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void SourceContract_ExpiryAndExecutorInsertResultRemainInShell(string file)
    {
        string parse = Method(ReadSource(file), "parseAlarms");
        Assert.Matches(@"\b(?:bool|Boolean)\s+insert\s*=\s*true\s*;", parse);
        Assert.Single(Regex.Matches(CodeMask(parse), @"\binsert\s*=\s*result\.Insert\s*;").Cast<Match>());
        Assert.DoesNotMatch(@"\binsert\s*=\s*false\s*;", CodeMask(parse));
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
    public void SourceContract_EmailJoinIsConditionalAndHasItsOwnQueueAuditHistorySequence(string file)
    {
        string body = Method(ReadSource(file), "SendNotificationsToOfficersInSameGroup");
        Assert.Contains("if (a.EmailJoin == 1)", body);
        Assert.Contains("if (await AddToNotificationQueueAsync(a, 2, cancellationToken).ConfigureAwait(false))", body);
        // Lexical direct-call order: nested getInsertEmails is NOT an extra history write.
        Assert.Equal(new[] { "AddToNotificationQueueAsync(a,2,cancellationToken)", "getInsertEmailsAsync(a,cancellationToken)",
            "CreateAlarmAuditAsync(3,emails,a.HistoryID,a.CurrentStateNo,cancellationToken)",
            "AddActiveAlarmActionToActivityAsync(a.HistoryID,getInsertEmailsAsync(a,cancellationToken),0,cancellationToken)", "getInsertEmailsAsync(a,cancellationToken)" },
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

    private static bool IsSteps(string file) => file == "WorkFlowSteps.cs";

    // WorkFlowInitiator inherits the normal bodies; keep every normal source
    // assertion active, and inspect the wrapper itself separately above.
    private static string ReadSource(string file) =>
        ReadRawSource(file == "WorkFlowInitiator.cs" ? "ParserCommon.cs" : file);

    private static string ReadRawSource(string file)
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
            @"\b(?:public|private|protected)\s+async\s+Task(?:\s*<\s*(?:bool|int|string|String)\s*>)?\s+" +
            Regex.Escape(name) + @"Async\s*\([^)]*\)\s*\{");
        Match signature = Assert.Single(matches.Cast<Match>());
        int open = signature.Index + signature.Length - 1;
        int close = MatchingDelimiter(CodeMask(source), open, '{', '}');
        return source[(open + 1)..close];
    }

    private static string[] Calls(string source, string targets)
    {
        string code = CodeMask(source);
        return Regex.Matches(code, @"(?<![\w.])(?<name>" + targets + @")(?:Async)?\s*\(")
            .Cast<Match>().Select(match =>
            {
            // Every direct I/O call (including nested reads) must actually be awaited.
            Assert.Matches(@"\bawait\s*$", code[..match.Index]);
                int open = match.Index + match.Length - 1;
                int close = MatchingDelimiter(code, open, '(', ')');
            string call = source[match.Index..(close + 1)];
            // Remove only await/configuration syntax inside arguments; retain async
            // method names and final tokens so exact mode/argument contracts stay strict.
            call = Regex.Replace(call, @"\bawait\s+", "");
            call = call.Replace(".ConfigureAwait(false)", "", StringComparison.Ordinal);
            return Regex.Replace(call, @"\s+", "");
            }).ToArray();
    }

    private static IEnumerable<string> CatchBodies(string source)
    {
        string code = CodeMask(source);
        // Cancellation catches intentionally precede these general legacy catches.
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