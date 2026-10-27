using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>
/// One clean question to Claude, outside any session: the watcher's check, the grade, the review, the lessons. Clean
/// means none of the user's settings, hooks or MCP servers and no session file (as the quiz's own calls run), so the
/// check is the same on every machine and leaves nothing in the sessions list.
/// </summary>
public static class ClaudeOnce
{
    /// <param name="tools">the tools it may use ("" for none), e.g. "Read,Grep,Glob"</param>
    /// <param name="allowed">the same tools allowed without asking (-p cannot ask), or "" with no tools</param>
    public static Task<string> AskAsync(string prompt, string? model, string folder, string tools, string allowed, TimeSpan timeout)
    {
        var args = new List<string> { "-p" };
        if (!string.IsNullOrEmpty(model)) args.AddRange(["--model", model]);
        args.AddRange(["--tools", tools]);
        if (allowed.Length > 0) args.AddRange(["--allowedTools", allowed]);
        args.AddRange(["--strict-mcp-config", "--setting-sources", "", "--no-session-persistence"]);
        return QuestionGenerator.RunAsync(args, prompt, folder, timeout);
    }
}
