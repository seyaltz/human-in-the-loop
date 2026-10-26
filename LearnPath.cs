using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>One step of a tab's course.</summary>
public sealed record Lesson(string Title, string Summary);

/// <summary>A lesson's text, with a check question and its answer.</summary>
public sealed record LessonContent(string Text, string Check, string Answer);

/// <summary>
/// The learn path: for each tab Claude writes an ordered course once (basics to what a senior must know), and each
/// lesson when it is first opened. Both are cached under learn\ next to the exe, with the lessons marked done.
/// Used from the UI thread only.
/// </summary>
public static class LearnPath
{
    private const int LessonsPerCourse = 12;

    // preparing in the background runs a few courses at a time
    private const int PrepareParallel = 3;

    private static readonly string LearnDir = Path.Combine(AppContext.BaseDirectory, "learn");
    private static readonly string ProgressPath = Path.Combine(LearnDir, "progress.json");

    private static readonly Dictionary<string, Task<IReadOnlyList<Lesson>>> Syllabi = [];
    private static readonly Dictionary<(string, int), Task<LessonContent>> Lessons = [];
    private static readonly SemaphoreSlim Preparing = new(PrepareParallel);
    private static Dictionary<string, HashSet<int>>? _done;

    /// <summary>Every tab except Mix, which has no topics of its own.</summary>
    public static IEnumerable<Category> Courses => Category.All.Where(c => c != Category.Mix);

    public static Task<IReadOnlyList<Lesson>> SyllabusAsync(Category category)
    {
        if (!Syllabi.TryGetValue(category.Name, out Task<IReadOnlyList<Lesson>>? task) || task.IsFaulted)
        {
            task = LoadOrWriteSyllabusAsync(category);
            Syllabi[category.Name] = task;
        }
        return task;
    }

    public static Task<LessonContent> LessonAsync(Category category, int index, IReadOnlyList<Lesson> syllabus)
    {
        if (!Lessons.TryGetValue((category.Name, index), out Task<LessonContent>? task) || task.IsFaulted)
        {
            task = LoadOrWriteLessonAsync(category, index, syllabus);
            Lessons[(category.Name, index)] = task;
        }
        return task;
    }

    /// <summary>
    /// Writes, in the background, every course's lesson list and its next lesson not done yet, so opening a course or
    /// carrying on is instant. Already written ones are read from disk. Failures are left for when the user opens it.
    /// </summary>
    public static void PrepareAll()
    {
        foreach (Category category in Courses)
        {
            _ = PrepareAsync(category);
        }
    }

    /// <summary>Writes the course's lesson list (if needed) and its next lesson not done yet.</summary>
    public static async Task PrepareAsync(Category category)
    {
        await Preparing.WaitAsync();
        try
        {
            IReadOnlyList<Lesson> syllabus = await SyllabusAsync(category);
            int next = Enumerable.Range(0, syllabus.Count).FirstOrDefault(i => !IsDone(category, i), -1);
            if (next >= 0) await LessonAsync(category, next, syllabus);
        }
        catch (Exception)
        {
            // the course or lesson shows its error when it is opened, and is retried then
        }
        finally
        {
            Preparing.Release();
        }
    }

    public static bool IsDone(Category category, int index) =>
        Done().TryGetValue(category.Name, out HashSet<int>? done) && done.Contains(index);

    public static void SetDone(Category category, int index, bool isDone)
    {
        Dictionary<string, HashSet<int>> all = Done();
        if (!all.TryGetValue(category.Name, out HashSet<int>? done))
        {
            done = [];
            all[category.Name] = done;
        }
        if (isDone) done.Add(index);
        else done.Remove(index);
        Directory.CreateDirectory(LearnDir);
        File.WriteAllText(ProgressPath, JsonSerializer.Serialize(all));
    }

    private static Dictionary<string, HashSet<int>> Done() =>
        _done ??= File.Exists(ProgressPath)
            ? JsonSerializer.Deserialize<Dictionary<string, HashSet<int>>>(File.ReadAllText(ProgressPath)) ?? []
            : [];

    private static string CourseDir(Category category) =>
        Path.Combine(LearnDir, string.Concat(category.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-')));

    internal static async Task<string> AboutThemAsync()
    {
        string knowledge = await Knowledge.GetAsync();
        return knowledge.Length == 0 ? "" : $"""
            About the learner:
            {knowledge}

            """;
    }

    private static async Task<IReadOnlyList<Lesson>> LoadOrWriteSyllabusAsync(Category category)
    {
        string path = Path.Combine(CourseDir(category), "syllabus.json");
        if (File.Exists(path))
        {
            return JsonSerializer.Deserialize<List<Lesson>>(await File.ReadAllTextAsync(path))!;
        }

        string prompt = $"""
            You are designing a short course for a developer who wants to master: {category.Name}.
            It covers what every senior software engineer working with {category.Name} should know: the theory behind it
            and the important, practical things that matter in real systems.
            Scope (use as a guide): {string.Join(", ", category.Topics)}.
            {await AboutThemAsync()}
            Write exactly {LessonsPerCourse} lessons, ordered from the foundations to senior-level topics.
            Each lesson is one focused subject, e.g. for a browser course "cookies vs localStorage vs sessionStorage".

            Reply in exactly this format and nothing else, one lesson per line:
            1. <title> | <one-sentence summary>
            2. <title> | <one-sentence summary>
            """;
        string output = await QuestionGenerator.RunClaudeAsync(prompt);
        var lessons = new List<Lesson>();
        foreach (string line in output.Split('\n'))
        {
            int dot = line.IndexOf(". ", StringComparison.Ordinal);
            int bar = line.IndexOf('|');
            if (dot < 1 || bar < dot || !line[..dot].Trim().All(char.IsDigit)) continue;
            lessons.Add(new Lesson(line[(dot + 2)..bar].Trim(), line[(bar + 1)..].Trim()));
        }
        if (lessons.Count == 0)
        {
            throw new InvalidOperationException("unexpected reply format:\n" + output);
        }
        Directory.CreateDirectory(CourseDir(category));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(lessons));
        return lessons;
    }

    private static async Task<LessonContent> LoadOrWriteLessonAsync(Category category, int index, IReadOnlyList<Lesson> syllabus)
    {
        string path = Path.Combine(CourseDir(category), $"lesson-{index + 1:00}.json");
        if (File.Exists(path))
        {
            return JsonSerializer.Deserialize<LessonContent>(await File.ReadAllTextAsync(path))!;
        }

        Lesson lesson = syllabus[index];
        string course = string.Join("\n", syllabus.Select((l, i) => $"{i + 1}. {l.Title}"));
        string prompt = $"""
            You are teaching lesson {index + 1} of a {category.Name} course for developers growing into senior engineers.
            {await AboutThemAsync()}
            The course:
            {course}

            This lesson: {lesson.Title} - {lesson.Summary}

            Teach it in under 600 words:
            - the theory: what it is, how it works underneath, and why it is designed that way;
            - real examples where they apply (real code, commands, configuration or a realistic production scenario);
            - what a senior engineer must know: trade-offs, common mistakes and pitfalls.
            Plain text with short paragraphs; you may use simple "- " bullets. No markdown headings and no bold.
            Put every code snippet in a fenced block with its language, e.g. ```java, ```sql, ```javascript, ```bash.
            Short names inside a sentence go in single backticks.
            {QuestionGenerator.Diagrams}
            Then write one short question that checks the key idea of the lesson, and its answer (under 80 words).

            Reply in exactly this format and nothing else:
            LESSON:
            <the lesson>
            CHECK:
            <the question>
            ANSWER:
            <the answer>
            """;
        string output = await QuestionGenerator.RunClaudeAsync(prompt);
        int l = output.IndexOf("LESSON:", StringComparison.Ordinal);
        int c = output.IndexOf("CHECK:", StringComparison.Ordinal);
        int a = output.IndexOf("ANSWER:", StringComparison.Ordinal);
        if (l < 0 || c < l || a < c)
        {
            throw new InvalidOperationException("unexpected reply format:\n" + output);
        }
        var content = new LessonContent(output[(l + 7)..c].Trim(), output[(c + 6)..a].Trim(), output[(a + 7)..].Trim());
        Directory.CreateDirectory(CourseDir(category));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(content));
        return content;
    }
}
