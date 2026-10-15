using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>
/// The user's own topic tabs, saved in topics.json next to the exe. On first run Claude suggests them from the user's
/// Claude Code prompts and the user picks; later the user adds more with AI help. Used from the UI thread only.
/// </summary>
public static class Topics
{
    private const int MaxSuggestions = 10;

    private static readonly string TopicsPath = Path.Combine(AppContext.BaseDirectory, "topics.json");

    // what every suggestion must stay clear of: only general, public high-tech knowledge
    private const string PublicTechOnly = """
        Only general, public high-tech topics (software, data, cloud, AI, security, networks, hardware, engineering
        practice). Never anything personal or private: no health, family, money or personal life, nothing about the
        user's employer, company, customers, products or business, no people's names, no internal project names.
        """;

    private static List<Category>? _chosen;

    /// <summary>Raised after the tabs change.</summary>
    public static event Action? Changed;

    /// <summary>False until the user has saved their topics once.</summary>
    public static bool IsSetUp => File.Exists(TopicsPath);

    /// <summary>Mix first, then the user's topics.</summary>
    public static IReadOnlyList<Category> All => [Category.Mix, .. Chosen()];

    public static IReadOnlyList<Category> Chosen() =>
        _chosen ??= File.Exists(TopicsPath)
            ? (JsonSerializer.Deserialize<List<Category>>(File.ReadAllText(TopicsPath)) ?? [])
            : [];

    public static void Save(IEnumerable<Category> chosen)
    {
        // keep the instances already in use, so open tabs and queued questions stay attached to them
        Dictionary<string, Category> current = Chosen().ToDictionary(c => c.Name);
        _chosen = chosen.Select(c => current.TryGetValue(c.Name, out Category? same) && same.Topics.SequenceEqual(c.Topics) ? same : c)
            .ToList();
        File.WriteAllText(TopicsPath, JsonSerializer.Serialize(_chosen));
        Changed?.Invoke();
    }

    /// <summary>
    /// Topics suggested from the user's own Claude Code prompts. Empty, without asking Claude, when there are no
    /// prompts yet.
    /// </summary>
    public static async Task<IReadOnlyList<Category>> SuggestAsync()
    {
        (IReadOnlyList<string> projects, string prompts, int count) = Knowledge.RecentPrompts();
        if (count == 0) return [];

        string prompt = $"""
            Below are a developer's own recent prompts to an AI coding assistant and the names of the project folders
            they worked in. Suggest up to {MaxSuggestions} topics this developer would gain from learning more about,
            based on the technologies and engineering problems they work with.
            {PublicTechOnly}
            Each topic is a short tab name (1-3 words, e.g. "PostgreSQL", "Kubernetes", "System design") with 4-8 sub-topics.

            Reply in exactly this format and nothing else, one topic per line:
            <tab name> | <sub-topic>; <sub-topic>; <sub-topic>; <sub-topic>

            PROJECT FOLDERS:
            {string.Join("\n", projects)}

            PROMPTS (newest first):
            {prompts}
            """;
        return Parse(await QuestionGenerator.RunClaudeAsync(prompt));
    }

    /// <summary>Turns what the user typed ("I want to learn ...") into one topic tab with sub-topics.</summary>
    public static async Task<Category> ShapeAsync(string wish)
    {
        string prompt = $"""
            A developer typed what they want to learn in a quiz and course app:
            "{wish.Trim()}"
            Turn it into one topic: a short tab name (1-4 words) and 4-8 sub-topics that cover it from the basics to
            what a senior professional should know.

            Reply in exactly this format and nothing else, on one line:
            <tab name> | <sub-topic>; <sub-topic>; <sub-topic>; <sub-topic>
            """;
        IReadOnlyList<Category> topics = Parse(await QuestionGenerator.RunClaudeAsync(prompt));
        return topics.Count > 0 ? topics[0] : throw new InvalidOperationException("unexpected reply format");
    }

    private static IReadOnlyList<Category> Parse(string output)
    {
        var topics = new List<Category>();
        foreach (string line in output.Split('\n'))
        {
            int bar = line.IndexOf('|');
            if (bar < 1) continue;
            string name = line[..bar].Trim().TrimStart('-', '*', ' ').Trim();
            string[] sub = line[(bar + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (name.Length == 0 || sub.Length == 0 || name == Category.Mix.Name) continue;
            if (topics.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            topics.Add(new Category(name, sub));
        }
        if (topics.Count == 0)
        {
            throw new InvalidOperationException("unexpected reply format:\n" + output);
        }
        return topics;
    }
}
