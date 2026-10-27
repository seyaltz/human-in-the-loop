using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>
/// The user's own Claude Code history, read from their session files: where the time of their turns went (waiting,
/// tools, the model, them), how often calls failed, how long a command usually takes, which commands they keep
/// re-deriving. The rules are the audit's (docs/specs/2026-10-27-watch-and-learn-design.md, "The audit's rules"). It
/// reads in the background; each file's numbers are cached next to the exe by its size and time, so only files that
/// changed are read again. Only files written in the last five weeks are read: the Time view shows four.
/// </summary>
public static class SessionStats
{
    public static readonly Regex Correction = new(
        @"\b(no[, ]|wrong|not what|why did you|you didn'?t|stop|again|still (not|doesn)|doesn'?t work|not working|broke)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Automated = new(@"^(You are grading|You are an? (evaluator|judge|classifier)|Reply with ONLY)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly Regex WaitCommand = new(@"\b(sleep|Start-Sleep|until |timeout /t)\b", RegexOptions.Compiled);

    private static readonly HashSet<string> Counted = ["cli", "claude-desktop", "sdk-cli"];
    private static readonly TimeSpan Window = TimeSpan.FromDays(35);
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(15);

    private static string CacheFile => Path.Combine(AppContext.BaseDirectory, "session-stats.json");
    private static string PaneFile => Path.Combine(AppContext.BaseDirectory, "pane-turns.json");
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private static readonly object Gate = new();
    private static Task? _reading;

    /// <summary>The last finished read, or null before the first one.</summary>
    public static Data? Current { get; private set; }
    public static bool Ready => Current != null;
    public static event Action? Changed;

    /// <summary>Reads (again) in the background; files that did not change come from the cache.</summary>
    public static Task RefreshAsync()
    {
        lock (Gate)
        {
            if (_reading is { IsCompleted: false }) return _reading;
            _reading = Task.Run(() =>
            {
                List<FileStats>? cache = null;
                try
                {
                    if (File.Exists(CacheFile)) cache = JsonSerializer.Deserialize<List<FileStats>>(File.ReadAllText(CacheFile));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    cache = null;
                }
                Data data = Scan(Root, cache);
                Current = data;
                try
                {
                    File.WriteAllText(CacheFile, JsonSerializer.Serialize(data.Files));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // no cache next time: it is read again
                }
                Changed?.Invoke();
            });
            return _reading;
        }
    }

    /// <summary>The median of a command family's past runs, from 3 runs on; null with fewer.</summary>
    public static TimeSpan? Usual(string key) => Current?.Usual(key);

    // command families that are not a procedure: an interpreter running an inline script, or a basic shell word
    private static readonly HashSet<string> Interpreters = ["python", "python3", "py", "node", "bash", "sh", "powershell", "pwsh", "ruby", "perl"];
    private static readonly HashSet<string> ShellWords =
        ["cat", "echo", "for", "while", "if", "cd", "ls", "dir", "grep", "rg", "sed", "awk", "head", "tail", "sleep", "until", "export", "set", "find", "wc", "test", "true", "$s", "$p"];

    /// <summary>
    /// The families worth a skill: not one an installed skill already names (all of the family's words in a skill's
    /// name or description), and not an inline script or a basic shell word - those are how work is done, not what.
    /// </summary>
    public static List<CommandFamily> Uncovered(IEnumerable<CommandFamily> families, IEnumerable<string> skills)
    {
        List<string> texts = [.. skills.Select(s => s.ToLowerInvariant())];
        return [.. families.Where(f =>
        {
            string[] words = f.Key.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || ShellWords.Contains(words[0]) || words[0].StartsWith('$')) return false;
            if (Interpreters.Contains(words[0]) && (words.Length == 1 || words[1].StartsWith('-'))) return false;
            return !texts.Any(t => words.All(w => t.Contains(w)));
        })];
    }

    /// <summary>A Sessions pane's turn, exactly timed from its result line: the Time view uses these for the pane's session.</summary>
    public static void RecordPaneTurn(string sessionId, DateTime start, double seconds)
    {
        lock (Gate)
        {
            List<PaneTurn> turns = ReadPaneTurns();
            turns.Add(new PaneTurn { Session = sessionId, Start = start, Seconds = seconds });
            turns.RemoveAll(t => t.Start < DateTime.Now - Window);
            try
            {
                File.WriteAllText(PaneFile, JsonSerializer.Serialize(turns));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // not kept: the Time view estimates that turn instead
            }
        }
    }

    private static List<PaneTurn> ReadPaneTurns()
    {
        try
        {
            return File.Exists(PaneFile) ? JsonSerializer.Deserialize<List<PaneTurn>>(File.ReadAllText(PaneFile)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Reads every session file under <paramref name="root"/> written in the window; <paramref name="cache"/>'s unchanged files are kept as they are.</summary>
    public static Data Scan(string root, List<FileStats>? cache)
    {
        Dictionary<string, FileStats> known = (cache ?? []).GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.First());
        var files = new List<FileStats>();
        if (Directory.Exists(root))
        {
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                foreach (string path in Directory.EnumerateFiles(dir, "*.jsonl"))
                {
                    FileInfo info;
                    try
                    {
                        info = new FileInfo(path);
                        if (info.LastWriteTime < DateTime.Now - Window) continue;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    if (known.TryGetValue(path, out FileStats? same) && same.Size == info.Length && same.Ticks == info.LastWriteTimeUtc.Ticks)
                    {
                        files.Add(same);
                        continue;
                    }
                    try
                    {
                        files.Add(ReadFile(path, info));
                    }
                    catch (IOException)
                    {
                        // locked or gone: next time
                    }
                }
            }
        }
        List<PaneTurn> pane = ReadPaneTurns();
        foreach (FileStats file in files.Where(f => f.Entry == "sdk-cli"))
        {
            // a pane's own turns are timed exactly: they replace the estimate for the weeks they fall in
            foreach (IGrouping<string, PaneTurn> week in pane.Where(t => t.Session == file.Session).GroupBy(t => WeekOf(t.Start)))
            {
                if (file.Weeks.TryGetValue(week.Key, out WeekStats? stats)) stats.ActiveH = week.Sum(t => t.Seconds) / 3600;
            }
        }
        return new Data(files);
    }

    private static FileStats ReadFile(string path, FileInfo info)
    {
        var stats = new FileStats
        {
            Path = path, Size = info.Length, Ticks = info.LastWriteTimeUtc.Ticks,
            Session = System.IO.Path.GetFileNameWithoutExtension(path),
        };
        var rows = new List<JsonElement>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith('{')) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    if (stats.Entry.Length == 0 && Str(root, "entrypoint") is { Length: > 0 } entry) stats.Entry = entry;
                    if (stats.Folder.Length == 0 && Str(root, "cwd") is { Length: > 0 } cwd) stats.Folder = cwd;
                    if (Str(root, "timestamp").Length > 0) rows.Add(root.Clone());
                }
                catch (JsonException)
                {
                    // a line being written right now, or not JSON: skipped
                }
            }
        }
        if (!Counted.Contains(stats.Entry)) return stats;
        string? first = rows.Select(TypedPrompt).FirstOrDefault(p => p != null);
        if (first == null || Automated.IsMatch(first)) return stats;
        stats.Counts = true;
        bool timed = rows.Any(r => Str(r, "type") == "system" && Str(r, "subtype") == "turn_duration");
        var pending = new Dictionary<string, (string Tool, DateTime Start, string Key, string Command)>();
        DateTime? previous = null, turnStart = null;
        double gaps = 0;
        string turnWeek = "";

        void EndTurn()
        {
            if (turnStart != null && !timed) Week(stats, turnWeek).ActiveH += gaps / 3600;
            gaps = 0;
        }

        foreach (JsonElement row in rows)
        {
            if (!DateTime.TryParse(Str(row, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)) continue;
            string week = WeekOf(t.ToLocalTime());
            string type = Str(row, "type");
            TimeSpan gap = previous is { } p ? t - p : TimeSpan.Zero;
            previous = t;
            if (TypedPrompt(row) is { } prompt)
            {
                EndTurn();
                turnStart = t;
                turnWeek = week;
                WeekStats w = Week(stats, week);
                if (prompt.Contains("[Request interrupted", StringComparison.Ordinal)) w.Interrupts++;
                else if (Correction.IsMatch(prompt.Length > 200 ? prompt[..200] : prompt)) w.Corrections++;
                continue;
            }
            if (turnStart != null && gap > TimeSpan.Zero && gap < MaxGap) gaps += gap.TotalSeconds;
            if (type == "system" && Str(row, "subtype") == "turn_duration" && row.TryGetProperty("durationMs", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number)
            {
                Week(stats, week).ActiveH += ms.GetDouble() / 1000 / 3600;
                continue;
            }
            if (type == "assistant" && gap > TimeSpan.Zero && gap < MaxGap) Week(stats, week).ModelH += gap.TotalSeconds / 3600;
            if (!row.TryGetProperty("message", out JsonElement message) || !message.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (JsonElement block in content.EnumerateArray())
            {
                string kind = Str(block, "type");
                if (kind == "text" && Str(block, "text").Contains("[Request interrupted", StringComparison.Ordinal)) Week(stats, week).Interrupts++;
                if (type == "assistant" && kind == "tool_use")
                {
                    JsonElement input = block.TryGetProperty("input", out JsonElement i) ? i : default;
                    string command = Str(input, "command");
                    string tool = Str(block, "name");
                    pending[Str(block, "id")] = (tool, t, command.Length > 0 ? TurnLog.KeyOf(command) : "", command);
                }
                else if (type == "user" && kind == "tool_result" && pending.Remove(Str(block, "tool_use_id"), out var call))
                {
                    double seconds = (t - call.Start).TotalSeconds;
                    if (seconds < 0 || seconds > 3600) continue;
                    WeekStats w = Week(stats, week);
                    bool error = block.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True;
                    w.Calls++;
                    w.ToolH += seconds / 3600;
                    if (error) w.Failed++;
                    if (call.Tool is "AskUserQuestion" or "ExitPlanMode")
                    {
                        w.UserH += seconds / 3600;
                    }
                    else if (call.Tool == "Monitor" || WaitCommand.IsMatch(call.Command))
                    {
                        w.WaitH += seconds / 3600;
                        string waitKey = call.Key.Length > 0 ? call.Key : call.Tool;
                        w.WaitByKey[waitKey] = w.WaitByKey.GetValueOrDefault(waitKey) + seconds / 3600;
                    }
                    if (call.Key.Length == 0) continue;
                    if (error) w.FailByKey[call.Key] = w.FailByKey.GetValueOrDefault(call.Key) + 1;
                    if (!stats.Durations.TryGetValue(call.Key, out List<double>? list)) stats.Durations[call.Key] = list = [];
                    list.Add(seconds);
                    stats.Calls[call.Key] = stats.Calls.GetValueOrDefault(call.Key) + 1;
                    if (error) stats.Fails[call.Key] = stats.Fails.GetValueOrDefault(call.Key) + 1;
                    if (!stats.Examples.TryGetValue(call.Key, out List<string>? examples)) stats.Examples[call.Key] = examples = [];
                    string example = call.Command.Length > 200 ? call.Command[..200] : call.Command;
                    if (examples.Count < 3 && !examples.Contains(example)) examples.Add(example);
                }
            }
        }
        EndTurn();
        return stats;
    }

    private static WeekStats Week(FileStats stats, string week)
    {
        if (!stats.Weeks.TryGetValue(week, out WeekStats? w)) stats.Weeks[week] = w = new WeekStats(DateTime.ParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        return w;
    }

    // the Monday a moment's week starts on
    internal static string WeekOf(DateTime t) => t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // a prompt the user typed: not a tool result, a command's output, a hook's note, a summary or a background task's notice
    private static string? TypedPrompt(JsonElement row)
    {
        if (Str(row, "type") != "user" || IsTrue(row, "isMeta") || IsTrue(row, "isSidechain")) return null;
        if (!row.TryGetProperty("message", out JsonElement message) || !message.TryGetProperty("content", out JsonElement content)) return null;
        string text;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString()!;
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            if (content.EnumerateArray().Any(b => Str(b, "type") == "tool_result")) return null;
            text = string.Join(" ", content.EnumerateArray().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")));
        }
        else
        {
            return null;
        }
        if (text.StartsWith("<command-", StringComparison.Ordinal) || text.StartsWith("<local-command", StringComparison.Ordinal)
            || text.StartsWith("Caveat:", StringComparison.Ordinal) || text.StartsWith("This session is being continued", StringComparison.Ordinal)
            || text.Contains("<task-notification>", StringComparison.Ordinal))
        {
            return null;
        }
        return text;
    }

    private static bool IsTrue(JsonElement e, string key) => e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement e, string key) => TurnLog.Str(e, key);

    /// <summary>One week's numbers: hours of active time and its parts, calls, failures, interruptions, corrections.</summary>
    public sealed class WeekStats(DateTime start)
    {
        public DateTime Start { get; set; } = start;
        public double ActiveH { get; set; }
        public double WaitH { get; set; }
        public double ToolH { get; set; }
        public double ModelH { get; set; }
        public double UserH { get; set; }
        public int Calls { get; set; }
        public int Failed { get; set; }
        public int Interrupts { get; set; }
        public int Corrections { get; set; }
        public Dictionary<string, double> WaitByKey { get; set; } = [];
        public Dictionary<string, int> FailByKey { get; set; } = [];

        public List<(string Key, double Hours)> TopWaits(int n) => [.. WaitByKey.OrderByDescending(x => x.Value).Take(n).Select(x => (x.Key, x.Value))];
        public List<(string Key, int Failed)> TopFailing(int n) => [.. FailByKey.OrderByDescending(x => x.Value).Take(n).Select(x => (x.Key, x.Value))];

        public void Add(WeekStats other)
        {
            ActiveH += other.ActiveH;
            WaitH += other.WaitH;
            ToolH += other.ToolH;
            ModelH += other.ModelH;
            UserH += other.UserH;
            Calls += other.Calls;
            Failed += other.Failed;
            Interrupts += other.Interrupts;
            Corrections += other.Corrections;
            foreach ((string key, double h) in other.WaitByKey) WaitByKey[key] = WaitByKey.GetValueOrDefault(key) + h;
            foreach ((string key, int f) in other.FailByKey) FailByKey[key] = FailByKey.GetValueOrDefault(key) + f;
        }
    }

    /// <summary>One session file's numbers, as cached.</summary>
    public sealed class FileStats
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public long Ticks { get; set; }
        public string Session { get; set; } = "";
        public string Entry { get; set; } = "";
        public string Folder { get; set; } = "";
        public bool Counts { get; set; }
        public Dictionary<string, WeekStats> Weeks { get; set; } = [];
        public Dictionary<string, List<double>> Durations { get; set; } = [];
        public Dictionary<string, int> Calls { get; set; } = [];
        public Dictionary<string, int> Fails { get; set; } = [];
        public Dictionary<string, List<string>> Examples { get; set; } = [];
    }

    public sealed class PaneTurn
    {
        public string Session { get; set; } = "";
        public DateTime Start { get; set; }
        public double Seconds { get; set; }
    }

    /// <summary>A shell command family across sessions: how many sessions ran it, how often it failed, a few examples and where.</summary>
    public sealed record CommandFamily(string Key, int Sessions, int Calls, int Failed, List<string> Examples, List<string> Folders);

    /// <summary>A finished read.</summary>
    public sealed class Data(List<FileStats> files)
    {
        public List<FileStats> Files { get; } = files;

        /// <summary>The last <paramref name="n"/> weeks with any time in them, newest first.</summary>
        public List<WeekStats> Weeks(int n)
        {
            var weeks = new Dictionary<DateTime, WeekStats>();
            foreach (FileStats file in Files.Where(f => f.Counts))
            {
                foreach (WeekStats w in file.Weeks.Values)
                {
                    if (!weeks.TryGetValue(w.Start, out WeekStats? sum)) weeks[w.Start] = sum = new WeekStats(w.Start);
                    sum.Add(w);
                }
            }
            return [.. weeks.Values.OrderByDescending(w => w.Start).Take(n)];
        }

        public TimeSpan? Usual(string key)
        {
            List<double> all = [.. Files.Where(f => f.Counts && f.Durations.ContainsKey(key)).SelectMany(f => f.Durations[key]).OrderBy(x => x)];
            return all.Count < 3 ? null : TimeSpan.FromSeconds(all[all.Count / 2]);
        }

        /// <summary>Shell command families run in at least <paramref name="minSessions"/> sessions, most sessions first.</summary>
        public List<CommandFamily> Families(int minSessions)
        {
            var counted = Files.Where(f => f.Counts).ToList();
            return [.. counted.SelectMany(f => f.Calls.Keys).Distinct()
                .Select(key =>
                {
                    var with = counted.Where(f => f.Calls.ContainsKey(key)).ToList();
                    return new CommandFamily(key, with.Count, with.Sum(f => f.Calls[key]), with.Sum(f => f.Fails.GetValueOrDefault(key)),
                        [.. with.SelectMany(f => f.Examples.GetValueOrDefault(key) ?? []).Distinct().Take(5)],
                        [.. with.Select(f => f.Folder).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(5)]);
                })
                .Where(f => f.Sessions >= minSessions)
                .OrderByDescending(f => f.Sessions)];
        }
    }
}
