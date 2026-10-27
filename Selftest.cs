using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DevBreak;

/// <summary>
/// The pure rules of the Sessions watcher, run by <c>DevBreak.exe -selftest</c>: each case one line, PASS or FAIL, in
/// selftest.txt next to the exe; the exit code is 0 only when all pass. The app has no test project, and these rules
/// (what counts as stuck, as verified, as a lesson) are the part a UI run cannot cover case by case.
/// </summary>
public static class Selftest
{
    private static readonly List<string> Lines = [];
    private static bool _ok = true;

    public static int Run(bool real = false)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), "started");
        Lines.Clear();
        _ok = true;
        try
        {
            TurnLogCases();
            StatsCases();
            WaitCases();
            FlagCases();
            QualityCases();
            LessonCases();
            LearnCases();
            ReviewFixCases();
            ContextCases();
            Check(CliSetup.CompactWindowOf("{\"autoCompactWindow\": 200000}") == "200000", "Compact window: read from the user's settings");
            Check(CliSetup.CompactWindowOf("{\"env\": {}}") == null, "Compact window: none set -> none passed");
            Check(CliSetup.CompactWindowOf("not json") == null, "Compact window: unreadable settings -> none passed");
            Check(Watch.IsQuickStep("commit and push"), "Quick step: \"commit and push\"");
            Check(Watch.IsQuickStep("rebuild and restart dev break"), "Quick step: \"rebuild and restart dev break\"");
            Check(!Watch.IsQuickStep("design a watcher that coaches long turns"), "Quick step: a design ask is not one");
            Check(!Watch.IsQuickStep("run the migration, then rewrite the three services that read the old table and add tests for each of them"),
                "Quick step: a long ask with 'run' in it is not one (over 80 characters)");
        }
        catch (Exception ex)
        {
            Check(false, "threw: " + ex);
        }
        if (real) RealScan();
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), [.. Lines, _ok ? "ALL PASS" : "FAILED"]);
        return _ok ? 0 : 1;
    }

    private static void Check(bool pass, string what)
    {
        Lines.Add((pass ? "PASS  " : "FAIL  ") + what);
        _ok &= pass;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // a tool call and its result, as the stream-json lines carry them
    internal static JsonElement ToolUse(string id, string tool, object input) => Json(JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new { content = new object[] { new { type = "tool_use", id, name = tool, input } } },
    }));

    internal static JsonElement ToolResult(string id, bool error, string text) => Json(JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { content = new object[] { new { type = "tool_result", tool_use_id = id, is_error = error, content = text } } },
    }));

    private static void TurnLogCases()
    {
        var log = new TurnLog("run the tests");
        log.Read(ToolUse("t1", "Bash", new { command = "cd x && npm test --a" }));
        Check(log.Running?.Id == "t1", "TurnLog: a tool_use is the running step");
        log.Read(ToolResult("t1", true, "1 failed"));
        Check(log.Steps.Count == 1 && log.Steps[0].Error && log.Steps[0].End != null && log.Running == null,
            "TurnLog: its error result ends the step as failed");
        Check(log.Steps[0].Key == "npm test", "TurnLog: the shell key skips a leading cd (\"npm test\")");
        Check(TurnLog.KeyOf("cd a && gcloud run deploy x") == "gcloud run", "TurnLog.KeyOf: cd a && gcloud run deploy x -> gcloud run");
        log.Read(ToolUse("t2", "Edit", new { file_path = @"C:\r\a.cs", old_string = "x", new_string = "a\nb\nc" }));
        log.Read(ToolResult("t2", false, "ok"));
        Check(log.Changed.Contains(@"C:\r\a.cs") && log.LinesChanged == 3, "TurnLog: an Edit counts its file and its new lines (3)");
        Check(TurnLog.KeyOf("Read") == "Read", "TurnLog.KeyOf: one word stays one word");
    }

    // "-selftest real": the user's own folder, read once, timed - numbers to compare with the audit, not a pass/fail
    private static void RealScan()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SessionStats.Data data = SessionStats.Scan(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"), null);
        Lines.Add($"real: {data.Files.Count} files, {data.Files.Count(f => f.Counts)} counted, {watch.Elapsed.TotalSeconds:0} s");
        foreach (SessionStats.WeekStats w in data.Weeks(5))
        {
            Lines.Add($"real: week {w.Start:yyyy-MM-dd} active {w.ActiveH:0.0} h, wait {w.WaitH:0.0}, tools {w.ToolH:0.0}, model {w.ModelH:0.0}, you {w.UserH:0.0}, calls {w.Calls}, failed {w.Failed}, interrupts {w.Interrupts}, corrections {w.Corrections}");
        }
        Lines.Add($"real: families in 3+ sessions: {data.Families(3).Count}");
    }

    // a log of finished calls: (tool, summary, error, result), a second apart, starting 10 minutes ago
    private static TurnLog LogOf(params (string Tool, string Summary, bool Error, string Result)[] calls)
    {
        var log = new TurnLog("fix the build");
        DateTime t = DateTime.Now.AddMinutes(-10);
        int n = 0;
        foreach ((string tool, string summary, bool error, string result) in calls)
        {
            n++;
            log.Steps.Add(new TurnStep("s" + n, tool, summary, tool, t, false) { End = t.AddSeconds(1), Error = error, Result = result });
            t = t.AddSeconds(2);
        }
        return log;
    }

    // the footer says what each step re-reads, in tokens; past 150k (or 80% full) it turns coral
    private static void ContextCases()
    {
        Check(Watch.ContextLabel(511_400, 51) == ("511k context", true), "Context: 511,400 tokens of 1M -> \"511k context\", heavy (51% hid it)");
        Check(Watch.ContextLabel(26_834, 13) == ("27k context", false), "Context: 26,834 tokens -> \"27k context\", light");
        Check(Watch.ContextLabel(149_999, 74).Heavy == false, "Context: just under 150k is light");
        Check(Watch.ContextLabel(150_000, 75).Heavy, "Context: 150k is heavy");
        Check(Watch.ContextLabel(120_000, 80).Heavy, "Context: 80% full is heavy even under 150k");
    }

    private static void FlagCases()
    {
        DateTime now = DateTime.Now;
        var failing = ("Bash", "npm run build", true, "error TS2304");
        Check(Watch.Flag(LogOf(failing, failing, failing), now.AddMinutes(-9.9), false)?.StartsWith("the same Bash call failed 3 times in a row") == true,
            "Flag: the same call failing 3 times");
        Check(Watch.Flag(LogOf(failing, ("Read", "a.cs", false, "x"), failing), now.AddMinutes(-9.9), false) == null, "Flag: not 3 in a row is not stuck");
        var same = ("Bash", "git status", false, "clean");
        Check(Watch.Flag(LogOf(same, same, same, same), now.AddMinutes(-9.9), false)?.StartsWith("it ran the same Bash call 4 times in a row") == true,
            "Flag: the same call 4 times");
        Check(Watch.Flag(LogOf(("Bash", "a", true, "e1"), ("Read", "b", false, "r1"), ("Bash", "c", true, "e2"), ("Read", "d", false, "r2"),
                ("Bash", "e", true, "e3"), ("Read", "f", false, "r3"), ("Bash", "g", true, "e4"), ("Bash", "h", true, "e5")), now.AddMinutes(-9.9), false)
            ?.StartsWith("5 of its last 10 tool calls failed") == true, "Flag: 5 failed of the last 10");
        Check(Watch.Flag(LogOf(("PowerShell", "head -5 x", true, "head : The term 'head' is not recognized as the name of a cmdlet")), now.AddMinutes(-9.9), false)
            ?.StartsWith("a command written for another shell") == true, "Flag: a wrong-shell-dialect error");
        Check(Watch.Flag(LogOf(("Read", "a.cs", false, "x")), now, false)?.StartsWith("no new step for 5 minutes") == true, "Flag: silent 5 minutes");
        Check(Watch.Flag(LogOf(("Read", "a.cs", false, "x")), now, true) == null, "Flag: silent while a command runs is waiting, not stuck");
        var again = Enumerable.Range(0, 13).Select(i => ("Grep", "pattern " + i, false, "No matches found")).ToArray();
        Check(Watch.Flag(LogOf(again), now.AddMinutes(-9.9), false)?.StartsWith("12 steps without progress") == true,
            "Flag: 12 steps after the last new result (no-progress counter)");
        var fresh = Enumerable.Range(0, 12).Select(i => ("Read", "f" + i, false, "content " + i)).ToArray();
        Check(Watch.Flag(LogOf(fresh), now.AddMinutes(-9.9), false) == null, "Flag: 12 reads of new content is progress");

        Check(SessionPane.Drifting("on track") == null, "AI check: \"on track\" raises nothing");
        Check(SessionPane.Drifting("drifting: it edits the wrong file\nnudge: edit src/a.cs instead") is ("it edits the wrong file", "edit src/a.cs instead"),
            "AI check: drifting with its why and nudge");
        Check(SessionPane.Drifting("Sure! Here is my analysis: drifting: maybe") == null, "AI check: an answer out of shape is ignored");
        Check(SessionPane.Drifting("Drifting: loops on npm install") is ("loops on npm install", null), "AI check: drifting without a nudge line");

        TurnLog slow = LogOf(("Read", "a.cs", false, "x"));
        slow.Steps.Add(new TurnStep("w", "Bash", "npm run deploy", "npm run", now.AddMinutes(-6), false)
            { Command = "npm run deploy", End = now.AddMinutes(-3), Result = "ok" });
        string? diagnosis = Watch.Diagnosis(slow, TimeSpan.FromMinutes(10));
        Check(diagnosis != null && diagnosis.Contains("npm run deploy ran 3 min in the foreground - run it in the background next time"),
            "Diagnosis: a 3-minute foreground command - got: " + diagnosis);
        Check(Watch.Diagnosis(slow, TimeSpan.FromSeconds(90)) == null, "Diagnosis: none for a turn under 2 minutes");
    }

    private static void QualityCases()
    {
        TurnLog edited = LogOf(("Read", "a.py", false, "x"));
        edited.Changed.Add(@"C:
.py");
        edited.Steps.Add(new TurnStep("e", "Write", "a.py", "Write", DateTime.Now, false) { End = DateTime.Now, Result = "ok" });
        Check(Watch.Unverified(edited, "Done - a.py prints 1.") is { } nudge && nudge.Contains("pytest"),
            "Unverified: a .py written, \"Done\", nothing run after - nudge names pytest");
        TurnLog checkedLog = LogOf(("Read", "a.py", false, "x"));
        checkedLog.Changed.Add(@"C:
.cs");
        checkedLog.Steps.Add(new TurnStep("e", "Edit", "a.cs", "Edit", DateTime.Now, false) { End = DateTime.Now, Result = "ok" });
        checkedLog.Steps.Add(new TurnStep("b", "Bash", "dotnet build", "dotnet build", DateTime.Now, false) { Command = "cd r && dotnet build -c Release", End = DateTime.Now, Result = "ok" });
        Check(Watch.Unverified(checkedLog, "Fixed, builds clean.") == null, "Unverified: a dotnet build after the edit is a check");
        Check(Watch.Unverified(edited, "I wrote a.py; I have not run it yet.") == null, "Unverified: no completion claim, no flag");
        Check(Watch.Unverified(LogOf(("Read", "a.py", false, "x")), "Done.") == null, "Unverified: nothing changed, no flag");

        string raw = "[{\"criterion\":\"claims match the record\",\"verdict\":\"violated\",\"evidence\":\"says tests pass, ran none\"}," +
                     "{\"criterion\":\"unknowns as facts\",\"verdict\":\"ok\",\"evidence\":\"\"}]";
        Check(Rubric.Parse(raw) is [("claims match the record", "says tests pass, ran none")], "Rubric.Parse: the violated criterion with its evidence");
        Check(Rubric.Parse("```json\n" + raw + "\n```") is { Count: 1 }, "Rubric.Parse: a fenced array is read");
        Check(Rubric.Parse("The turn looks fine to me.") == null, "Rubric.Parse: prose is not a verdict (null)");
        Check(Rubric.Parse("[]") is { Count: 0 }, "Rubric.Parse: an empty array is clean");
    }

    private static void LessonCases()
    {
        TurnLog retried = LogOf(("Bash", "npm i", true, "e1"), ("Bash", "npm ci", true, "e2"), ("Bash", "npm install --legacy", false, "ok"));
        Check(SessionLessons.ShouldPropose(retried, null, false), "Lessons: 2 failed calls of a tool, then it worked - propose");
        Check(!SessionLessons.ShouldPropose(LogOf(("Bash", "a", true, "e"), ("Read", "b", false, "x")), null, false), "Lessons: one failure, never fixed - no");
        Check(SessionLessons.ShouldPropose(LogOf(("Read", "b", false, "x")), "no, use the other config file", false), "Lessons: the next message corrects - propose");
        Check(SessionLessons.ShouldPropose(LogOf(("Read", "b", false, "x")), "thanks", true), "Lessons: stopped and asked again - propose");
        Check(!SessionLessons.ShouldPropose(LogOf(("Read", "b", false, "x")), "now add a test", false), "Lessons: a normal next message - no");

        List<FactoryLesson> parsed = SessionLessons.Parse(
            "Here you go:\n[{\"kind\":\"skill\",\"name\":\"Deploy Preview!\",\"description\":\"d\",\"content\":\"c\"},{\"kind\":\"weird\",\"name\":\"x\",\"description\":\"d\",\"content\":\"c\"}]");
        Check(parsed.Count == 1 && parsed[0].Kind == "skill" && parsed[0].Name == "deploy-preview", "Lessons.Parse: a kind it knows, its name made a slug; others dropped");
        Check(SessionLessons.Parse("No lessons this time.").Count == 0, "Lessons.Parse: prose is no lessons");

        string home = Path.Combine(Path.GetTempPath(), "devbreak-home-" + Guid.NewGuid().ToString("N")[..8]);
        string memory = Path.Combine(home, "memory");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", "CLAUDE.md"), "# my rules\n- keep it\n");
        try
        {
            foreach (string kind in new[] { "memory", "skill", "rule" })
            {
                var lesson = new FactoryLesson { Kind = kind, Name = "test-" + kind, Description = "a " + kind, Content = "Do the thing the proven way." };
                string file = SessionLessons.Save(lesson, memory, home);
                bool written = File.Exists(file) && File.ReadAllText(file).Contains("proven way");
                SessionLessons.Undo(lesson);
                bool gone = kind == "rule"
                    ? File.ReadAllText(file) == "# my rules\n- keep it\n"
                    : !File.Exists(file) && (kind != "memory" || !File.ReadAllText(Path.Combine(memory, "MEMORY.md")).Contains("test-memory"));
                Check(written && gone, $"Lessons: save then undo a {kind} leaves things as they were ({file})");
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // what the final review found, pinned
    private static void ReviewFixCases()
    {
        DateTime early = DateTime.Now.AddMinutes(-9.9);
        var twelve = Enumerable.Range(0, 13).Select(i => ("Grep", "p" + i, false, "No matches found")).ToArray();
        var thirteen = Enumerable.Range(0, 14).Select(i => ("Grep", "p" + i, false, "No matches found")).ToArray();
        Check(Watch.FlagRule(LogOf(twelve), early, false)?.Rule is "no-progress" && Watch.FlagRule(LogOf(thirteen), early, false)?.Rule is "no-progress",
            "Review fix: the no-progress flag keeps one rule id as its count grows (Dismiss holds)");
        var lesson = new FactoryLesson { Kind = "memory", Name = "x", Content = "y", Status = "pending" };
        Check(SessionLessons.TryBeginSave(lesson) && !SessionLessons.TryBeginSave(lesson), "Review fix: a second Save while the first runs is refused");
        Check(Rubric.Parse("[{\"criterion\":\"a\",\"verdict\":\"fail\",\"evidence\":\"e\"}]") == null,
            "Review fix: a verdict that is neither ok nor violated makes the grade unreadable, not clean");
    }

    private static void LearnCases()
    {
        var families = new List<SessionStats.CommandFamily>
        {
            new("gcloud run", 20, 90, 9, [], []),
            new("docker compose", 6, 30, 2, [], []),
            new("python -", 24, 600, 40, [], []),
        };
        string[] skills = ["cloud-run-deploy: Deploy a service to Cloud Run with gcloud run deploy", "pdf: work with PDF files"];
        List<SessionStats.CommandFamily> left = SessionStats.Uncovered(families, skills);
        Check(left.Select(f => f.Key).SequenceEqual(["docker compose"]),
            "Learn: a family a skill already names (gcloud run) and an ad-hoc one (python -) are left out - got: " + string.Join(", ", left.Select(f => f.Key)));
    }

    private static void WaitCases()
    {
        DateTime now = DateTime.Now;
        var step = new TurnStep("w1", "Bash", "", "npm run", now.AddSeconds(-200), false) { Command = "cd app && npm run build -- --prod" };
        Check(Watch.WaitText(step, now, TimeSpan.FromMinutes(6)) == "⧗ waiting on npm run build -- --prod · 3:20 · usually ~6 min",
            "Watch.WaitText: command without its cd, m:ss, usually ~6 min - got: " + Watch.WaitText(step, now, TimeSpan.FromMinutes(6)));
        Check(Watch.WaitText(step, now, null) == "⧗ waiting on npm run build -- --prod · 3:20", "Watch.WaitText: no 'usually' without history");
        Check(Watch.WaitText(step, now, TimeSpan.FromSeconds(40)) == "⧗ waiting on npm run build -- --prod · 3:20 · usually ~1 min",
            "Watch.WaitText: under a minute reads ~1 min");
    }

    private static void StatsCases()
    {
        string root = Path.Combine(Path.GetTempPath(), "devbreak-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        string project = Path.Combine(root, "C--some-folder");
        Directory.CreateDirectory(project);
        DateTime t0 = DateTime.UtcNow.AddHours(-2);
        string At(int seconds) => t0.AddSeconds(seconds).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        string[] lines =
        [
            $$$"""{"type":"user","entrypoint":"cli","timestamp":"{{{At(0)}}}","message":{"role":"user","content":"make the build faster"}}""",
            $$$"""{"type":"assistant","entrypoint":"cli","timestamp":"{{{At(5)}}}","message":{"content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"cd x && sleep 5"}}]}}""",
            $$$"""{"type":"user","entrypoint":"cli","timestamp":"{{{At(15)}}}","message":{"content":[{"type":"tool_result","tool_use_id":"b1","is_error":true,"content":"x"}]}}""",
            $$$"""{"type":"assistant","entrypoint":"cli","timestamp":"{{{At(20)}}}","message":{"content":[{"type":"text","text":"done"}]}}""",
            $$$"""{"type":"system","subtype":"turn_duration","durationMs":60000,"entrypoint":"cli","timestamp":"{{{At(21)}}}"}""",
            $$$"""{"type":"user","entrypoint":"cli","timestamp":"{{{At(30)}}}","message":{"role":"user","content":"no, not like that"}}""",
            """{"type":"assistant","timestamp":"2026-""",
        ];
        File.WriteAllLines(Path.Combine(project, "11111111-2222-3333-4444-555555555555.jsonl"), lines);
        try
        {
            SessionStats.Data data = SessionStats.Scan(root, null);
            List<SessionStats.WeekStats> weeks = data.Weeks(1);
            SessionStats.WeekStats week = weeks.Count > 0 ? weeks[0] : new SessionStats.WeekStats(DateTime.MinValue);
            Check(Math.Abs(week.ActiveH * 3600 - 60) < 0.5, $"SessionStats: active time is the turn_duration (60 s, got {week.ActiveH * 3600:0.#})");
            Check(Math.Abs(week.WaitH * 3600 - 10) < 0.5, $"SessionStats: a sleep command's 10 s is waiting (got {week.WaitH * 3600:0.#})");
            Check(week.Calls == 1 && week.Failed == 1, "SessionStats: one call, one failed");
            Check(week.Corrections == 1, "SessionStats: \"no, not like that\" is a correction");
            Check(data.Usual("sleep 5") == null, "SessionStats: no 'usually' from fewer than 3 samples");
        }
        catch (Exception ex)
        {
            Check(false, "SessionStats threw on a half-written last line: " + ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Check(SessionStats.Correction.IsMatch("no, use the other file"), "Correction: \"no, use the other file\"");
        Check(!SessionStats.Correction.IsMatch("add a button to the bar"), "Correction: \"add a button to the bar\" is not one");
    }
}
