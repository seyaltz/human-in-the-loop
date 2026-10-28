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
            ResendCases();
            DigestCases();
            SteerCases();
            HandOffCases();
            FreshCases();
            BackgroundCases();
            FlagCases();
            QualityCases();
            LessonCases();
            LearnCases();
            ReviewFixCases();
            ContextCases();
            TelemetryCases();
            JudgeCases();
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

    // one devbreak.log line per finished turn: what it did, what it cost, what Dev break spent watching it
    private static void TelemetryCases()
    {
        TurnLog log = LogOf(("Bash", "npm test", true, "e"), ("Read", "a.cs", false, "x"));
        log.Read(ToolUse("w1", "Edit", new { file_path = @"C:\r\a.cs", old_string = "a", new_string = "1\n2\n3" }));
        Check(Watch.TurnSummary(log, 324, 1.234, 129_354, 167_000, 6) ==
              "3 steps, 1 failed, 1 file / 3 lines, 5.4 min, $1.23, context 129k (compacts at 167k), 6 ai checks",
            "Telemetry: a turn's summary line");
        Check(Watch.TurnSummary(new TurnLog("q"), 2, 0, 0, 0, 0) == "0 steps, 0 failed, 0 files / 0 lines, 0.0 min, $0.00, context ?, 0 ai checks",
            "Telemetry: an empty turn, context unknown");
    }

    // the grade is only as good as its record: a judge shown "ok" for every step flagged a right answer's numbers 5
    // times; a turn waiting on a background task is not over; a session's cost total can be far back in a big file
    private static void JudgeCases()
    {
        var log = new TurnLog("recheck the map page");
        log.Read(ToolUse("r1", "mcp__playwright__browser_run_code_unsafe", new { code = "open the map page" }));
        log.Read(ToolResult("r1", false, new string('.', 400) + "39 destinations, from $138"));
        string prompt = Rubric.Prompt(log, "The page shows 39 items from $138.");
        Check(prompt.Contains("39 destinations, from $138"), "Judge: a step's result is in the record, not just \"ok\"");
        Check(prompt.Contains("open the map page"), "Judge: a step with a blank summary shows its input");

        var finished = new TurnLog("restart") { Answer = "Restarted, 12/12 up." };
        for (int i = 0; i < SessionPane.GradeFrom; i++) finished.Read(ToolUse("g" + i, "Read", new { file_path = "f" + i }));
        Check(SessionPane.Gradable(finished, 0), "Judge: a finished turn is graded");
        Check(!SessionPane.Gradable(finished, 1), "Judge: a turn with a background task still running is not graded yet");

        string file = Path.Combine(Path.GetTempPath(), "devbreak-cost-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(file, "{\"type\":\"cost-state\",\"totalCostUSD\":7.53}\n" + string.Concat(Enumerable.Repeat("{\"type\":\"user\",\"pad\":\"" + new string('x', 1000) + "\"}\n", 400)));
        try
        {
            Check(Math.Abs(ClaudeSessions.LastCostIn(file) - 7.53) < 1e-9, "Cost: the session's total is found 400 KB before the end");
        }
        finally
        {
            File.Delete(file);
        }
    }

    // 4 different Playwright scripts: the tool keeps its script in "code", which the summary does not show (it is blank),
    // so only the whole input tells them apart - the case that flagged real progress as stuck
    private static void SameInputCases()
    {
        string[] scripts = ["open the deal link", "close the other tabs, go to the page", "take a screenshot", "type into the chat box"];
        TurnLog different = new("check the map page"), same = new("check the map page");
        for (int i = 0; i < 4; i++)
        {
            different.Read(ToolUse("d" + i, "mcp__playwright__browser_run_code_unsafe", new { code = scripts[i] }));
            different.Read(ToolResult("d" + i, false, "ok " + i));
            same.Read(ToolUse("s" + i, "mcp__playwright__browser_run_code_unsafe", new { code = scripts[0] }));
            same.Read(ToolResult("s" + i, false, "ok"));
        }
        Check(Watch.FlagRule(different, DateTime.Now, false)?.Rule != "same-4", "Flag: 4 different scripts of one tool are not the same call");
        Check(Watch.FlagRule(same, DateTime.Now, false)?.Rule == "same-4", "Flag: 4 identical scripts are the same call");
    }

    // one step polling a page that never loads: 3 minutes in one browser call, which the step rules (they read finished
    // steps) and "silent" (a running step is waiting) both let through
    private static void SlowStepCases()
    {
        DateTime now = DateTime.Now;
        TurnLog Running(string tool, string command, double minutes)
        {
            TurnLog log = LogOf(("Read", "a.cs", false, "x"));
            log.Steps.Add(new TurnStep("r", tool, "", tool, now.AddMinutes(-minutes), false) { Command = command, Input = "{\"code\":\"poll the deal page\"}" });
            return log;
        }
        (string Rule, string Reason)? polling = Watch.FlagRule(Running("mcp__playwright__browser_run_code_unsafe", "", 3), now, true);
        Check(polling?.Rule == "slow-step" && polling.Value.Reason.Contains("3 min") && polling.Value.Reason.Contains("poll the deal page"),
            "Slow step: one browser call 3 min with nothing back is flagged, with what it runs - got: " + polling?.Reason);
        Check(Watch.FlagRule(Running("mcp__playwright__browser_run_code_unsafe", "", 1.5), now, true) == null, "Slow step: 1.5 min is not yet");
        Check(Watch.FlagRule(Running("Bash", "npm run build", 4), now, true, TimeSpan.FromMinutes(6)) == null,
            "Slow step: a build that usually takes 6 min is not slow at 4");
        Check(Watch.FlagRule(Running("Bash", "npm run build", 10), now, true, TimeSpan.FromMinutes(6))?.Rule == "slow-step",
            "Slow step: the same build at 10 min is");
    }

    private static void FlagCases()
    {
        SameInputCases();
        SlowStepCases();
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

    private static void BackgroundCases()
    {
        // measured: the CLI writes a background task's output to %TEMP%\claude\<project>\<session>\tasks\<id>.output
        string root = Path.Combine(Path.GetTempPath(), "devbreak-selftest-bg");
        string tasks = Path.Combine(root, "C--some-folder", "sess-1", "tasks");
        Directory.CreateDirectory(tasks);
        File.WriteAllText(Path.Combine(tasks, "b1.output"), "x");
        try
        {
            Check(BackgroundWatch.OutputFile(root, "sess-1", "b1") == Path.Combine(tasks, "b1.output"), "Background: the task's output file is found by session and task id");
            Check(BackgroundWatch.OutputFile(root, "sess-1", "b2") == null, "Background: no file yet -> null");
        }
        finally { Directory.Delete(root, true); }

        Check(BackgroundWatch.Tail("a\n\nb\r50%\r100%\nc\n", 2).SequenceEqual(["100%", "c"]),
            "Background: the tail keeps the last lines, a progress line as it ends (after its last \\r), no blanks - got: " +
            string.Join("|", BackgroundWatch.Tail("a\n\nb\r50%\r100%\nc\n", 2)));

        const string waitOnBuild = "id=502f6581-c16f-4e20-8fb7-f941fc479266; until s=$(gcloud builds describe $id --project=my-project --region=me-west1 --format=\"value(status)\"); do sleep 10; done";
        Check(BackgroundWatch.CloudBuild(waitOnBuild, "") == ("502f6581-c16f-4e20-8fb7-f941fc479266", "my-project", "me-west1"),
            "Background: a wait on a Cloud Build gives its id, project and region");
        Check(BackgroundWatch.CloudBuild("gcloud builds triggers run t1 --project=p1 --region=r1", "1282b8f8-526d-4f56-9af1-ee30f0863344\tabc") == ("1282b8f8-526d-4f56-9af1-ee30f0863344", "p1", "r1"),
            "Background: a triggered build's id is read from its output");
        Check(BackgroundWatch.CloudBuild("npm run build", "") == null, "Background: a local build is not a Cloud Build");

        List<(string Name, string Status)> steps = BackgroundWatch.BuildSteps(
            """{"status":"WORKING","steps":[{"id":"build","status":"SUCCESS"},{"name":"gcr.io/cloud-builders/docker","status":"WORKING"},{"id":"push"}]}""");
        Check(steps.SequenceEqual([("build", "SUCCESS"), ("docker", "WORKING"), ("push", "QUEUED")]),
            "Background: build steps by id, else the builder's last name; no status yet is QUEUED - got: " + string.Join(", ", steps));

        Check(BackgroundWatch.Rollout("gcloud deploy rollouts describe ro-1 --release=r-1 --delivery-pipeline=pipe --project=p --region=r --format=\"value(state)\"")
              == ("ro-1", "r-1", "pipe", "p", "r"), "Background: a wait on a Cloud Deploy rollout gives its rollout, release, pipeline, project and region");
        Check(BackgroundWatch.Rollout("ro=r-c55-0001; until s=$(gcloud deploy rollouts describe $ro --release=r-c55 --delivery-pipeline=web --project=p --region=r); do sleep 5; done")
              == ("r-c55-0001", "r-c55", "web", "p", "r"), "Background: the rollout name is read from the variable it was set in");
        // the values go back into a gcloud command: anything a shell would read as code means no poll at all
        Check(BackgroundWatch.Rollout("ro=$(touch x); gcloud deploy rollouts describe $ro --release=r --delivery-pipeline=p --project=p --region=r") == null,
            "Background: a rollout name with shell code in it is not polled");
        Check(BackgroundWatch.Rollout("gcloud deploy rollouts describe r1 --release=`id` --delivery-pipeline=p --project=p --region=r") == null,
            "Background: a flag with shell code in it is not polled");
        Check(BackgroundWatch.CloudBuild("gcloud builds describe 502f6581-c16f-4e20-8fb7-f941fc479266 --project=$(id) --region=r", "") == null,
            "Background: a build with shell code in its project is not polled");
    }

    private static void FreshCases()
    {
        Check(Watch.GoFresh(165_000, working: false, backgroundTasks: 0, queued: 0), "Fresh: an idle session at 165k goes fresh on the next message");
        Check(!Watch.GoFresh(119_000, false, 0, 0), "Fresh: under 120k it stays");
        Check(!Watch.GoFresh(165_000, true, 0, 0), "Fresh: not while a turn runs");
        Check(!Watch.GoFresh(165_000, false, 1, 0), "Fresh: not while a background task runs - its result comes back to this session");
        Check(!Watch.GoFresh(165_000, false, 0, 1), "Fresh: not with a queued message - it goes to this session first");
        string first = Watch.FreshMessage("Goal: deploy.", "now check the logs");
        Check(first.Contains("Goal: deploy.") && first.EndsWith("now check the logs"), "Fresh: the first message is the handover, then what you wrote - got: " + first);
    }

    private static void DigestCases()
    {
        // the shapes of two real answers (2026-10-28): one with TITLE:, one opening with a bold sentence
        const string answer = """
            TITLE: Hotel search reproduced locally: about 2 s on a warm search service

            **I reproduced it locally.** I sent the search three times.

            | Call | Time |
            |---|---|
            | 1 | 1925 ms |

            **Not verified:**
            - **A cold-JVM first call.** It needs a restart of its PID.
            - Which jar the JVM loaded.

            The rerun script is `time-search.ps1`.

            QUESTIONS:
            1. Should I restart search service and run the script right after startup? Suggested answer: yes. Today's numbers come from a warm JVM.
            2. Should a follow-up task look at the Redis list at `CacheConfig.java:100-102`? Suggested answer: only as a proposal,
               since core library changes need it.

            EVIDENCE: api, logs, perf
            """;
        Check(FactoryDigest.Headline(answer, "resend the request") == "Hotel search reproduced locally: about 2 s on a warm search service",
            "Digest: the headline is the answer's TITLE - got: " + FactoryDigest.Headline(answer, "x"));
        Check(FactoryDigest.Headline("**The first search is slow only on a fresh instance.** About four caches...", "https://x/y") == "The first search is slow only on a fresh instance.",
            "Digest: with no TITLE, the answer's opening bold sentence");
        Check(FactoryDigest.Headline("Plain answer.", "the task title") == "the task title", "Digest: else the task's own title");

        List<FactoryQuestion> asks = FactoryDigest.Questions(answer);
        Check(asks.Count == 2, "Digest: both questions are found - got " + asks.Count);
        Check(asks.Count == 2 && asks[0].Ask == "Should I restart search service and run the script right after startup?"
              && asks[0].Suggested == "yes. Today's numbers come from a warm JVM.", "Digest: a question splits into the ask and its suggested answer - got: " + (asks.Count > 0 ? asks[0] : null));
        Check(asks.Count == 2 && asks[1].Suggested == "only as a proposal, since core library changes need it.", "Digest: a question that wraps onto the next line is read whole - got: " + (asks.Count > 1 ? asks[1].Suggested : ""));
        Check(FactoryDigest.Questions("1. Should the caches go into Redis? I suggest yes, after a check.").Count == 0, "Digest: numbered lines outside QUESTIONS: are not questions");
        bool overflowSafe;
        try { overflowSafe = FactoryDigest.Questions("QUESTIONS:\n99999999999. Too big a number?\n2. Fine?").Count == 1; }
        catch (OverflowException) { overflowSafe = false; }
        Check(overflowSafe, "Digest: a question number too big for an int is skipped, not thrown on every refresh");
        List<FactoryQuestion> suggest = FactoryDigest.Questions("QUESTIONS:\n1. Should the caches go into Redis? I suggest yes, after a check.");
        Check(suggest.Count == 1 && suggest[0].Suggested == "yes, after a check.", "Digest: 'I suggest' reads as the suggested answer");

        (string body, string notVerified) = FactoryDigest.Body(answer);
        Check(!body.Contains("TITLE:") && !body.Contains("QUESTIONS:") && !body.Contains("EVIDENCE:") && !body.Contains("Should I restart"),
            "Digest: the body leaves out the title, the questions and the evidence line - got: " + body);
        Check(body.Contains("I reproduced it locally") && body.Contains("| 1 | 1925 ms |") && body.Contains("time-search.ps1"), "Digest: the body keeps the findings, tables and what follows");
        Check(!body.Contains("Not verified") && notVerified.Contains("A cold-JVM first call") && notVerified.Contains("Which jar"),
            "Digest: 'Not verified' is set apart - got: " + notVerified);

        const string note = "Read the report and answer questions 1-4.\n\nWhy it stopped: Could not switch your checkout: fatal: invalid reference\n\nProposed changes to the factory (for you to approve):\n- Retry a busy session.";
        (string action, string why) = FactoryDigest.SplitNote(note);
        Check(action == "Read the report and answer questions 1-4." && why == "Could not switch your checkout: fatal: invalid reference",
            $"Digest: the manager's note splits into what to do and why it stopped - got: {action} | {why}");
        Check(FactoryDigest.SplitNote("Claude needs you: sign in.") == ("Claude needs you: sign in.", ""), "Digest: a note with no 'Why it stopped' is all action");

        string sent = FactoryDigest.Answers(asks, new Dictionary<int, string> { [1] = FactoryDigest.Yes, [2] = "no - leave the core library as it is" });
        Check(sent.Contains("1. Should I restart search service") && sent.Contains("Yes, as you suggested: yes. Today's numbers")
              && sent.Contains("2. Should a follow-up task") && sent.Contains("no - leave the core library as it is"), "Digest: the answers go back as one message - got: " + sent);
        Check(FactoryDigest.Answers(asks, new Dictionary<int, string> { [2] = FactoryDigest.No }) is var one && !one.Contains("1.") && one.Contains("No."),
            "Digest: only the answered questions are sent");

        Check(FactoryDigest.Clean("Add them to `CacheConfig.java:100-102`, **then** check") == "Add them to CacheConfig.java:100-102, then check",
            "Digest: plain text for a label has no markdown marks - got: " + FactoryDigest.Clean("Add them to `CacheConfig.java:100-102`, **then** check"));
        // 1920x1080, 2026-10-28: the card took ~650 of the ~760 px under the line and left Activity ~100 px
        Check(FactoryView.CardMaxHeight(760) == 380, "Card: half the room under the line, the activity gets the other half - got " + FactoryView.CardMaxHeight(760));
        Check(FactoryView.CardMaxHeight(300) == 180, "Card: never under 180 px on a small window");
        DateTime now = new(2026, 10, 28, 14, 0, 0);
        Check(FactoryDigest.Ago(new DateTime(2026, 10, 28, 11, 58, 0), now) == "11:58 · 2 h ago", "Digest: when it stopped - got: " + FactoryDigest.Ago(new DateTime(2026, 10, 28, 11, 58, 0), now));
        Check(FactoryDigest.Ago(new DateTime(2026, 10, 28, 13, 55, 0), now) == "13:55 · 5 min ago", "Digest: minutes ago");
        Check(FactoryDigest.Ago(new DateTime(2026, 10, 27, 9, 0, 0), now) == "27/10 09:00 · 1 d ago", "Digest: another day");
    }

    private static void HandOffCases()
    {
        // a research run that proved a change by editing another repo (2026-10-28): Development takes it in that repo
        static bool IsRepo(string dir) => dir == @"C:\r\core";
        const string outside = @"C:\r\core\ext\src\Cache.java - added ""Places"" to the Redis list, plus its registration - C:\r\core\ext\src\Cache.java.bak";
        Check(Factory.OutsideRepo([outside], @"C:\r", IsRepo) == @"C:\r\core", "Hand off: the repo of the change it made outside its own - got " + Factory.OutsideRepo([outside], @"C:\r", IsRepo));
        Check(Factory.OutsideRepo(["changed the proxy settings"], @"C:\r", IsRepo) == null, "Hand off: an outside change with no path names no repo");
        Check(Factory.OutsideRepo([@"D:\elsewhere\x.txt - y"], @"C:\r", IsRepo) == null, "Hand off: a path in no repo names none");
        // the path is the model's text: it may only pick a repo inside the folder the research ran in
        static bool AnyRepo(string dir) => dir is @"C:\other\repo" or @"C:\r\core";
        Check(Factory.OutsideRepo([@"C:\other\repo\a.cs - x"], @"C:\r", AnyRepo) == null, "Hand off: a repo outside the run's folder is never the target");
        Check(Factory.OutsideRepo([@"C:\r\..\other\repo\a.cs - x"], @"C:\r", AnyRepo) == null, "Hand off: '..' cannot climb out of the run's folder");
        Check(Factory.OutsideRepo([@"C:\rx\core\a.cs - x"], @"C:\r", d => d == @"C:\rx\core") == null, "Hand off: a sibling folder sharing the name's start is outside");

        var research = new FactoryRun
        {
            Id = "r1", Title = "First search slow", Department = FactoryDepartment.Research, Task = "reproduce it locally",
            Repo = @"C:\r\search", BaseBranch = "main", Folder = @"C:\r", Result = "Places in Redis: the first search 14 s -> 6 s.",
        };
        research.OutsideChanges.Add(outside);
        (string task, string folder, string branch) = Factory.HandOffTask(research, IsRepo);
        Check(folder == @"C:\r\core" && branch == "", "Hand off: research goes to the repo it changed, on that repo's own branch - got " + folder + " / " + branch);
        Check(task.Contains("research run \"First search slow\"") && task.Contains("Places in Redis: the first search 14 s -> 6 s.")
              && task.Contains("Cache.java - added \"Places\"") && task.Contains("draft PR"), "Hand off: the task carries the findings and the change it tested - got: " + task);
        research.OutsideChanges.Clear();
        (_, string ownFolder, string ownBranch) = Factory.HandOffTask(research, IsRepo);
        Check(ownFolder == @"C:\r\search" && ownBranch == "main", "Hand off: with no outside change, the run's own repo and base");
        var diagnosed = new FactoryRun { Id = "d1", Title = "Login fails", Department = FactoryDepartment.Troubleshooting, Task = "login 500", Repo = @"C:\r\web", BaseBranch = "dev", Result = "Null session." };
        Check(Factory.HandOffTask(diagnosed, IsRepo).Task.Contains("Fix the problem diagnosed by the troubleshooting run"), "Hand off: a diagnosis is still handed over as a fix");
    }

    private static void SteerCases()
    {
        // measured 2026-10-28 (claude -p stream-json, --replay-user-messages): a message written mid-turn is echoed back
        // when Claude takes it in, at its next tool step, and the same turn ends with one result that follows it
        var steers = new ClaudeSessions.SteerTracker();
        Check(steers.TurnEnds(), "Steer: with nothing sent, a result ends the turn");
        steers.Sent("do it all locally");
        Check(!steers.TurnEnds(), "Steer: a message not taken in yet - the result is not the end, its own turn follows");
        Check(!steers.Echoed("Run the search twice."), "Steer: the turn's own prompt echoed back is not a steer");
        Check(steers.Echoed("do it all locally") && steers.TurnEnds(), "Steer: once it is echoed back, the next result ends the turn");

        string Echo(string content) => """{"type":"user","message":{"role":"user","content":""" + content + "}}";
        JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;
        Check(ClaudeSessions.SteerTracker.EchoText(Root(Echo("\"do it all locally\""))) == "do it all locally", "Steer: an echo with text content is read");
        Check(ClaudeSessions.SteerTracker.EchoText(Root(Echo("""[{"type":"text","text":"do it all locally"}]"""))) == "do it all locally", "Steer: an echo with text blocks is read");
        Check(ClaudeSessions.SteerTracker.EchoText(Root(Echo("""[{"type":"tool_result","tool_use_id":"t1","content":"one"}]"""))) == null, "Steer: a tool result is not an echo");
        Check(ClaudeSessions.SteerTracker.EchoText(Root("""{"type":"assistant","message":{"content":[]}}""")) == null, "Steer: an assistant line is not an echo");

        Check(Factory.SteerMessage("do it all locally").EndsWith("do it all locally") && Factory.SteerMessage("x").Contains("while you work"),
            "Steer: the message says it comes from the developer while the task works");
        Check(Factory.WithSteers("Plan the change.", ["use the local stack", "no prod calls"]) is var p
              && p.Contains("- use the local stack") && p.Contains("- no prod calls") && p.EndsWith("Plan the change."), "Steer: held messages go first in the next turn - got: " + Factory.WithSteers("Plan.", ["a"]));
        Check(Factory.WithSteers("Plan the change.", []) == "Plan the change.", "Steer: no held messages, the prompt as it is");
    }

    private static void ResendCases()
    {
        // measured: claude --session-id on an id that has a transcript fails "Session ID ... is already in use"; a reply
        // to an investigation asks for a new session on the run's own id (13:18, after a restart closed the open process)
        Check(!ClaudeSessions.LiveSession.StartsNew(isNew: true, transcript: @"C:\x\0a1b2c3d.jsonl"), "Session: a new turn on a session with a transcript resumes it");
        Check(ClaudeSessions.LiveSession.StartsNew(isNew: true, transcript: null), "Session: a new turn with no transcript yet starts the session");
        Check(!ClaudeSessions.LiveSession.StartsNew(isNew: false, transcript: null), "Session: a resume stays a resume");

        // an answered run's branch is deleted when it finishes; its changes ask for no branch (10:32 "invalid reference")
        var answered = new FactoryRun { Id = "t1", Department = FactoryDepartment.Research, NoChange = true, Result = "The first search is slow." };
        Check(!Factory.NeedsBranch(answered), "Resend: an answered run is revised without its (deleted) branch");
        var built = new FactoryRun { Id = "t2", Department = FactoryDepartment.Development, Result = "" };
        Check(Factory.NeedsBranch(built), "Resend: a run that built a change is revised on its branch");
        Check(!Factory.NeedsBranch(new FactoryRun { Id = "t3", FolderWide = true }), "Resend: a folder-wide run has no branch");
        // Continue takes the same rule (11:57 "invalid reference" on Continue)
        answered.InCheckout = built.InCheckout = true;
        Check(!Factory.ResumesOnBranch(answered), "Continue: an answered run in the checkout goes on without its (deleted) branch");
        Check(Factory.ResumesOnBranch(built), "Continue: a run that built a change goes on on its branch");
        string gone = Factory.SwitchFailed("devbreak/x-1", "fatal: invalid reference: devbreak/x-1");
        Check(gone.Contains("no longer exists") && !gone.Contains("fatal:"), "Continue: a deleted branch is said plainly - got: " + gone);
        // git's own words (2.52) when local changes block a switch: then they are stashed and the switch tried again
        Check(Factory.SwitchBlockedByLocalChanges("error: Your local changes to the following files would be overwritten by checkout:\n\tb.txt\nAborting"),
            "Checkout: a switch blocked by local changes is seen");
        Check(Factory.SwitchBlockedByLocalChanges("error: The following untracked working tree files would be overwritten by checkout:\n\tc.txt"),
            "Checkout: a switch blocked by untracked files is seen");
        Check(!Factory.SwitchBlockedByLocalChanges("fatal: invalid reference: devbreak/x-1"), "Checkout: a missing branch is not stashed for");
        Check(Factory.SwitchFailed("devbreak/x-1", "error: your local changes would be overwritten").Contains("would be overwritten"),
            "Continue: another switch failure keeps git's words");

        Check(Factory.LatestArtifact(answered).Contains("The first search is slow.") && !Factory.LatestArtifact(answered).Contains("report:"),
            "Resend: the revision carries the latest answer, and no report line while there is no report");
        string report = Path.Combine(AppContext.BaseDirectory, "traces", "t1-report.html");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, "<html></html>");
        try
        {
            Check(Factory.LatestArtifact(answered).Contains("Its report: " + report),
                "Resend: the report on disk is named even after a failed round cleared the run's link - got: " + Factory.LatestArtifact(answered));
        }
        finally { File.Delete(report); }
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
