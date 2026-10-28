using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevBreak;

/// <summary>A tool call of a turn: what it was, when it ran, how it ended.</summary>
/// <param name="Key">a shell command's family - its first two words after a leading "cd ... &amp;&amp;" - else the tool's name</param>
/// <param name="Sub">a step of a subagent (the stream marks it with parent_tool_use_id): shown, but not the turn's own work</param>
public sealed record TurnStep(string Id, string Tool, string Summary, string Key, DateTime Start, bool Sub)
{
    public DateTime? End { get; set; }
    public bool Error { get; set; }
    public string Result { get; set; } = "";
    public bool IsShell => Tool is "Bash" or "PowerShell";
    public string Command { get; init; } = "";

    /// <summary>The call's whole input as sent: two calls are the same only when this matches (a summary can be blank).</summary>
    public string Input { get; init; } = "";
}

/// <summary>
/// What a turn did, step by step, read from its process's stream-json lines: every tool call with its time and
/// outcome, the files it changed and how many lines. The watcher's rules, the wait chip, the diagnosis line, the
/// quality checks and the lessons all read this.
/// </summary>
public sealed class TurnLog
{
    private static readonly Regex LeadingCd = new(@"^\s*cd\s+[^&;]+(&&|;)\s*", RegexOptions.Compiled);

    public TurnLog(string message)
    {
        Message = message;
        Start = DateTime.Now;
    }

    public string Message { get; }
    public DateTime Start { get; }
    public List<TurnStep> Steps { get; } = [];
    public HashSet<string> Changed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int LinesChanged { get; private set; }
    public string Answer { get; set; } = "";

    /// <summary>The step still waiting for its result (the last one started), or null.</summary>
    public TurnStep? Running => Steps.LastOrDefault(s => s.End == null);

    /// <summary>A shell command's family, as the audit counted them: "cd x &amp;&amp; npm test --a" -> "npm test".</summary>
    internal static string KeyOf(string command)
    {
        string[] words = LeadingCd.Replace(command, "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(2));
    }

    public void Read(JsonElement line)
    {
        string type = Str(line, "type");
        bool sub = Str(line, "parent_tool_use_id").Length > 0;
        if (!line.TryGetProperty("message", out JsonElement message) || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement block in content.EnumerateArray())
        {
            string kind = Str(block, "type");
            if (type == "assistant" && kind == "tool_use")
            {
                string tool = Str(block, "name");
                JsonElement input = block.TryGetProperty("input", out JsonElement i) ? i : default;
                string command = Str(input, "command");
                Steps.Add(new TurnStep(Str(block, "id"), tool, ClaudeSessions.ToolSummary(block),
                    command.Length > 0 ? KeyOf(command) : tool, DateTime.Now, sub)
                {
                    Command = command, Input = input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText(),
                });
                if (!sub && tool is "Edit" or "Write" or "NotebookEdit" or "MultiEdit")
                {
                    string file = Str(input, "file_path") is { Length: > 0 } f ? f : Str(input, "notebook_path");
                    if (file.Length > 0) Changed.Add(file);
                    string added = Str(input, "new_string") is { Length: > 0 } n ? n : Str(input, "content") is { Length: > 0 } c ? c : Str(input, "new_source");
                    if (added.Length > 0) LinesChanged += added.Split('\n').Length;
                }
            }
            else if (type == "user" && kind == "tool_result" && Steps.LastOrDefault(s => s.Id == Str(block, "tool_use_id")) is { } step)
            {
                step.End = DateTime.Now;
                step.Error = block.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True;
                string text = block.TryGetProperty("content", out JsonElement body)
                    ? body.ValueKind == JsonValueKind.String ? body.GetString()! : string.Join(" ", body.ValueKind == JsonValueKind.Array
                        ? body.EnumerateArray().Select(b => Str(b, "text")) : [])
                    : "";
                step.Result = text.Length > 1000 ? text[..1000] : text;
            }
        }
    }

    internal static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
