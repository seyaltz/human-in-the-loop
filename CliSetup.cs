using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>One thing Claude Code has in a folder: a skill, plugin, MCP server, agent, CLAUDE.md or memory note.</summary>
public sealed record SetupItem(string Name, string Description, string Source, string? File = null, string? Status = null);

/// <summary>What Claude Code has in a folder - read from the CLI itself, then filled in from the files it names.</summary>
public sealed record SetupInfo(
    string Folder, string Model, string Version, string PermissionMode, string OutputStyle, int Tools,
    IReadOnlyList<SetupItem> Memory, IReadOnlyList<SetupItem> Skills, IReadOnlyList<SetupItem> Plugins,
    IReadOnlyList<SetupItem> Mcp, IReadOnlyList<SetupItem> Agents);

/// <summary>
/// Reads the user's Claude Code setup for a folder the way the CLI sees it: claude starts there and reports its
/// setup (model, skills, plugins, MCP servers and their status, agents, memory folder) in its first line, and is
/// stopped right after that - before any answer is generated. Skill descriptions come from their SKILL.md files,
/// memory from the CLAUDE.md files that apply to the folder and the notes in its memory folder.
/// </summary>
public static class CliSetup
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static async Task<SetupInfo> LoadAsync(string folder)
    {
        JsonElement init = await InitAsync(folder);
        string Str(string key) => init.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        IEnumerable<JsonElement> Arr(string key) =>
            init.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

        var plugins = Arr("plugins").Select(p => new SetupItem(
            p.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
            p.TryGetProperty("version", out JsonElement ver) ? "version " + ver.GetString() : "",
            p.TryGetProperty("source", out JsonElement s) ? s.GetString() ?? "" : "",
            p.TryGetProperty("path", out JsonElement path) ? path.GetString() : null)).ToList();

        Dictionary<string, (string Description, string Source, string File)> known = KnownSkills(folder, plugins);
        var skills = Arr("skills").Select(s => s.GetString() ?? "").Where(s => s.Length > 0).Select(name =>
            known.TryGetValue(name, out var k) ? new SetupItem(name, k.Description, k.Source, k.File)
                : new SetupItem(name, "", name.Contains(':') ? "plugin " + name[..name.IndexOf(':')] : "built in")).ToList();

        var mcp = Arr("mcp_servers").Select(m => new SetupItem(
            m.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
            "", m.TryGetProperty("name", out JsonElement nn) && (nn.GetString() ?? "").StartsWith("plugin:") ? "from a plugin"
                : (nn.GetString() ?? "").StartsWith("claude.ai") ? "claude.ai connector" : "configured",
            null, m.TryGetProperty("status", out JsonElement st) ? st.GetString() : null)).ToList();

        var agents = Arr("agents").Select(a => a.GetString() ?? "").Where(a => a.Length > 0)
            .Select(a => new SetupItem(a, "", a.Contains(':') ? "plugin " + a[..a.IndexOf(':')] : "agent")).ToList();

        string? memoryDir = init.TryGetProperty("memory_paths", out JsonElement mp) && mp.TryGetProperty("auto", out JsonElement auto) ? auto.GetString() : null;
        return new SetupInfo(folder, Str("model"), Str("claude_code_version"), Str("permissionMode"), Str("output_style"),
            Arr("tools").Count(), Memory(folder, memoryDir), skills.OrderBy(s => s.Name).ToList(), plugins.OrderBy(p => p.Name).ToList(),
            mcp, agents);
    }

    /// <summary>
    /// Runs started with <c>--setting-sources ""</c> do not read the user's settings, so their auto-compact point would be
    /// near the full window (967k of 1M, measured): they get the user's own <c>autoCompactWindow</c> as the environment
    /// variable the CLI also reads (CLAUDE_CODE_AUTO_COMPACT_WINDOW, measured: 200000 -> compacts at 167k).
    /// </summary>
    public static void UseCompactWindow(ProcessStartInfo start)
    {
        const string Name = "CLAUDE_CODE_AUTO_COMPACT_WINDOW";
        if (Environment.GetEnvironmentVariable(Name) != null) return;
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        try
        {
            if (File.Exists(settings) && CompactWindowOf(File.ReadAllText(settings)) is { } window) start.Environment[Name] = window;
        }
        catch (IOException)
        {
            // unread this time: the run keeps the CLI's own window
        }
    }

    internal static string? CompactWindowOf(string settingsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(settingsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("autoCompactWindow", out JsonElement w)
                   && w.ValueKind == JsonValueKind.Number ? w.GetRawText() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The memory folder Claude Code uses for a folder (memory_paths.auto of its init line). A git worktree gets its
    /// main repository's folder, so what is saved there reaches sessions in the repo and in its worktrees.
    /// </summary>
    public static async Task<string?> MemoryDirAsync(string folder)
    {
        JsonElement init = await InitAsync(folder);
        return init.TryGetProperty("memory_paths", out JsonElement mp) && mp.TryGetProperty("auto", out JsonElement auto) ? auto.GetString() : null;
    }

    // starts claude in the folder and returns its init line; claude is stopped as soon as that line arrives
    private static async Task<JsonElement> InitAsync(string folder)
    {
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = folder, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence" }) start.ArgumentList.Add(arg);
        using Process process = await Task.Run(() => Process.Start(start)) ?? throw new InvalidOperationException("could not start claude");
        try
        {
            await process.StandardInput.WriteAsync("hi");
            process.StandardInput.Close();
            Task timeout = Task.Delay(TimeSpan.FromSeconds(90));
            while (true)
            {
                Task<string?> read = process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(read, timeout) == timeout) throw new TimeoutException("claude did not report its setup within 90 s");
                string? line = await read;
                if (line == null) throw new InvalidOperationException("claude ended before reporting its setup: " + await process.StandardError.ReadToEndAsync());
                if (!line.Contains("\"subtype\":\"init\"", StringComparison.Ordinal)) continue;
                using JsonDocument doc = JsonDocument.Parse(line);
                return doc.RootElement.Clone();
            }
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    // every skill file Claude Code reads: the user's, each plugin's (named plugin:skill) and the project's
    private static Dictionary<string, (string, string, string)> KnownSkills(string folder, IEnumerable<SetupItem> plugins)
    {
        var known = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        void Scan(string dir, string? prefix, string source)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string skillDir in Directory.GetDirectories(dir))
            {
                string file = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(file)) continue;
                (string? name, string description) = FrontMatter(file);
                name ??= Path.GetFileName(skillDir);
                known.TryAdd((prefix != null ? prefix + ":" : "") + name, (description, source, file));
            }
        }
        Scan(Path.Combine(folder, ".claude", "skills"), null, "this project");
        Scan(Path.Combine(Home, ".claude", "skills"), null, "your skills");
        foreach (SetupItem plugin in plugins.Where(p => p.File != null)) Scan(Path.Combine(plugin.File!, "skills"), plugin.Name, "plugin " + plugin.Name);
        return known;
    }

    // the CLAUDE.md files that apply to the folder (yours, then from the drive root down), then the memory notes
    private static List<SetupItem> Memory(string folder, string? memoryDir)
    {
        var items = new List<SetupItem>();
        void Add(string file, string source)
        {
            if (File.Exists(file) && items.All(i => !string.Equals(i.File, file, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new SetupItem(Path.GetFileName(file), Summary(file), source, file));
            }
        }
        Add(Path.Combine(Home, ".claude", "CLAUDE.md"), "your instructions, every project");
        var dirs = new List<DirectoryInfo>();
        for (DirectoryInfo? d = new(folder); d != null; d = d.Parent) dirs.Insert(0, d);
        foreach (DirectoryInfo d in dirs)
        {
            string where = d.FullName.Equals(new DirectoryInfo(folder).FullName, StringComparison.OrdinalIgnoreCase) ? "this project" : d.FullName;
            Add(Path.Combine(d.FullName, "CLAUDE.md"), where);
            Add(Path.Combine(d.FullName, ".claude", "CLAUDE.md"), where);
            Add(Path.Combine(d.FullName, "CLAUDE.local.md"), where + " (local)");
        }
        if (memoryDir != null && Directory.Exists(memoryDir))
        {
            Add(Path.Combine(memoryDir, "MEMORY.md"), "memory index");
            foreach (string note in Directory.GetFiles(memoryDir, "*.md").Where(f => !Path.GetFileName(f).Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
            {
                (string? name, string description) = FrontMatter(note);
                items.Add(new SetupItem(name ?? Path.GetFileNameWithoutExtension(note), description, "memory note", note));
            }
        }
        return items;
    }

    // a CLAUDE.md in one line: its first heading or first line of text
    private static string Summary(string file)
    {
        try
        {
            string? line = File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "---");
            return line == null ? "" : line.TrimStart('#', ' ');
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>name and description from a markdown file's front matter (description may be a folded block).</summary>
    private static (string? Name, string Description) FrontMatter(string file)
    {
        try
        {
            string[] lines = File.ReadLines(file).Take(60).ToArray();
            if (lines.Length == 0 || lines[0].Trim() != "---") return (null, "");
            string? name = null, description = "";
            for (int i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
            {
                string line = lines[i];
                if (line.StartsWith("name:", StringComparison.Ordinal)) name = Unquote(line[5..]);
                else if (line.StartsWith("description:", StringComparison.Ordinal))
                {
                    string value = line[12..].Trim();
                    if (value is ">" or "|" or ">-" or "|-" or "")
                    {
                        var block = new List<string>();
                        while (i + 1 < lines.Length && (lines[i + 1].StartsWith(' ') || lines[i + 1].StartsWith('\t'))) block.Add(lines[++i].Trim());
                        description = string.Join(" ", block);
                    }
                    else
                    {
                        description = Unquote(value);
                    }
                }
            }
            return (name, description);
        }
        catch (IOException)
        {
            return (null, "");
        }
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;
    }
}
