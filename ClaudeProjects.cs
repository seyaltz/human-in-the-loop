using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>A folder the user works in with Claude Code, found from their own Claude Code sessions.</summary>
public sealed record ClaudeProject(string Key, string Folder, string Summary);

/// <summary>What Claude suggests saving for one project: a memory note, or a skill.</summary>
public sealed record ShareSuggestion(ClaudeProject Project, string Kind, string Name, string Description, string Content);

/// <summary>
/// The user's Claude Code projects (from %USERPROFILE%\.claude\projects, nothing is hard-coded) and saving to them:
/// a memory note in that project's Claude Code memory, or a skill in the project's .claude\skills.
/// Everything is read from and written to this machine only.
/// </summary>
public static class ClaudeProjects
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // DEVBREAK_CLAUDE_PROJECTS points at another projects folder (a sandbox for tests and demos)
    private static readonly string ProjectsDir =
        Environment.GetEnvironmentVariable("DEVBREAK_CLAUDE_PROJECTS") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".claude", "projects");

    /// <summary>
    /// The projects whose folder still exists. Each Claude Code session log records its working folder (cwd), which is
    /// the reliable way back from the encoded project key to the real path. Temp folders and the home folder are skipped.
    /// </summary>
    public static IReadOnlyList<ClaudeProject> Find()
    {
        var projects = new List<ClaudeProject>();
        if (!Directory.Exists(ProjectsDir)) return projects;
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        foreach (string dir in Directory.GetDirectories(ProjectsDir))
        {
            string? folder = FolderOf(dir);
            if (folder == null || !Directory.Exists(folder)) continue;
            string full = Path.GetFullPath(folder).TrimEnd('\\');
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || full.Equals(Home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            if (projects.Any(p => p.Folder.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
            projects.Add(new ClaudeProject(Path.GetFileName(dir), full, Fingerprint(full)));
        }
        return projects;
    }

    private static string? FolderOf(string projectDir)
    {
        foreach (string log in Directory.GetFiles(projectDir, "*.jsonl").OrderByDescending(File.GetLastWriteTime).Take(3))
        {
            try
            {
                using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                for (int i = 0; i < 60 && reader.ReadLine() is { } line; i++)
                {
                    if (!line.Contains("\"cwd\"", StringComparison.Ordinal)) continue;
                    using JsonDocument doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("cwd", out JsonElement cwd) && cwd.GetString() is { Length: > 0 } path) return path;
                }
            }
            catch (Exception)
            {
                // a log being written or not JSON: try the next one
            }
        }
        return null;
    }

    /// <summary>A short description of a folder for Claude: its top-level entries and the start of its CLAUDE.md or README.</summary>
    private static string Fingerprint(string folder)
    {
        var text = new StringBuilder();
        try
        {
            IEnumerable<string> entries = Directory.EnumerateFileSystemEntries(folder)
                .Select(Path.GetFileName).Where(n => n != null && !n.StartsWith('.')).Take(24)!;
            text.Append("contains: ").Append(string.Join(", ", entries));
            string? doc = new[] { "CLAUDE.md", "README.md" }.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
            if (doc != null)
            {
                string start = string.Join(" ", File.ReadLines(doc).Where(l => l.Trim().Length > 0).Take(4).Select(l => l.Trim()));
                text.Append("; ").Append(Path.GetFileName(doc)).Append(": ").Append(start.Length > 300 ? start[..300] : start);
            }
        }
        catch (Exception)
        {
            // no access: the path alone is still a hint
        }
        return text.ToString();
    }

    /// <summary>Claude's suggestions for where one TL;DR item belongs among the user's projects (none when it is not useful).</summary>
    public static async Task<IReadOnlyList<ShareSuggestion>> SuggestAsync(string topic, string item, IReadOnlyList<TldrSource> sources)
    {
        IReadOnlyList<ClaudeProject> projects = await Task.Run(Find);
        if (projects.Count == 0) return [];
        string list = string.Join("\n", projects.Select((p, i) => $"{i + 1}. {p.Folder} - {p.Summary}"));
        string prompt = $"""
            You help a developer keep their Claude Code setup up to date with what they learn. Below is one item from a
            TL;DR on {topic}, and the folders they work in with Claude Code, each with a short fingerprint.
            {await LearnPath.AboutThemAsync()}
            THE ITEM:
            {item}

            SOURCES OF THE TL;DR:
            {string.Join("\n", sources.Select(s => $"- {s.Title} | {s.Url}"))}

            THEIR PROJECTS:
            {list}

            Pick the projects this item is really useful for - at most 3, and none when it does not fit any of them.
            For each, choose:
            - memory: a fact, change or gotcha Claude should remember when working in that project (most items);
            - skill: only when the item is a procedure a developer would repeat there (steps to do something).
            Write the content for that project, concretely and in its terms. Include the source URL and the date
            {DateTime.Now:yyyy-MM-dd}, and end with a line saying to check it is still current before relying on it.
            Memory: under 120 words. Skill: numbered steps, under 300 words.

            Reply in exactly this format and nothing else, one block per project:
            TARGET: <project number> | <memory or skill> | <short-kebab-case-name> | <one-line description>
            <content>
            END
            Or reply with the single word NONE.
            """;
        string output = await QuestionGenerator.RunClaudeAsync(prompt);

        var suggestions = new List<ShareSuggestion>();
        string[] lines = output.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("TARGET:", StringComparison.Ordinal)) continue;
            string[] parts = lines[i]["TARGET:".Length..].Split('|').Select(p => p.Trim()).ToArray();
            int end = Array.FindIndex(lines, i + 1, l => l.Trim() == "END");
            if (end < 0) end = lines.Length;
            if (parts.Length >= 4 && int.TryParse(parts[0], out int n) && n >= 1 && n <= projects.Count
                && (parts[1] == "memory" || parts[1] == "skill"))
            {
                string content = string.Join("\n", lines[(i + 1)..end]).Trim();
                suggestions.Add(new ShareSuggestion(projects[n - 1], parts[1], Slug(parts[2]), parts[3], content));
            }
            i = end;
        }
        if (suggestions.Count == 0 && !output.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase) && !output.Contains("TARGET:"))
        {
            throw new InvalidOperationException("unexpected reply format:\n" + output);
        }
        return suggestions;
    }

    /// <summary>Saves a suggestion and returns the file written: never overwrites an existing note or skill.</summary>
    public static string Save(ShareSuggestion s, string content)
    {
        string front = $"---\nname: {s.Name}\ndescription: {s.Description.Replace("\n", " ")}\n";
        if (s.Kind == "memory")
        {
            // the project's Claude Code memory: one note per file, listed in MEMORY.md
            string dir = Path.Combine(ProjectsDir, s.Project.Key, "memory");
            Directory.CreateDirectory(dir);
            string file = Unique(n => Path.Combine(dir, n + ".md"), s.Name);
            File.WriteAllText(file, front + "metadata:\n  type: reference\n---\n\n" + content.Trim() + "\n");
            string index = Path.Combine(dir, "MEMORY.md");
            string line = $"- [{s.Name}]({Path.GetFileName(file)}) — {s.Description.Replace("\n", " ")}";
            string existing = File.Exists(index) ? File.ReadAllText(index) : "";
            File.AppendAllText(index, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + line + "\n");
            return file;
        }
        // a project skill: <project>\.claude\skills\<name>\SKILL.md
        string skills = Path.Combine(s.Project.Folder, ".claude", "skills");
        string skillDir = Unique(n => Path.Combine(skills, n), s.Name);
        Directory.CreateDirectory(skillDir);
        string skillFile = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillFile, front + "---\n\n" + content.Trim() + "\n");
        return skillFile;
    }

    private static string Unique(Func<string, string> pathFor, string name)
    {
        string path = pathFor(name);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = pathFor($"{name}-{i}");
        return path;
    }

    private static string Slug(string name)
    {
        string slug = string.Concat(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "note" : slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
