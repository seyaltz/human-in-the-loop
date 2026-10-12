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
        ["algorithms and complexity", "data structures", "concurrency", "SQL and indexes", "runtime internals (memory, GC)",
         "networking and HTTP", "operating systems", "web frameworks and ORMs", "testing strategy", "design patterns"]);

    public static readonly Level Light = new("Light",
        "a quick question answerable in under a minute (a short snippet, a definition, a \"what does this print\")",
        ["programming language trivia", "git", "HTTP status codes", "Linux / shell commands", "regex", "JavaScript quirks",
         "SQL basics", "CS history", "computer architecture basics", "encoding and Unicode"]);

    public static Level At(TimeSpan time) =>
        time < MediumFrom ? Heavy : time < LightFrom ? Medium : Light;
}

/// <summary>
/// Tech tabs quiz the developer. Business and Product tabs bring an idea about their company (company.txt) to discuss:
/// Business a new innovation for the company, Product a way to extend the current product's features.
/// </summary>
public enum CategoryKind { Tech, Business, Product }

/// <summary>A topic tab. "Mix" has no topics of its own: it uses the time-of-day level's list.</summary>
public sealed record Category(string Name, IReadOnlyList<string> Topics, CategoryKind Kind = CategoryKind.Tech)
{
    public static readonly Category Mix = new("Mix", []);

    public static readonly IReadOnlyList<Category> All =
    [
        Mix,
        new("Architecture", ["distributed systems", "caching strategy", "messaging and event-driven design",
            "consistency and transactions", "API design", "microservices vs monolith", "resilience (retries, timeouts, circuit breakers)",
            "scaling a read-heavy system"]),
        new("Cloud", ["serverless and containers (scaling, cold starts)", "managed databases and connection limits",
            "identity, roles and service accounts", "load balancers, WAF and edge security", "queues and pub/sub",
            "cloud logging, metrics and tracing", "cloud cost", "infrastructure as code"]),
        new("Backend", ["memory management and GC", "concurrency", "framework internals (dependency injection, proxies)",
            "transactions in application code", "ORMs and data access", "language features", "async and threads"]),
        new("Databases", ["indexes and query plans", "relational vs NoSQL", "SQL", "locking and isolation levels",
            "schema design", "caches and key-value stores"]),
        new("Algorithms", ["algorithms and complexity", "data structures", "graphs and shortest paths",
            "sorting and searching", "dynamic programming", "hashing"]),
        new("Frontend", ["rendering strategies (SSR, SSG, client-side)", "React state and effects", "TypeScript types",
            "browser and HTTP caching", "web performance", "JavaScript quirks"]),
        new("DevOps", ["Docker", "CI/CD pipelines", "git", "Terraform", "observability (logs, metrics, traces)",
            "deployments and rollbacks"]),
        new("Business", ["AI and automation in travel", "new revenue streams", "personalization and customer experience",
            "distribution and partnerships (OTAs, NDC, B2B)", "pricing and ancillary revenue", "loyalty and retention",
            "operational efficiency and cost", "data and analytics as a product", "new markets and customer segments",
            "disruption and travel irregularities"], CategoryKind.Business),
        new("Product", ["search and results", "booking flow and conversion", "payments and checkout",
            "manage booking (changes, cancellations, refunds)", "ancillaries (seats, bags, insurance)",
            "packages and dynamic packaging", "notifications and customer communication", "agent and back-office tools",
            "mobile experience", "AI assistant and conversational booking", "reporting and insights",
            "accessibility and localization"], CategoryKind.Product),
    ];

    public IReadOnlyList<string> TopicsFor(Level level) => Topics.Count > 0 ? Topics : level.Topics;
}

/// <summary>
/// <paramref name="Poc"/> is a small runnable proof of concept, or empty when one would not help.
/// <paramref name="Check"/> is how it did when it was run here before the question was shown; null if it was not run.
/// </summary>
public sealed record Question(string Text, string Theory, string Answer, string Poc, PocCheck? Check = null,
    CategoryKind Kind = CategoryKind.Tech)
{
    /// <summary>A business or product idea to discuss, rather than a question with a right answer.</summary>
    public bool IsIdea => Kind != CategoryKind.Tech;

    /// <summary>The answer followed by the proof of concept, if there is one.</summary>
    public string AnswerWithPoc => Poc.Length == 0 ? Answer : $"{Answer}\n\nProof of concept:\n{Poc}";
}

/// <summary>The result of running a proof of concept here: whether it ran cleanly, its output, and the attempts it took.</summary>
public sealed record PocCheck(bool Passed, string Output, int Attempts);

public sealed record Score(int Points, string Feedback);

public static class QuestionGenerator
{
    private static readonly string[] ClaudeArgs =
        ["-p", "--tools", "", "--strict-mcp-config", "--setting-sources", "", "--no-session-persistence"];

    // a proof of concept that fails is sent back to Claude with its output this many times
    private const int PocFixes = 2;
    private static readonly TimeSpan PocTimeout = TimeSpan.FromSeconds(60);
    private const int PocOutputLimit = 4000;

    public static async Task<Question> GenerateAsync(Category category, Level level, string topic)
    {
        if (category.Kind != CategoryKind.Tech) return await GenerateIdeaAsync(category.Kind, level, topic);

        string knowledge = await Knowledge.GetAsync();
        string runtimes = await PocRunner.RuntimesAsync();
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
            Then, where it helps, give a proof of concept: a small, self-contained, runnable snippet (under 40 lines)
            that demonstrates the answer, e.g. code that shows the bug and the fix, a SQL script that shows the plan
            or the lock, or a minimal sketch of the chosen design. Fenced block with its language, plus one line on
            how to run it and what to look for. For a system-design topic, simulate the mechanism in one process
            (threads or an in-memory queue standing in for services, a dictionary for a cache or a database).
            {PocRules(runtimes)}
            If a snippet would not add anything (a definition, history, a pure trade-off discussion), write just NONE.

            Reply in exactly this format and nothing else:
            QUESTION:
            <the question>
            THEORY:
            <the background theory>
            ANSWER:
            <the answer>
            POC:
            <the proof of concept, or NONE>
            """;

        (string text, string theory, string answer, string poc) = Parse(await RunClaudeAsync(prompt));
        (poc, PocCheck? check) = await CheckPocAsync(poc, runtimes);
        return new Question(text, theory, answer, poc, check);
    }

    /// <summary>
    /// A business innovation or a product extension to discuss, set in the company described in company.txt.
    /// It reuses the question layout: the idea as the question, context as the hint, Claude's take as the answer.
    /// </summary>
    private static async Task<Question> GenerateIdeaAsync(CategoryKind kind, Level level, string topic)
    {
        string company = await Knowledge.CompanyAsync();
        string aboutCompany = company.Length > 0 ? $"""
            About their company and product:
            {company}
            """ : "Their company builds software; the details are unknown, so keep the idea general.";
        string what = kind == CategoryKind.Business
            ? """
              a business innovation idea for the company: something new it could offer, a new market or revenue stream,
              or a way to use technology (AI, data, automation) to change how it works. Be concrete and a little bold,
              but realistic for a company of its size and customers.
              """
            : """
              an idea to extend one of the product's CURRENT features (named from the description above): what to add
              or change, for which users, and why it would matter. Build on what exists rather than inventing a new product.
              """;
        string depth = level == Level.Heavy ? "Make it a substantial idea with real trade-offs to weigh."
            : level == Level.Medium ? "Make it a focused idea that can be discussed in a few minutes."
            : "Make it a small, quick idea: something that could ship in a sprint or two.";
        string prompt = $"""
            You are a product and business strategist sparring with a developer during a short break.
            {aboutCompany}

            Bring ONE {what}
            Area: {topic}. {depth}
            Use plain text: no markdown headings, no bold, no lists markup. Short names in single backticks.

            First the idea, as an open question to think about: the situation or opportunity, the idea in a sentence or
            two, then ask what they think: would it work, what is the value, what are the risks, how would they build or
            test it (under 130 words).
            Then background as a hint: the market context, what others in the industry do, the customers' pain, or the
            constraints that matter (under 120 words). It must not give your verdict.
            Then your take: is it worth doing and why, the expected value, the main risks, how to test it cheaply
            (an MVP or experiment and the metric to watch), and the first concrete steps (under 180 words).

            Reply in exactly this format and nothing else:
            QUESTION:
            <the idea as a question>
            THEORY:
            <the background>
            ANSWER:
            <your take>
            """;

        (string text, string theory, string answer, _) = Parse(await RunClaudeAsync(prompt));
        return new Question(text, theory, answer, "", Kind: kind);
    }

    /// <summary>Splits a QUESTION / THEORY / ANSWER / optional POC reply; an absent or NONE proof of concept is empty.</summary>
    private static (string Text, string Theory, string Answer, string Poc) Parse(string output)
    {
        int q = output.IndexOf("QUESTION:", StringComparison.Ordinal);
        int t = output.IndexOf("THEORY:", StringComparison.Ordinal);
        int a = output.IndexOf("ANSWER:", StringComparison.Ordinal);
        if (q < 0 || t < q || a < t)
        {
            throw new InvalidOperationException("unexpected reply format:\n" + output);
        }
        int p = output.IndexOf("\nPOC:", a, StringComparison.Ordinal);
        string answer = (p < 0 ? output[(a + 7)..] : output[(a + 7)..p]).Trim();
        string poc = p < 0 ? "" : output[(p + 5)..].Trim();
        if (poc.Equals("NONE", StringComparison.OrdinalIgnoreCase)) poc = "";
        return (output[(q + 9)..t].Trim(), output[(t + 7)..a].Trim(), answer, poc);
    }

    private static string PocRules(string runtimes) => $"""
        The reader runs it with one click on their own machine, and it is run once before they see it, so where the
        point allows, write it for one of these runtimes, using only language features and APIs that exact version has:
        {(runtimes.Length > 0 ? runtimes : "Java, Python, JavaScript (Node), Bash or C#")}.
        One file, the standard library only, no network, no input, no files outside its own folder. It must finish in
        a few seconds, print what to look for, and exit with code 0: catch an exception the demo expects and print it.
        For SQL, a Python script with the built-in sqlite3 works when SQLite shows the point.
        """;

    /// <summary>
    /// Runs the proof of concept here. If it fails or hangs, Claude gets the code and its output to fix it, up to
    /// <see cref="PocFixes"/> times. Returns the last version and how it ran; no check when it cannot run here.
    /// </summary>
    private static async Task<(string Poc, PocCheck? Check)> CheckPocAsync(string poc, string runtimes)
    {
        for (int attempt = 1; ; attempt++)
        {
            // also covers a fix that moved to a language that cannot run here
            var runner = new PocRunner(poc);
            if (poc.Length == 0 || runner.NotRunnable != null) return (poc, null);
            int exit;
            string output;
            try
            {
                (exit, output) = await runner.CaptureAsync(PocTimeout);
            }
            catch (Exception ex)
            {
                return (poc, new PocCheck(false, "Could not run it: " + ex.Message, attempt));
            }
            if (output.Length > PocOutputLimit) output = "...\n" + output[^PocOutputLimit..];
            if (exit == 0) return (poc, new PocCheck(true, output, attempt));
            if (attempt > PocFixes) return (poc, new PocCheck(false, output, attempt));

            string problem = exit == -1 ? $"It did not finish within {PocTimeout.TotalSeconds:0} seconds." : $"It exited with code {exit}.";
            string fixPrompt = $"""
                This proof of concept for a quiz answer failed when it was run. {problem}

                PROOF OF CONCEPT:
                {poc}

                OUTPUT:
                {output}

                Fix it so it runs cleanly and still demonstrates the same point.
                {PocRules(runtimes)}
                Reply with the fixed proof of concept only, in the same form: one fenced code block with its language,
                then one line on how to run it and what to look for. No other text.
                """;
            try
            {
                poc = await RunClaudeAsync(fixPrompt);
            }
            catch (Exception)
            {
                return (poc, new PocCheck(false, output, attempt));
            }
        }
    }

    public static async Task<Score> ScoreAsync(Question question, string mine)
    {
        string grading = question.IsIdea
            ? """
              You are reviewing a developer's thoughts on a business or product idea during a short break. There is no
              single right answer: the reference is one strategist's take, not the key. Judge the thinking itself:
              did they weigh customer value, feasibility, risks and cost, and say how to test it? Be encouraging and honest.
              """
            : """
              You are grading a developer's answer to a quiz question during a short break. Be fair and encouraging,
              but honest: give credit for the key insight even if worded loosely; take points for wrong or missing key ideas.
              """;
        string prompt = $"""
            {grading}

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
    public static async Task<string> ChatAsync(Question question, string mine, IReadOnlyList<(bool IsMine, string Text)> history)
    {
        var conversation = new StringBuilder();
        foreach ((bool isMine, string text) in history)
        {
            conversation.Append(isMine ? "DEVELOPER: " : "TUTOR: ").AppendLine(text).AppendLine();
        }

        string company = question.IsIdea ? await Knowledge.CompanyAsync() : "";
        string role = question.IsIdea
            ? $"""
              You are a product and business strategist brainstorming with a developer about an idea for their company.
              Answer their latest message directly and concisely (usually under 150 words). Build on good thoughts,
              push back on weak ones, and get concrete: customers, value, cost, risks, how to test it, first steps.
              {(company.Length > 0 ? "About their company and product:\n" + company : "")}
              """
            : """
              You are a friendly senior engineer tutoring a developer about a quiz question they just did during a break.
              Answer their latest message directly and concisely (usually under 150 words). Go deeper, give examples,
              or correct them when they are wrong.
              """;
        string prompt = $"""
            {role}
            Plain text, no markdown headings or bold.
            Put code in fenced blocks with the language (```java, ```sql, ...); short names in single backticks.

            QUESTION:
            {question.Text}

            REFERENCE ANSWER:
            {question.AnswerWithPoc}

            THE DEVELOPER'S OWN ANSWER:
            {(mine.Trim().Length > 0 ? mine : "(none)")}

            CONVERSATION SO FAR (the last DEVELOPER message is the one to answer):
            {conversation}

            Reply with your answer only, without a "TUTOR:" prefix.
            """;
        return await RunClaudeAsync(prompt);
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

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start claude");
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        string output = (await stdout).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"claude exited with {process.ExitCode}: {output} {await stderr}".Trim());
        }
        return output;
    }
}
