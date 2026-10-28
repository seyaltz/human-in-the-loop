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
        // the branches Dev break rewound: from the cut message until the line that goes on from where it resumed
        List<RewindCut> cuts = SessionSettings.RewindsOf(id);
        RewindCut? skipping = null;
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            if (cuts.Count > 0 && Cut(line, cuts, ref skipping)) continue;
            bool user = line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
            // a "/command"'s output, in newer files a system line of its own
            bool local = !user && line.Contains("\"subtype\":\"local_command\"", StringComparison.Ordinal);
            if (!user && !local && !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("isSidechain", out JsonElement side) && side.ValueKind == JsonValueKind.True) continue;
                if (root.TryGetProperty("isMeta", out JsonElement meta) && meta.ValueKind == JsonValueKind.True) continue;
                if (local)
                {
                    if (root.TryGetProperty("content", out JsonElement said) && said.ValueKind == JsonValueKind.String
                        && (Tag(said.GetString()!, "local-command-stdout") ?? Tag(said.GetString()!, "local-command-stderr")) is { } output)
                    {
                        entries.Add(new SessionEntry(SessionEntryKind.Note, output.Length > 0 ? output : "(no output)"));
                    }
                    continue;
                }
                if (!root.TryGetProperty("message", out JsonElement message) || !message.TryGetProperty("content", out JsonElement content)) continue;
                if (user)
                {
                    if (!IsHuman(root) || Typed(content) is not { Length: > 0 } typed) continue;
                    // a "/command" is saved as tags: shown as typed, its output as the turn's answer, the CLI's caveat left out
                    if (Tag(typed, "local-command-stdout") is { } output) entries.Add(new SessionEntry(SessionEntryKind.Note, output.Length > 0 ? output : "(no output)"));
                    else if (Tag(typed, "local-command-stderr") is { } error) entries.Add(new SessionEntry(SessionEntryKind.Note, error));
                    else if (Tag(typed, "command-name") is { } name) entries.Add(new SessionEntry(SessionEntryKind.You, (name + " " + (Tag(typed, "command-args") ?? "")).Trim()));
                    else if (!typed.StartsWith("<local-command-caveat>", StringComparison.Ordinal)) entries.Add(new SessionEntry(SessionEntryKind.You, typed));
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

    // true when the line is in a rewound branch: from the line with the cut's uuid until a message whose parent is
    // where the session resumed (a message with no parent, for a rewind to the start)
    private static bool Cut(string line, List<RewindCut> cuts, ref RewindCut? skipping)
    {
        if (!line.Contains("\"uuid\"", StringComparison.Ordinal)) return skipping != null;
        string uuid = "", parent = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("uuid", out JsonElement u) && u.ValueKind == JsonValueKind.String) uuid = u.GetString()!;
            if (doc.RootElement.TryGetProperty("parentUuid", out JsonElement p) && p.ValueKind == JsonValueKind.String) parent = p.GetString()!;
        }
        catch (JsonException)
        {
            return skipping != null;
        }
        if (skipping == null)
        {
            skipping = cuts.FirstOrDefault(c => c.From == uuid);
            return skipping != null;
        }
        if (parent == (skipping.At ?? "") && line.Contains("\"type\":\"user\"", StringComparison.Ordinal))
        {
            skipping = cuts.FirstOrDefault(c => c.From == uuid);
            return skipping != null;
        }
        return true;
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

    // one streamed line of a turn: its steps and answer go to onEntry; true when it is the line that ends the turn
    internal static bool ReadLine(string line, Action<SessionEntry> onEntry, Action<JsonElement>? onLine)
    {
        if (!line.StartsWith('{')) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            onLine?.Invoke(root);
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
                return true;
            }
        }
        catch (JsonException)
        {
            // not a whole JSON line: skip it
        }
        return false;
    }

    /// <summary>
    /// A session's claude process kept open for many turns (--input-format stream-json): it starts with the user's
    /// whole setup once, not once per turn. Measured: a new process took 11.7-13.7 s for a one-line answer, a later
    /// turn of an open one 2.9-3.0 s, and the open process still knew the earlier turns. A stopped or failed turn
    /// ends the process; the next turn starts a new one that resumes the session.
    /// </summary>
    public sealed class LiveSession : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stderr = new();
        private bool _broken;

        /// <summary>The folder, session id and arguments it was started with: a turn that needs others needs another process.</summary>
        public string Key { get; }

        public bool Usable => !_broken && !_process.HasExited;

        // the process's running total at its last result (a resumed one may carry on the session's recorded total)
        private double _costSoFar;

        private LiveSession(Process process, string key)
        {
            _process = process;
            Key = key;
        }

        /// <summary>What a turn cost, from its result's total_cost_usd - the process's total so far, not the turn's.</summary>
        public double TurnCost(double total)
        {
            // a total below the recorded one: this process did not carry the session's total on, it counts from 0
            if (total < _costSoFar) _costSoFar = 0;
            double cost = total - _costSoFar;
            _costSoFar = total;
            return cost;
        }

        public static LiveSession Start(string folder, string id, bool isNew, IEnumerable<string>? extraArgs)
        {
            string[] extra = (extraArgs ?? []).ToArray();
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
            foreach (string arg in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-mode", "bypassPermissions" })
            {
                start.ArgumentList.Add(arg);
            }
            start.ArgumentList.Add(isNew ? "--session-id" : "--resume");
            start.ArgumentList.Add(id);
            foreach (string arg in extra) start.ArgumentList.Add(arg);
            CliSetup.UseCompactWindow(start);
            Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start claude");
            var live = new LiveSession(process, KeyOf(folder, id, extra)) { _costSoFar = isNew ? 0 : LastCost(id) };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (live._stderr) live._stderr.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();
            return live;
        }

        public static string KeyOf(string folder, string id, IEnumerable<string>? extraArgs) =>
            folder + "|" + id + "|" + string.Join(" ", extraArgs ?? []);

        // claude reads stdin only once it is initialized: written before, a message bigger than the pipe's buffer
        // blocks the writer and the turn never starts (two factory runs hung so; in a test 6,000- and 20,000-character
        // messages blocked without this and went through with it). So, like the Agent SDK, the first thing written is
        // an initialize request, and the turn waits for its answer - at most InitTimeout.
        private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(60);
        private bool _initialized;

        private async Task InitializeAsync(CancellationToken cancel)
        {
            await _process.StandardInput.WriteLineAsync("""{"type":"control_request","request_id":"devbreak_init","request":{"subtype":"initialize"}}""");
            await _process.StandardInput.FlushAsync();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(InitTimeout);
            try
            {
                while (await _process.StandardOutput.ReadLineAsync(limit.Token) is { } line)
                {
                    if (line.StartsWith('{') && line.Contains("\"control_response\"", StringComparison.Ordinal))
                    {
                        _initialized = true;
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new TimeoutException($"claude did not answer its initialize request in {InitTimeout.TotalSeconds:0} s");
            }
            throw new IOException("claude exited before it was initialized");
        }

        /// <summary>One turn: the prompt goes in as the next user message; it ends at the turn's result line.</summary>
        public async Task TurnAsync(string prompt, Action<SessionEntry> onEntry, CancellationToken cancel, Action<JsonElement>? onLine = null)
        {
            bool finished = false;
            using (cancel.Register(Dispose))
            {
                try
                {
                    if (!_initialized) await InitializeAsync(cancel);
                    string message = JsonSerializer.Serialize(new
                    {
                        type = "user",
                        message = new { role = "user", content = new[] { new { type = "text", text = prompt } } },
                    });
                    await _process.StandardInput.WriteLineAsync(message);
                    await _process.StandardInput.FlushAsync();
                    while (await _process.StandardOutput.ReadLineAsync() is { } line)
                    {
                        if (ReadLine(line, onEntry, onLine))
                        {
                            finished = true;
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // the process went away under the read (stopped, or it died): reported below
                }
                catch (TimeoutException ex)
                {
                    Dispose();
                    onEntry(new SessionEntry(SessionEntryKind.Error, ex.Message));
                    return;
                }
            }
            if (cancel.IsCancellationRequested)
            {
                onEntry(new SessionEntry(SessionEntryKind.Error, "Stopped."));
            }
            else if (!finished)
            {
                _broken = true;
                string err;
                lock (_stderr) err = _stderr.ToString().Trim();
                string code = _process.HasExited ? $" with {_process.ExitCode}" : "";
                onEntry(new SessionEntry(SessionEntryKind.Error, $"claude exited{code}" + (err.Length > 0 ? ": " + err : "")));
            }
        }

        /// <summary>Ends the process and everything it started.</summary>
        public void Dispose()
        {
            _broken = true;
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
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
    internal static string ToolSummary(JsonElement block)
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

    // what the user typed: a string (a message from the terminal or -p) or text blocks (a message written as stream-json,
    // as Sessions and the Factory send them); a line of tool results, or the CLI's "[Request interrupted ...]", is not
    private static string? Typed(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var text = new StringBuilder();
        foreach (JsonElement block in content.EnumerateArray())
        {
            string kind = block.TryGetProperty("type", out JsonElement k) ? k.GetString() ?? "" : "";
            if (kind == "tool_result") return null;
            if (kind == "text" && block.TryGetProperty("text", out JsonElement t)) text.Append(t.GetString());
        }
        string typed = text.ToString();
        return typed.StartsWith("[Request interrupted", StringComparison.Ordinal) ? null : typed;
    }

    // the text inside <tag>...</tag> when the message starts with that tag (after white space), else null
    private static string? Tag(string text, string tag)
    {
        string trimmed = text.TrimStart();
        string open = "<" + tag + ">", close = "</" + tag + ">";
        int start = trimmed.IndexOf(open, StringComparison.Ordinal);
        // the command's tags come in either order (<command-message> first in older files)
        if (start < 0 || (start > 0 && !(tag.StartsWith("command-", StringComparison.Ordinal) && trimmed.StartsWith("<command-", StringComparison.Ordinal)))) return null;
        int end = trimmed.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : trimmed[(start + open.Length)..end].Trim();
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

    /// <summary>
    /// The line a message follows in the session file (its parentUuid). Measured: the echo of a message
    /// (--replay-user-messages) carries its uuid but no parentUuid, and a process that just resumed has seen no line
    /// before it - the file has it.
    /// </summary>
    public static string? ParentOf(string id, string uuid)
    {
        if (FileOf(id) is not { } file) return null;
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains(uuid, StringComparison.Ordinal)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("uuid", out JsonElement u) && u.GetString() == uuid)
                {
                    return root.TryGetProperty("parentUuid", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                }
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// The session's running cost as its file last recorded it (the last cost-state line's totalCostUSD), 0 without one.
    /// Measured: a process resuming the session carries this total on in its results' total_cost_usd.
    /// </summary>
    public static double LastCost(string id) => FileOf(id) is { } file ? LastCostIn(file) : 0;

    // the end of the file first; the CLI writes the line rarely, so it can lie further back (424 KB from the end of a
    // 13 MB session: missed, its next turn showed the whole session's $7.53 as its own cost) - then the whole file
    internal static double LastCostIn(string file) => LastCostIn(file, 256 * 1024) ?? LastCostIn(file, long.MaxValue) ?? 0;

    private static double? LastCostIn(string file, long tail)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - tail), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            double? cost = null;
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"cost-state\"", StringComparison.Ordinal)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("totalCostUSD", out JsonElement c) && c.ValueKind == JsonValueKind.Number) cost = c.GetDouble();
                }
                catch (JsonException)
                {
                }
            }
            return cost;
        }
        catch (IOException)
        {
            return null;
        }
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
