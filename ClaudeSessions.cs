using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>
/// A Claude Code session on this machine: its id, the folder it works in, a title and when it was last used, and
/// whether Claude Code gave it a topic title (ai-title) - without one, Dev break has Claude write one.
/// </summary>
public sealed record SessionInfo(string Id, string Folder, string Title, DateTime LastActive, bool HasAiTitle = false);

/// <summary>
/// One line of a session's transcript: something the developer said, a tool step, a note Claude wrote while working,
/// the answer that ends a turn, or how the turn ended.
/// </summary>
public sealed record SessionEntry(SessionEntryKind Kind, string Text);

public enum SessionEntryKind { You, Note, Tool, Answer, Done, Error }

/// <summary>
/// Real Claude Code sessions, the same ones the CLI keeps under .claude\projects: listed, read back and continued.
/// A turn runs the user's own claude CLI in the session's folder with their full setup (CLAUDE.md, memory, skills,
/// plugins, hooks, MCP servers) and full access (bypass - the user chose it; -p cannot stop to ask), so a session can
/// be continued here or with "claude --resume &lt;id&gt;" in a terminal.
/// </summary>
public static class ClaudeSessions
{
    private const int MaxSessions = 60;
    private const int MaxEntries = 300;

    /// <summary>The most recently used sessions whose folder still exists, newest first.</summary>
    public static IReadOnlyList<SessionInfo> List()
    {
        var sessions = new List<SessionInfo>();
        if (!Directory.Exists(ClaudeProjects.ProjectsDir)) return sessions;
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        IEnumerable<FileInfo> files = Directory.GetDirectories(ClaudeProjects.ProjectsDir)
            .SelectMany(d => new DirectoryInfo(d).GetFiles("*.jsonl"))
            .OrderByDescending(f => f.LastWriteTime);
        foreach (FileInfo file in files)
        {
            if (sessions.Count >= MaxSessions) break;
            try
            {
                (string? folder, string? firstPrompt) = Head(file.FullName);
                if (folder == null || !Directory.Exists(folder)) continue;
                if (Path.GetFullPath(folder).StartsWith(temp, StringComparison.OrdinalIgnoreCase)) continue;
                (string? last, bool hasAiTitle) = LastTitle(file.FullName);
                string title = last ?? firstPrompt ?? Path.GetFileName(folder);
                sessions.Add(new SessionInfo(Path.GetFileNameWithoutExtension(file.Name), folder, Short(title, 90), file.LastWriteTime, hasAiTitle));
            }
            catch (Exception)
            {
                // a session file being written or unreadable: leave it out
            }
        }
        return sessions;
    }

    /// <summary>The session's file, if it has one yet.</summary>
    public static string? FileOf(string id) =>
        Directory.Exists(ClaudeProjects.ProjectsDir)
            ? Directory.GetDirectories(ClaudeProjects.ProjectsDir).Select(d => Path.Combine(d, id + ".jsonl")).FirstOrDefault(File.Exists)
            : null;

    /// <summary>What was said in a session: the developer's prompts, Claude's text and its tool steps (the last few hundred).</summary>
    public static IReadOnlyList<SessionEntry> Transcript(string id)
    {
        var entries = new List<SessionEntry>();
        string? file = FileOf(id);
        if (file == null) return entries;
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            bool user = line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
            if (!user && !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("isSidechain", out JsonElement side) && side.ValueKind == JsonValueKind.True) continue;
                if (root.TryGetProperty("isMeta", out JsonElement meta) && meta.ValueKind == JsonValueKind.True) continue;
                if (!root.TryGetProperty("message", out JsonElement message) || !message.TryGetProperty("content", out JsonElement content)) continue;
                if (user)
                {
                    if (content.ValueKind == JsonValueKind.String && IsHuman(root)) entries.Add(new SessionEntry(SessionEntryKind.You, content.GetString()!));
                    continue;
                }
                if (content.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement block in content.EnumerateArray()) AddBlock(entries, block);
            }
            catch (JsonException)
            {
                // a line being written: skip it
            }
        }
        AnswerPerTurn(entries);
        return entries.Count > MaxEntries ? entries[^MaxEntries..] : entries;
    }

    // a saved turn has no separate result: its last text is the answer, the texts before it were notes on the way
    private static void AnswerPerTurn(List<SessionEntry> entries)
    {
        int last = -1;
        for (int i = 0; i <= entries.Count; i++)
        {
            if (i == entries.Count || entries[i].Kind == SessionEntryKind.You)
            {
                if (last >= 0) entries[last] = entries[last] with { Kind = SessionEntryKind.Answer };
                last = -1;
            }
            else if (entries[i].Kind == SessionEntryKind.Note)
            {
                last = i;
            }
        }
    }

    /// <summary>
    /// Runs one turn of a session in its folder: <paramref name="isNew"/> starts it with that id, otherwise it resumes.
    /// Each step is reported as it streams in. Cancelling stops claude and everything it started.
    /// <paramref name="extraArgs"/> go to claude as they are (the factory denies pushing with them).
    /// </summary>
    public static async Task RunTurnAsync(string folder, string id, bool isNew, string prompt, Action<SessionEntry> onEntry,
        CancellationToken cancel, IEnumerable<string>? extraArgs = null)
    {
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "bypassPermissions" })
        {
            start.ArgumentList.Add(arg);
        }
        start.ArgumentList.Add(isNew ? "--session-id" : "--resume");
        start.ArgumentList.Add(id);
        foreach (string arg in extraArgs ?? []) start.ArgumentList.Add(arg);

        using Process process = await Task.Run(() => Process.Start(start)) ?? throw new InvalidOperationException("could not start claude");
        using CancellationTokenRegistration stop = cancel.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        bool finished = false;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                if (type == "assistant" && root.TryGetProperty("message", out JsonElement message)
                    && message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
                {
                    var entries = new List<SessionEntry>();
                    foreach (JsonElement block in content.EnumerateArray()) AddBlock(entries, block);
                    foreach (SessionEntry entry in entries) onEntry(entry);
                }
                else if (type == "result")
                {
                    finished = true;
                    bool error = root.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True;
                    double seconds = root.TryGetProperty("duration_ms", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() / 1000 : 0;
                    double cost = root.TryGetProperty("total_cost_usd", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
                    string text = root.TryGetProperty("result", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
                    if (error)
                    {
                        onEntry(new SessionEntry(SessionEntryKind.Error, text.Length > 0 ? text : "the turn ended with an error"));
                    }
                    else
                    {
                        // the answer that ends the turn; the texts streamed before it were notes on the way
                        if (text.Length > 0) onEntry(new SessionEntry(SessionEntryKind.Answer, text));
                        onEntry(new SessionEntry(SessionEntryKind.Done, $"{seconds:0.#} s · ${cost:0.00}"));
                    }
                }
            }
            catch (JsonException)
            {
                // not a whole JSON line: skip it
            }
        }
        await process.WaitForExitAsync();
        if (cancel.IsCancellationRequested)
        {
            onEntry(new SessionEntry(SessionEntryKind.Error, "Stopped."));
        }
        else if (!finished)
        {
            string err = (await stderr).Trim();
            onEntry(new SessionEntry(SessionEntryKind.Error, $"claude exited with {process.ExitCode}" + (err.Length > 0 ? ": " + err : "")));
        }
    }

    /// <summary>The command that continues this session in a terminal.</summary>
    public static string ResumeCommand(SessionInfo session) => $"cd \"{session.Folder}\"; claude --resume {session.Id}";

    private static void AddBlock(List<SessionEntry> entries, JsonElement block)
    {
        string kind = block.TryGetProperty("type", out JsonElement k) ? k.GetString() ?? "" : "";
        if (kind == "text" && block.TryGetProperty("text", out JsonElement text) && text.GetString() is { Length: > 0 } t)
        {
            entries.Add(new SessionEntry(SessionEntryKind.Note, t));
        }
        else if (kind == "tool_use")
        {
            string name = block.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "tool" : "tool";
            entries.Add(new SessionEntry(SessionEntryKind.Tool, name + "  " + ToolSummary(block)));
        }
    }

    // the one detail that says what a tool step does: its command, file, pattern or url
    private static string ToolSummary(JsonElement block)
    {
        if (!block.TryGetProperty("input", out JsonElement input) || input.ValueKind != JsonValueKind.Object) return "";
        foreach (string key in new[] { "command", "file_path", "pattern", "url", "query", "description", "prompt", "skill" })
        {
            if (input.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return Short(value.GetString()!.Replace('\n', ' '), 140);
            }
        }
        return "";
    }

    private static bool IsHuman(JsonElement root) =>
        !root.TryGetProperty("origin", out JsonElement origin)
        || (origin.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "human");

    // the folder (cwd) and the first prompt the developer typed, from the start of the file
    private static (string? Folder, string? FirstPrompt) Head(string file)
    {
        string? folder = null, prompt = null;
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        for (int i = 0; i < 200 && reader.ReadLine() is { } line; i++)
        {
            if (folder != null && prompt != null) break;
            if (!line.Contains("\"cwd\"", StringComparison.Ordinal) && !line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (folder == null && root.TryGetProperty("cwd", out JsonElement cwd)) folder = cwd.GetString();
                if (prompt == null && root.TryGetProperty("type", out JsonElement t) && t.GetString() == "user" && IsHuman(root)
                    && root.TryGetProperty("message", out JsonElement m) && m.TryGetProperty("content", out JsonElement c)
                    && c.ValueKind == JsonValueKind.String && !c.GetString()!.StartsWith('<'))
                {
                    prompt = c.GetString()!.Replace('\n', ' ').Trim();
                }
            }
            catch (JsonException)
            {
            }
        }
        return (folder, prompt);
    }

    // the session's latest generated title, from the end of the file (titles are rewritten as the session goes on)
    private static (string? Title, bool HasAiTitle) LastTitle(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long from = Math.Max(0, stream.Length - 256 * 1024);
        stream.Seek(from, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string? title = null;
        bool ai = false;
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"ai-title\"", StringComparison.Ordinal) && !line.Contains("\"custom-title\"", StringComparison.Ordinal)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                foreach (string key in new[] { "customTitle", "aiTitle" })
                {
                    if (doc.RootElement.TryGetProperty(key, out JsonElement value) && value.GetString() is { Length: > 0 } v)
                    {
                        title = v;
                        ai |= key == "aiTitle";
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
        return (title, ai);
    }

    private static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
