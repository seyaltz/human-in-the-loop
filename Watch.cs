using System;
using System.Text.RegularExpressions;

namespace DevBreak;

/// <summary>
/// The watcher's rules, as pure functions of a turn's log: what it waits on, whether it is stuck, whether "done" was
/// checked, where its time went. The pane only shows what these return; <c>-selftest</c> checks them case by case.
/// </summary>
public static class Watch
{
    private static readonly Regex LeadingCd = new(@"^\s*cd\s+[^&;]+(&&|;)\s*", RegexOptions.Compiled);

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
}
