using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>One part of a TL;DR, e.g. "Latest releases", with its bullets.</summary>
public sealed record TldrSection(string Title, string Text);

/// <summary>A web page the TL;DR was written from.</summary>
public sealed record TldrSource(string Title, string Url);

/// <summary>A tab's TL;DR, the pages it came from, and when it was written.</summary>
public sealed record TldrContent(DateTime Written, IReadOnlyList<TldrSection> Sections, IReadOnlyList<TldrSource>? Sources)
{
    /// <summary>The TL;DR as plain text, for the follow-up chat.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        foreach (TldrSection section in Sections)
        {
            text.AppendLine(section.Title.ToUpperInvariant() + ":").AppendLine(section.Text).AppendLine();
        }
        text.AppendLine("SOURCES:");
        foreach (TldrSource source in Sources ?? [])
        {
            text.AppendLine($"- {source.Title} | {source.Url}");
        }
        return text.ToString();
    }
}

/// <summary>
/// The TL;DR: for each tab Claude fetches current data from the web and writes a short briefing - latest releases,
/// hot topics, quirks, what a senior must know and optimization tips - with the pages it used. Cached under tldr\
/// next to the exe and written again when it is a day old or on Refresh. Used from the UI thread only.
/// </summary>
public static class Tldr
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private static readonly string[] Titles =
        ["LATEST RELEASES", "HOT TOPICS", "INTERESTING QUIRKS", "WHAT A SENIOR MUST KNOW", "OPTIMIZATION TIPS"];

    private const string SourcesTitle = "SOURCES";

    private static readonly string TldrDir = Path.Combine(AppContext.BaseDirectory, "tldr");

    private static readonly Dictionary<string, Task<TldrContent>> Briefs = [];

    // each TL;DR is a web search of a minute or more, so preparing runs only two at a time
    private static readonly SemaphoreSlim Preparing = new(2);

    /// <summary>
    /// Writes, in the background, every tab's TL;DR that is missing or a day old, so opening one is instant.
    /// Fresh ones are read from disk. Failures are left for when the user opens it.
    /// </summary>
    public static void PrepareAll()
    {
        foreach (Category category in LearnPath.Courses)
        {
            _ = PrepareAsync(category);
        }
    }

    private static async Task PrepareAsync(Category category)
    {
        await Preparing.WaitAsync();
        try
        {
            await GetAsync(category);
        }
        catch (Exception)
        {
            // the TL;DR shows its error when it is opened, and is retried then
        }
        finally
        {
            Preparing.Release();
        }
    }

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

    // a TL;DR saved before sources were added has none, so it is written again
    private static bool IsOld(TldrContent content) => DateTime.Now - content.Written >= MaxAge || content.Sources == null;

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
            Then list under SOURCES every web page you used, one per line as: - <page title> | <full https URL>
            Plain text; no markdown headings and no bold.
            Put every code snippet in a fenced block with its language, e.g. ```java, ```sql, ```javascript, ```bash.
            Short names inside a sentence go in single backticks.

            Reply in exactly this format and nothing else, each title alone on its line followed by its bullets:
            {string.Join("\n", Titles.Select(t => t + ":\n<bullets>"))}
            {SourcesTitle}:
            - <page title> | <full https URL>
            """;
        string output = await QuestionGenerator.RunClaudeAsync(prompt, web: true);

        string[] all = [.. Titles, SourcesTitle];
        int[] starts = all.Select(t => output.IndexOf(t + ":", StringComparison.Ordinal)).ToArray();
        string Part(int i)
        {
            int end = i + 1 < all.Length ? starts[i + 1] : output.Length;
            if (starts[i] < 0 || end < starts[i])
            {
                throw new InvalidOperationException("unexpected reply format:\n" + output);
            }
            return output[(starts[i] + all[i].Length + 1)..end].Trim();
        }

        var sections = new List<TldrSection>();
        for (int i = 0; i < Titles.Length; i++)
        {
            sections.Add(new TldrSection(Titles[i][0] + Titles[i][1..].ToLowerInvariant(), Part(i)));
        }
        var sources = new List<TldrSource>();
        foreach (string line in Part(Titles.Length).Split('\n'))
        {
            string item = line.Trim().TrimStart('-').Trim();
            int bar = item.LastIndexOf('|');
            if (bar < 0) continue;
            string url = item[(bar + 1)..].Trim();
            // only web links: they are opened in the browser
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                sources.Add(new TldrSource(item[..bar].Trim(), uri.AbsoluteUri));
            }
        }

        var content = new TldrContent(DateTime.Now, sections, sources);
        Directory.CreateDirectory(TldrDir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(content));
        return content;
    }
}
