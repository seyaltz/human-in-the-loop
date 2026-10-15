using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DevBreak;

public sealed record Level(string Name, string Style, IReadOnlyList<string> Topics)
{
    public static readonly TimeSpan MediumFrom = new(12, 0, 0);
    public static readonly TimeSpan LightFrom = new(15, 0, 0);

    public static readonly Level Heavy = new("Heavy",
        "an architecture / system-design decision with real trade-offs (2-4 options to choose between)",
        ["distributed systems", "caching strategy", "database design and scaling", "consistency and transactions",
         "messaging and event-driven design", "API design", "microservices vs monolith", "security architecture",
         "observability and reliability", "cloud cost and scaling", "data pipelines"]);

    public static readonly Level Medium = new("Medium",
        "a focused technical question that needs some reasoning",
        ["algorithms and complexity", "data structures", "concurrency", "SQL and indexes", "JVM internals",
         "networking and HTTP", "operating systems", "Spring and JPA", "testing strategy", "design patterns"]);

    public static readonly Level Light = new("Light",
        "a quick question answerable in under a minute (a short snippet, a definition, a \"what does this print\")",
        ["Java language trivia", "git", "HTTP status codes", "Linux / shell commands", "regex", "JavaScript quirks",
         "SQL basics", "CS history", "computer architecture basics", "encoding and Unicode"]);

    public static Level At(TimeSpan time) =>
        time < MediumFrom ? Heavy : time < LightFrom ? Medium : Light;
}

/// <summary>A topic tab. "Mix" has no topics of its own: it uses the time-of-day level's list.</summary>
public sealed record Category(string Name, IReadOnlyList<string> Topics)
{
    public static readonly Category Mix = new("Mix", []);

    /// <summary>Mix, then the user's own topics (see <see cref="DevBreak.Topics"/>).</summary>
    public static IReadOnlyList<Category> All => DevBreak.Topics.All;

    public IReadOnlyList<string> TopicsFor(Level level) => Topics.Count > 0 ? Topics : level.Topics;
}

public sealed record Question(string Text, string Theory, string Answer);

public sealed record Score(int Points, string Feedback);

public static class QuestionGenerator
{
    private static readonly string[] ClaudeArgs =
        ["-p", "--tools", "", "--strict-mcp-config", "--setting-sources", "", "--no-session-persistence"];

    public static async Task<Question> GenerateAsync(Level level, string topic)
    {
        string knowledge = await Knowledge.GetAsync();
        string aboutThem = knowledge.Length == 0 ? "" : $"""
            About this developer:
            {knowledge}

            Where it fits naturally, set the question in their stack and domain, but keep it a general lesson that is worth knowing.

            """;
        string prompt = $"""
            You are quizzing a developer during a short break.
            {aboutThem}
            Write ONE {level.Name.ToLowerInvariant()} question about: {topic}.
            Style: {level.Style}.
            Keep the question under 120 words. Use plain text: no markdown headings, no bold, no lists markup.
            Put every code snippet in a fenced block with its language, e.g. ```java, ```sql, ```javascript, ```bash.
            Short names inside a sentence go in single backticks, e.g. `HashMap`.
            Then give the background theory: the concepts someone needs to know to work it out, as a hint
            (under 120 words). It must not give away the answer.
            Then give the answer: the key insight first, then a short explanation (under 150 words).
            For a design question there may be no single right answer: give the best choice and its trade-offs.

            Reply in exactly this format and nothing else:
            QUESTION:
            <the question>
            THEORY:
            <the background theory>
            ANSWER:
            <the answer>
            """;

        string output = await RunClaudeAsync(prompt);
        int q = output.IndexOf("QUESTION:", StringComparison.Ordinal);
        int t = output.IndexOf("THEORY:", StringComparison.Ordinal);
        int a = output.IndexOf("ANSWER:", StringComparison.Ordinal);
        if (q < 0 || t < q || a < t)
        {
            throw new InvalidOperationException("unexpected reply format:\n" + output);
        }
        return new Question(output[(q + 9)..t].Trim(), output[(t + 7)..a].Trim(), output[(a + 7)..].Trim());
    }

    public static async Task<Score> ScoreAsync(Question question, string mine)
    {
        string prompt = $"""
            You are grading a developer's answer to a quiz question during a short break. Be fair and encouraging,
            but honest: give credit for the key insight even if worded loosely; take points for wrong or missing key ideas.

            QUESTION:
            {question.Text}

            REFERENCE ANSWER:
            {question.Answer}

            THE DEVELOPER'S ANSWER:
            {mine}

            Score it from 0 to 10. Then say in under 60 words what they got right and what was missing or wrong.
            Use plain text; single backticks for code names.

            Reply in exactly this format and nothing else:
            SCORE: <0-10>
            FEEDBACK:
            <the feedback>
            """;

        string output = await RunClaudeAsync(prompt);
        Match score = Regex.Match(output, @"SCORE:\s*(\d+)");
        int f = output.IndexOf("FEEDBACK:", StringComparison.Ordinal);
        if (!score.Success || f < 0)
        {
            throw new InvalidOperationException("unexpected reply format:\n" + output);
        }
        return new Score(Math.Clamp(int.Parse(score.Groups[1].Value), 0, 10), output[(f + 9)..].Trim());
    }

    /// <summary>One chat turn about a question. The CLI keeps no session, so every turn sends the whole conversation.</summary>
    public static Task<string> ChatAsync(Question question, string mine, IReadOnlyList<(bool IsMine, string Text)> history)
    {
        var conversation = new StringBuilder();
        foreach ((bool isMine, string text) in history)
        {
            conversation.Append(isMine ? "DEVELOPER: " : "TUTOR: ").AppendLine(text).AppendLine();
        }

        string prompt = $"""
            You are a friendly senior engineer tutoring a developer about a quiz question they just did during a break.
            Answer their latest message directly and concisely (usually under 150 words). Go deeper, give examples,
            or correct them when they are wrong. Plain text, no markdown headings or bold.
            Put code in fenced blocks with the language (```java, ```sql, ...); short names in single backticks.

            QUESTION:
            {question.Text}

            REFERENCE ANSWER:
            {question.Answer}

            THE DEVELOPER'S OWN ANSWER:
            {(mine.Trim().Length > 0 ? mine : "(none)")}

            CONVERSATION SO FAR (the last DEVELOPER message is the one to answer):
            {conversation}

            Reply with your answer only, without a "TUTOR:" prefix.
            """;
        return RunClaudeAsync(prompt);
    }

    public static async Task<string> RunClaudeAsync(string prompt)
    {
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in ClaudeArgs)
        {
            start.ArgumentList.Add(arg);
        }

        // starting claude.exe (a 250+ MB file) takes about half a second, so it stays off the UI thread
        using Process process = await Task.Run(() => Process.Start(start)) ?? throw new InvalidOperationException("could not start claude");
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        }
        catch (TimeoutException)
        {
            // disposing the Process does not stop it: without this the claude process tree keeps running
            process.Kill(entireProcessTree: true);
            throw;
        }
        string output = (await stdout).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"claude exited with {process.ExitCode}: {output} {await stderr}".Trim());
        }
        return output;
    }
}
