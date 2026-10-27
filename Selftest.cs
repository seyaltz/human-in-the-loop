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
