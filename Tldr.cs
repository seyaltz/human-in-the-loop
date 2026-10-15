using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>One part of a TL;DR, e.g. "Latest releases", with its bullets.</summary>
public sealed record TldrSection(string Title, string Text);

/// <summary>A tab's TL;DR and when it was written.</summary>
public sealed record TldrContent(DateTime Written, IReadOnlyList<TldrSection> Sections);

/// <summary>
/// The TL;DR: for each tab Claude fetches current data from the web and writes a short briefing - latest releases,
/// hot topics, quirks, what a senior must know and optimization tips. Cached under tldr\ next to the exe and written
/// again when it is a day old or on Refresh. Used from the UI thread only.
/// </summary>
public static class Tldr
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private static readonly string[] Titles =
        ["LATEST RELEASES", "HOT TOPICS", "INTERESTING QUIRKS", "WHAT A SENIOR MUST KNOW", "OPTIMIZATION TIPS"];

    private static readonly string TldrDir = Path.Combine(AppContext.BaseDirectory, "tldr");

    private static readonly Dictionary<string, Task<TldrContent>> Briefs = [];

    /// <summary>The tab's TL;DR: from disk while under a day old, otherwise (or on refresh) written again.</summary>
    public static Task<TldrContent> GetAsync(Category category, bool refresh = false)
    {
        if (refresh || !Briefs.TryGetValue(category.Name, out Task<TldrContent>? task) || task.IsFaulted
            || (task.IsCompletedSuccessfully && IsOld(task.Result)))
        {
            task = LoadOrWriteAsync(category, refresh);
            Briefs[category.Name] = task;
        }
        return task;
    }

    private static bool IsOld(TldrContent content) => DateTime.Now - content.Written >= MaxAge;

    private static string PathOf(Category category) =>
        Path.Combine(TldrDir, string.Concat(category.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-')) + ".json");

    private static async Task<TldrContent> LoadOrWriteAsync(Category category, bool refresh)
    {
        string path = PathOf(category);
        if (!refresh && File.Exists(path))
        {
            TldrContent saved = JsonSerializer.Deserialize<TldrContent>(await File.ReadAllTextAsync(path))!;
            if (!IsOld(saved)) return saved;
        }

        string prompt = $"""
            You are writing a TL;DR briefing on {category.Name} for a developer growing into a senior engineer.
            Scope (use as a guide): {string.Join(", ", category.Topics)}.
            {await LearnPath.AboutThemAsync()}
            Today is {DateTime.Now:yyyy-MM-dd}. First search the web and fetch the official sources (release notes,
            changelogs, official blogs and docs) to get the latest, current data. Do not rely on memory for anything
            recent, and give version numbers and dates.

            Write these sections, each 3 to 6 short "- " bullets, under 700 words in total:
            LATEST RELEASES: the newest releases and what they changed, with version and date.
            HOT TOPICS: what is new and getting attention right now.
            INTERESTING QUIRKS: surprising behaviour and gotchas.
            WHAT A SENIOR MUST KNOW: the things a senior engineer is expected to know.
            OPTIMIZATION TIPS: practical tips for performance and cost.
            Plain text; no markdown headings and no bold.
            Put every code snippet in a fenced block with its language, e.g. ```java, ```sql, ```javascript, ```bash.
            Short names inside a sentence go in single backticks.

            Reply in exactly this format and nothing else, each title alone on its line followed by its bullets:
            {string.Join("\n", Titles.Select(t => t + ":\n<bullets>"))}
            """;
        string output = await QuestionGenerator.RunClaudeAsync(prompt, web: true);

        var sections = new List<TldrSection>();
        int[] starts = Titles.Select(t => output.IndexOf(t + ":", StringComparison.Ordinal)).ToArray();
        for (int i = 0; i < Titles.Length; i++)
        {
            int end = i + 1 < Titles.Length ? starts[i + 1] : output.Length;
            if (starts[i] < 0 || end < starts[i])
            {
                throw new InvalidOperationException("unexpected reply format:\n" + output);
            }
            string title = Titles[i][0] + Titles[i][1..].ToLowerInvariant();
            sections.Add(new TldrSection(title, output[(starts[i] + Titles[i].Length + 1)..end].Trim()));
        }
        var content = new TldrContent(DateTime.Now, sections);
        Directory.CreateDirectory(TldrDir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(content));
        return content;
    }
}
