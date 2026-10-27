using System;
using System.Collections.Generic;
using System.IO;
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

    public static int Run()
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), "started");
        Lines.Clear();
        _ok = true;
        try
        {
            TurnLogCases();
        }
        catch (Exception ex)
        {
            Check(false, "threw: " + ex);
        }
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
}
