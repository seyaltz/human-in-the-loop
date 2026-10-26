using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>
/// Clear titles for sessions Claude Code did not title itself (no ai-title - their title is a clipped line of chat or
/// the first prompt): Claude writes one, once per session, from its first prompts and its last answer. Kept next to
/// the app in session-titles.json (the user's own data, never in the repo).
/// </summary>
public static class SessionTitles
{
    private static readonly string CachePath = Path.Combine(AppContext.BaseDirectory, "session-titles.json");
    private static Dictionary<string, string>? _cache;

    private static Dictionary<string, string> Cache
    {
        get
        {
            if (_cache != null) return _cache;
            try
            {
                _cache = File.Exists(CachePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) ?? []
                    : [];
            }
            catch (Exception)
            {
                _cache = [];
            }
            return _cache;
        }
    }

    /// <summary>The title Claude wrote for this session, if it has one.</summary>
    public static string? Get(string id) => Cache.GetValueOrDefault(id);

    /// <summary>
    /// Has Claude write a short topic title for a session from its first prompts and last answer, and keeps it.
    /// Null when the session has nothing the developer wrote yet.
    /// </summary>
    public static async Task<string?> WriteAsync(string id)
    {
        IReadOnlyList<SessionEntry> entries = await Task.Run(() => ClaudeSessions.Transcript(id));
        List<string> prompts = entries.Where(e => e.Kind == SessionEntryKind.You).Take(3).Select(e => Clip(e.Text, 500)).ToList();
        if (prompts.Count == 0) return null;
        string answer = entries.LastOrDefault(e => e.Kind == SessionEntryKind.Answer)?.Text ?? "";
        string prompt = $"""
            Name the topic of this Claude Code session in a short title, 3 to 7 words, like a good email subject:
            what it is about, not how it started. Same language as the developer. Reply with only the title.

            The developer's first messages:
            {string.Join("\n---\n", prompts)}

            Claude's last answer:
            {Clip(answer, 900)}
            """;
        string title = (await QuestionGenerator.RunClaudeAsync(prompt)).Trim().Trim('"', '\'', '*', '#', ' ', '.');
        title = title.Split('\n')[0].Trim();
        if (title.Length == 0) return null;
        if (title.Length > 90) title = title[..89] + "…";
        Cache[id] = title;
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(Cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // kept for this run only
        }
        return title;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
