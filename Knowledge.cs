using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>Where the "who is this developer" text in the question prompt comes from.</summary>
public enum KnowledgeSource { None, File, Claude }

/// <summary>
/// The knowledge about the user that steers questions to their stack: nothing, their own profile.txt,
/// or a once-a-day summary of their recent Claude Code prompts.
/// </summary>
public static class Knowledge
{
    private const int HistoryDays = 7;
    private const int MaxPrompts = 200;
    private const int MaxPromptChars = 300;

    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly string ProfilePath = Path.Combine(AppContext.BaseDirectory, "profile.txt");
    private static readonly string ClaudeSummaryPath = Path.Combine(AppContext.BaseDirectory, "knowledge-from-claude.txt");
    private static readonly string ProjectsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private static Task<string>? _claudeSummary;
    private static DateTime _claudeSummaryDay;

    public static KnowledgeSource Source { get; private set; } = LoadSource();

    public static void SetSource(KnowledgeSource source)
    {
        Source = source;
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["knowledge"] = source.ToString() }));
    }

    public static async Task<string> GetAsync() => Source switch
    {
        KnowledgeSource.File => File.Exists(ProfilePath) ? (await File.ReadAllTextAsync(ProfilePath)).Trim() : "",
        KnowledgeSource.Claude => await ClaudeSummaryAsync(),
        _ => "",
    };

    private static KnowledgeSource LoadSource()
    {
        if (!File.Exists(SettingsPath)) return KnowledgeSource.File;
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath));
        return settings != null && settings.TryGetValue("knowledge", out string? value)
            && Enum.TryParse(value, out KnowledgeSource source) ? source : KnowledgeSource.File;
    }

    /// <summary>Built once a day and shared by every question generated that day.</summary>
    private static Task<string> ClaudeSummaryAsync()
    {
        DateTime today = DateTime.Today;
        if (_claudeSummary == null || _claudeSummaryDay != today || _claudeSummary.IsFaulted)
        {
            _claudeSummaryDay = today;
            _claudeSummary = File.Exists(ClaudeSummaryPath) && File.GetLastWriteTime(ClaudeSummaryPath).Date == today
                ? File.ReadAllTextAsync(ClaudeSummaryPath)
                : BuildClaudeSummaryAsync();
        }
        return _claudeSummary;
    }

    private static async Task<string> BuildClaudeSummaryAsync()
    {
        DateTime since = DateTime.Now.AddDays(-HistoryDays);
        var projects = new List<string>();
        var prompts = new List<(DateTime At, string Text)>();
        if (Directory.Exists(ProjectsDir))
        {
            foreach (string dir in Directory.GetDirectories(ProjectsDir))
            {
                string[] sessions = Directory.GetFiles(dir, "*.jsonl").Where(f => File.GetLastWriteTime(f) >= since).ToArray();
                if (sessions.Length == 0) continue;
                projects.Add(Path.GetFileName(dir));
                foreach (string session in sessions)
                {
                    prompts.AddRange(ReadTypedPrompts(session, since));
                }
            }
        }

        var recent = new StringBuilder();
        foreach ((DateTime _, string text) in prompts.OrderByDescending(p => p.At).Take(MaxPrompts))
        {
            string line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            recent.Append("- ").AppendLine(line.Length > MaxPromptChars ? line[..MaxPromptChars] : line);
        }

        string prompt = $"""
            Below are a developer's own recent prompts to an AI coding assistant (last {HistoryDays} days) and the names of
            the project folders they worked in. Describe this developer for a quiz app that sets questions in their world:
            their role, languages, frameworks, databases, cloud and tools, the kind of product they build, and what they
            worked on lately. Under 150 words, plain text.
            Leave out secrets, credentials, people's names, customer data and anything else private.

            PROJECT FOLDERS:
            {string.Join("\n", projects)}

            PROMPTS (newest first):
            {recent}
            """;
        string summary = (await QuestionGenerator.RunClaudeAsync(prompt)).Trim();
        await File.WriteAllTextAsync(ClaudeSummaryPath, summary);
        return summary;
    }

    /// <summary>The prompts the user typed themselves, not tool results, hook text or system notices.</summary>
    private static IEnumerable<(DateTime At, string Text)> ReadTypedPrompts(string session, DateTime since)
    {
        using var stream = new FileStream(session, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"human\"")) continue;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            using (document)
            {
                (DateTime At, string Text)? typed = TypedPrompt(document.RootElement, since);
                if (typed != null) yield return typed.Value;
            }
        }
    }

    private static (DateTime At, string Text)? TypedPrompt(JsonElement root, DateTime since)
    {
        if (root.TryGetProperty("type", out JsonElement type) && type.GetString() == "user"
            && root.TryGetProperty("origin", out JsonElement origin) && origin.ValueKind == JsonValueKind.Object
            && origin.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "human"
            && root.TryGetProperty("message", out JsonElement message)
            && message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String
            && root.TryGetProperty("timestamp", out JsonElement timestamp) && timestamp.TryGetDateTime(out DateTime at)
            && at.ToLocalTime() >= since)
        {
            return (at.ToLocalTime(), content.GetString()!);
        }
        return null;
    }
}
