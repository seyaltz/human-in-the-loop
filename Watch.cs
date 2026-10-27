using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DevBreak;

/// <summary>
/// The watcher's rules, as pure functions of a turn's log: what it waits on, whether it is stuck, whether "done" was
/// checked, where its time went. The pane only shows what these return; <c>-selftest</c> checks them case by case.
/// </summary>
public static class Watch
{
    private static readonly Regex LeadingCd = new(@"^\s*cd\s+[^&;]+(&&|;)\s*", RegexOptions.Compiled);

    private static readonly Regex Dialect = new(
        @"is not recognized as|unexpected EOF|ParserError|Unexpected token|command not found", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly TimeSpan Silent = TimeSpan.FromMinutes(5);
    private const int NoProgress = 12;

    /// <summary>
    /// Why a running turn looks stuck, or null. The first two are the Factory's (OpenHands' thresholds: repeats in a
    /// row, so a long turn may run `git status` four times); the rest: an error run, a command written for another
    /// shell, silence with nothing running, and published loop-detection practice's no-progress counter (exact repeats
    /// miss the slow loop where each try is a different command with a different error). A subagent's steps are its
    /// own work, not the turn's: they are left out.
    /// </summary>
    /// <param name="busy">a command or a background task is running: silence is waiting, not being stuck</param>
    public static string? Flag(TurnLog log, DateTime now, bool busy) => FlagRule(log, now, busy)?.Reason;

    /// <summary>The flag with a fixed id for its rule (a reason can carry a live count; Dismiss keys on the id).</summary>
    public static (string Rule, string Reason)? FlagRule(TurnLog log, DateTime now, bool busy)
    {
        List<TurnStep> done = [.. log.Steps.Where(s => !s.Sub && s.End != null)];
        if (done.Count > 0)
        {
            TurnStep last = done[^1];
            bool Same(int n, Func<TurnStep, bool> also) => done.Count >= n &&
                done.TakeLast(n).All(s => s.Tool == last.Tool && s.Summary == last.Summary && s.Input == last.Input && s.Error == last.Error && also(s));
            if (Same(3, s => s.Error)) return ("failed-3", $"the same {last.Tool} call failed 3 times in a row: {last.Summary}");
            if (Same(4, _ => true)) return ("same-4", $"it ran the same {last.Tool} call 4 times in a row: {last.Summary}");
            if (done.TakeLast(10).Count(s => s.Error) >= 5) return ("errors", "5 of its last 10 tool calls failed");
            if (last.Error && Dialect.IsMatch(last.Result)) return ("dialect", "a command written for another shell: " + Short(last.Result, 90));
        }
        DateTime lastMove = log.Steps.Where(s => !s.Sub).Select(s => s.End ?? s.Start).DefaultIfEmpty(log.Start).Max();
        if (!busy && now - lastMove >= Silent) return ("silent", "no new step for 5 minutes, and nothing is running");
        int since = 0;
        var seen = new HashSet<string>();
        var errors = new HashSet<string>();
        foreach (TurnStep s in done)
        {
            bool progress = s.Error
                ? errors.Add(Short(s.Result, 60))
                : s.Tool is "Edit" or "Write" or "NotebookEdit" or "MultiEdit" || seen.Add(s.Result);
            since = progress ? 0 : since + 1;
        }
        return since >= NoProgress ? ("no-progress", $"{since} steps without progress: no file written, no new result, no new error") : null;
    }

    /// <summary>
    /// Where a finished turn's time went - "waiting 0:40 · tools 1:10 · model 2:05" - and the one thing that would have
    /// saved the most, when there is one; null for a turn under 2 minutes.
    /// </summary>
    public static string? Diagnosis(TurnLog log, TimeSpan total)
    {
        if (total < TimeSpan.FromMinutes(2)) return null;
        List<TurnStep> own = [.. log.Steps.Where(s => !s.Sub && s.End != null)];
        double Seconds(TurnStep s) => (s.End!.Value - s.Start).TotalSeconds;
        bool Waits(TurnStep s) => s.Tool == "Monitor" || SessionStats.WaitCommand.IsMatch(s.Command);
        double waiting = own.Where(Waits).Sum(Seconds);
        double tools = own.Where(s => !Waits(s)).Sum(Seconds);
        double model = Math.Max(0, total.TotalSeconds - waiting - tools);
        string text = $"waiting {Clock(waiting)} · tools {Clock(tools)} · model {Clock(model)}";
        TurnStep? slow = own.Where(s => s.IsShell && !Waits(s)).OrderByDescending(Seconds).FirstOrDefault();
        if (slow != null && Seconds(slow) >= 120)
        {
            return text + $" - {Short(LeadingCd.Replace(slow.Command, ""), 50)} ran {(int)Math.Round(Seconds(slow) / 60)} min in the foreground - run it in the background next time";
        }
        if (own.Where(s => s.Error && s.IsShell).GroupBy(s => s.Key).OrderByDescending(g => g.Count()).FirstOrDefault() is { } fails && fails.Count() >= 3)
        {
            return text + $" - {fails.Key} failed {fails.Count()} times - a skill for it would save the retries";
        }
        return text;
    }

    private static readonly Regex Completion = new(@"\b(done|fixed|works|passes|implemented|ready|all set)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // a step that checks work: a test runner, a build, the app, a localhost call, or a changed file run directly
    private static readonly Regex CheckCommand = new(
        @"\bdotnet\s+(build|test|run)\b|\b(npm|pnpm|yarn)\s+(test|run|build)\b|\bnpx\s+(jest|vitest|playwright)\b|\bpytest\b|\bgradlew|\bmvn\b" +
        @"|\bgo\s+(test|build)\b|\bcargo\s+(test|build)\b|\bcurl\b.*(localhost|127\.0\.0\.1)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A turn that changed files and says it is finished, with nothing after the last change that checks it: the nudge
    /// to send ("Run dotnet build and show the output"), or null. A claim and a record are different kinds of fact, and
    /// only the record can be checked - "it works" next to a record with no run in it is a claim.
    /// </summary>
    public static string? Unverified(TurnLog log, string answer)
    {
        if (log.Changed.Count == 0 || !Completion.IsMatch(answer)) return null;
        List<TurnStep> own = [.. log.Steps.Where(s => !s.Sub)];
        int lastChange = own.FindLastIndex(s => s.Tool is "Edit" or "Write" or "NotebookEdit" or "MultiEdit");
        IEnumerable<string> names = log.Changed.Select(f => System.IO.Path.GetFileName(f)).Where(n => n.Length > 0);
        bool Checks(TurnStep s) =>
            (s.IsShell && (CheckCommand.IsMatch(s.Command) || names.Any(n => s.Command.Contains(n, StringComparison.OrdinalIgnoreCase))))
            || s.Tool.Contains("playwright", StringComparison.OrdinalIgnoreCase) || s.Tool.Contains("chrome", StringComparison.OrdinalIgnoreCase)
            || s.Tool.Contains("browser", StringComparison.OrdinalIgnoreCase);
        if (own.Skip(lastChange + 1).Any(Checks)) return null;
        return $"You said it is finished, but nothing ran after the last change. Run {CheckFor(log.Changed)} and show the output.";
    }

    // the check that fits the changed files
    private static string CheckFor(IEnumerable<string> files)
    {
        string[] ext = [.. files.Select(f => System.IO.Path.GetExtension(f).ToLowerInvariant())];
        if (ext.Any(e => e is ".cs" or ".csproj" or ".xaml")) return "dotnet build (and its tests if there are any)";
        if (ext.Any(e => e is ".ts" or ".tsx" or ".js" or ".jsx")) return "npm test (or the build)";
        if (ext.Contains(".py")) return "pytest, or the changed script itself";
        if (ext.Any(e => e is ".java" or ".kt")) return "gradlew test";
        if (ext.Contains(".go")) return "go test";
        if (ext.Contains(".rs")) return "cargo test";
        return "the app or the changed file";
    }

    private static readonly Regex QuickWord = new(@"\b(commit|push|merge|restart|rebuild|deploy|run|status|pull|rebase|tag)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A short operational ask ("commit and push", "restart it"): low effort answers it faster, at the same quality.</summary>
    public static bool IsQuickStep(string text) => text.Trim().Length is > 0 and <= 80 && QuickWord.IsMatch(text);

    private static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

    private static string Short(string text, int max)
    {
        string line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    /// <summary>The wait chip: "⧗ waiting on npm run build · 3:20 · usually ~6 min" (the command without a leading cd).</summary>
    public static string WaitText(TurnStep step, DateTime now, TimeSpan? usual)
    {
        string command = LeadingCd.Replace(step.Command.Length > 0 ? step.Command : step.Summary, "").Replace('\n', ' ').Trim();
        if (command.Length > 40) command = command[..39] + "…";
        TimeSpan ran = now - step.Start;
        string text = $"⧗ waiting on {command} · {(int)ran.TotalMinutes}:{ran.Seconds:00}";
        if (usual is { } u) text += $" · usually ~{Math.Max(1, (int)Math.Round(u.TotalMinutes))} min";
        return text;
    }

    /// <summary>
    /// A finished turn in one devbreak.log line: its own steps and failures, what it changed, how long, what it cost, the
    /// context the next turn starts from, and the AI checks Dev break spent watching it.
    /// </summary>
    public static string TurnSummary(TurnLog log, double seconds, double cost, double context, double compactAt, int aiChecks)
    {
        int steps = log.Steps.Count(s => !s.Sub), failed = log.Steps.Count(s => !s.Sub && s.Error);
        string files = log.Changed.Count == 1 ? "1 file" : $"{log.Changed.Count} files";
        string size = context <= 0 ? "?" : $"{Math.Round(context / 1000):0}k" + (compactAt > 0 ? $" (compacts at {Math.Round(compactAt / 1000):0}k)" : "");
        return $"{steps} steps, {failed} failed, {files} / {log.LinesChanged} lines, {seconds / 60:0.0} min, ${cost:0.00}, context {size}, {aiChecks} ai checks";
    }

    /// <summary>What a fresh session starts from instead of the whole history.</summary>
    public const string HandoverQuestion =
        "Write a handover of this conversation for a new session that will not see it. Plain text, under 300 words: " +
        "the goal in the user's words; what is done, with file paths; decisions made and why; what is left, in order; " +
        "commands that work here; anything that failed and must not be retried. No preamble.";

    /// <summary>
    /// The footer's context: every step re-sends the whole conversation, so the size in tokens is what each step costs -
    /// a percent of a 1M window hid that (51% was 511k re-read per step). Heavy from 150k, or 80% full.
    /// </summary>
    public static (string Text, bool Heavy) ContextLabel(double tokens, int percent) =>
        ($"{Math.Round(tokens / 1000):0}k context", tokens >= 150_000 || percent >= 80);
}
