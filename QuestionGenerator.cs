using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

/// <summary>Which Claude a discussion runs: Clean (only the profile) or Mine (the user's own settings, memory, skills, hooks).</summary>
public enum ClaudeSetup { Clean, Mine }

public static class QuestionGenerator
{
    private static readonly string[] ClaudeArgs =
        ["-p", "--tools", "", "--strict-mcp-config", "--setting-sources", "", "--no-session-persistence"];

    // the TL;DR needs current data: the same, plus web search and fetch, allowed so -p does not stop to ask
    private static readonly string[] ClaudeWebArgs =
        ["-p", "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch", "--strict-mcp-config",
         "--setting-sources", "", "--no-session-persistence"];

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
    private static string Conversation(IReadOnlyList<(bool IsMine, string Text)> history)
    {
        var conversation = new StringBuilder();
        foreach ((bool isMine, string text) in history)
        {
            conversation.Append(isMine ? "DEVELOPER: " : "TUTOR: ").AppendLine(text).AppendLine();
        }
        return conversation.ToString();
    }

    // what every discussion's system prompt says about how to answer
    private const string HowToAnswer = """
        Answer their latest message directly and concisely (usually under 150 words). Go deeper, give examples,
        or correct them when they are wrong. When they ask what it means for their own work, products or platform,
        answer in terms of what is known about them below.
        Format lightly, it is rendered: short paragraphs, "- " or "1. " lists, **bold** for the key point; no headings
        and no tables. Put code in fenced blocks with the language (```java, ```sql, ...); short names in single backticks.
        When the developer attached images, read each one with your Read tool before you answer.
        """;

    // the user turn of every discussion: the talk so far; the item itself is in the system prompt
    private static string Turn(IReadOnlyList<(bool IsMine, string Text)> history) => $"""
        CONVERSATION SO FAR (the last DEVELOPER message is the one to answer):
        {Conversation(history)}

        Reply with your answer only, without a "TUTOR:" prefix.
        """;

    /// <summary>A follow-up about a TL;DR: the TL;DR is the system context. It may use the web, so answers stay current.</summary>
    public static async Task<string> ChatAboutTldrAsync(string topic, TldrContent tldr, IReadOnlyList<(bool IsMine, string Text)> history)
    {
        string system = $"""
            You are a friendly senior engineer discussing the latest news on {topic} with a developer during a break.
            When it is about something recent, search the web and fetch the official source first, and name the page
            you used with its full https URL.
            {HowToAnswer}
            {await LearnPath.AboutThemAsync()}
            {OnTheirMachine()}
            THE TL;DR YOU ARE DISCUSSING (written {tldr.Written:yyyy-MM-dd}):
            {tldr.ToText()}
            """;
        return await RunDiscussionAsync(system, Turn(history));
    }

    /// <summary>A follow-up about a lesson of the learn path: the lesson and its check question and answer are the system context.</summary>
    public static async Task<string> ChatAboutLessonAsync(string course, Lesson lesson, LessonContent content, IReadOnlyList<(bool IsMine, string Text)> history)
    {
        string system = $"""
            You are a friendly senior engineer tutoring a developer through a lesson of their {course} course during a break.
            {HowToAnswer}
            {await LearnPath.AboutThemAsync()}
            {OnTheirMachine()}
            THE LESSON: {lesson.Title} - {lesson.Summary}
            {content.Text}

            ITS CHECK QUESTION:
            {content.Check}

            ITS ANSWER:
            {content.Answer}
            """;
        return await RunDiscussionAsync(system, Turn(history));
    }

    /// <summary>
    /// A follow-up about a quiz question: the question, the reference answer, the developer's own answer and its score
    /// (once checked) are the system context. Answer and score are read at each turn, so a later check is known too.
    /// </summary>
    public static async Task<string> ChatAsync(Question question, string mine, Score? score, IReadOnlyList<(bool IsMine, string Text)> history)
    {
        string scored = score == null ? "(not checked yet)" : $"{score.Points}/10 - {score.Feedback}";
        string system = $"""
            You are a friendly senior engineer tutoring a developer about a quiz question they just did during a break.
            {HowToAnswer}
            {await LearnPath.AboutThemAsync()}
            {OnTheirMachine()}
            QUESTION:
            {question.Text}

            REFERENCE ANSWER:
            {question.Answer}

            THE DEVELOPER'S OWN ANSWER:
            {(mine.Trim().Length > 0 ? mine : "(none)")}

            ITS SCORE:
            {scored}
            """;
        return await RunDiscussionAsync(system, Turn(history));
    }

    public static Task<string> RunClaudeAsync(string prompt, bool web = false) =>
        RunAsync(web ? ClaudeWebArgs : ClaudeArgs, prompt, Path.GetTempPath(), TimeSpan.FromMinutes(3));

    /// <summary>Which Claude runs the discussions: clean (only the profile) or the user's own setup, as in their CLI.</summary>
    public static ClaudeSetup DiscussSetup { get; private set; } =
        Enum.TryParse(AppSettings.Get("discussSetup"), out ClaudeSetup setup) ? setup : ClaudeSetup.Clean;

    public static void SetDiscussSetup(ClaudeSetup setup)
    {
        DiscussSetup = setup;
        AppSettings.Set("discussSetup", setup.ToString());
    }

    // the read-only git subcommands a discussion may run, each only as: git -C <one of the user's repos> <subcommand>
    private static readonly string[] ReadOnlyGit = ["log", "status", "diff", "show"];

    // the read-only Gradle reports a discussion may run, each only as: <repo>\gradlew(.bat) -p <repo> <task>
    // (Claude Code asks before any "cd ...; command", and -p cannot ask, so the project folder goes in -p)
    private static readonly string[] ReadOnlyGradle = ["dependencies", "dependencyInsight"];

    /// <summary>
    /// A discussion turn that may look at the user's machine but not change it: it can read and search their Claude
    /// Code project folders, use the web, and run only allow-listed read-only commands. Nothing else is allowed, and
    /// -p cannot ask, so anything else is denied. Clean leaves out the user's settings, hooks and MCP servers; Mine
    /// loads them. A dependency report can be slow, so it gets more time.
    /// </summary>
    private static async Task<string> RunDiscussionAsync(string system, string prompt)
    {
        // own files per turn: two discussions can run at the same time; removed when the turn ends
        string id = Guid.NewGuid().ToString("N");
        // git -C <path> and gradlew -p <path> take any subcommand or task, so each is allowed per repo and per read-only
        // subcommand only. Dozens of repos make the rules too long for a command line, so they go in a settings file.
        IReadOnlyList<string> repos = ClaudeProjects.GitRepos();
        var allow = new List<string> { "Read", "Grep", "Glob", "WebSearch", "WebFetch", "PowerShell(dir:*)" };
        allow.AddRange(repos.SelectMany(repo => ReadOnlyGit.Select(g => $"PowerShell(git -C {repo} {g}:*)")));
        foreach (string repo in repos.Where(r => File.Exists(Path.Combine(r, "gradlew.bat"))))
        {
            foreach (string task in ReadOnlyGradle)
            {
                allow.Add($@"PowerShell({repo}\gradlew.bat -p {repo} {task}:*)");
                allow.Add($@"PowerShell({repo}\gradlew -p {repo} {task}:*)");
            }
        }
        string settings = Path.Combine(Path.GetTempPath(), $"devbreak-discussion-{id}-permissions.json");
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(new
        {
            permissions = new { allow, deny = new[] { "Write", "Edit", "NotebookEdit" } },
        }));
        // the discussion's context (the item, the learner, the machine) is its system prompt; a TL;DR is too long for a
        // command line, so it goes in a file
        string systemFile = Path.Combine(Path.GetTempPath(), $"devbreak-discussion-{id}-system.txt");
        File.WriteAllText(systemFile, system);
        var args = new List<string>
        {
            "-p", "--tools", "Read,Grep,Glob,WebSearch,WebFetch,PowerShell", "--settings", settings,
            "--disallowedTools", "Write,Edit,NotebookEdit", "--append-system-prompt-file", systemFile,
            "--no-session-persistence",
        };
        if (DiscussSetup == ClaudeSetup.Clean) args.AddRange(["--setting-sources", "", "--strict-mcp-config"]);
        foreach (ClaudeProject project in ClaudeProjects.Find()) args.AddRange(["--add-dir", project.Folder]);
        try
        {
            return await RunAsync(args, prompt, Path.GetTempPath(), TimeSpan.FromMinutes(10));
        }
        finally
        {
            File.Delete(settings);
            File.Delete(systemFile);
        }
    }

    /// <summary>Tells a discussion what it may look at on the developer's machine, and how to run the allowed commands.</summary>
    private static string OnTheirMachine()
    {
        IReadOnlyList<ClaudeProject> projects = ClaudeProjects.Find();
        IReadOnlyList<string> repos = ClaudeProjects.GitRepos();
        return $"""
            You run on the developer's own machine and may look at it, read-only: read and search files in their
            Claude Code project folders, search the web, and run only these commands -
            - git, exactly as: git -C <repo path as listed below> {string.Join("|", ReadOnlyGit)} ... (never cd + git, never another subcommand)
            - dir
            - a Gradle dependency report, exactly as: <repo>\gradlew.bat -p <repo> {string.Join("|", ReadOnlyGradle)} ... (never cd first)
            When an answer depends on their actual code, versions or setup, check it yourself instead of guessing or
            asking them to run something, and say in one line what you looked at. You cannot change anything.
            Their Claude Code project folders:
            {string.Join("\n", projects.Select(p => "- " + p.Folder))}
            Their git repos:
            {string.Join("\n", repos.Select(r => "- " + r))}

            """;
    }

    private static async Task<string> RunAsync(IEnumerable<string> args, string prompt, string workingDirectory, TimeSpan timeout)
    {
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
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
            await process.WaitForExitAsync().WaitAsync(timeout);
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
