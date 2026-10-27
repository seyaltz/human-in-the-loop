using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>
/// What Sessions learns: lessons drafted after a turn that needed retries or a correction, kept or dismissed by the
/// user, and how often the grader found each criterion violated per folder. Kept in session-lessons.json next to the
/// exe (the user's own data, git-ignored); a saved lesson itself goes into the user's Claude Code setup, where the CLI
/// loads it natively. Lessons use the Factory's lesson type, and memories its memory writer.
/// </summary>
public static class SessionLessons
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Kinds = ["memory", "skill", "rule"];
    private static Store? _store;

    private static string FileName => Path.Combine(AppContext.BaseDirectory, "session-lessons.json");

    /// <summary>Counts one more violation of <paramref name="criterion"/> in <paramref name="folder"/>; returns the count.</summary>
    public static int CountViolation(string folder, string criterion)
    {
        lock (Gate)
        {
            Store store = Load();
            string key = folder.TrimEnd('\\').ToLowerInvariant() + "|" + criterion.Trim().ToLowerInvariant();
            int count = store.Violations.GetValueOrDefault(key) + 1;
            store.Violations[key] = count;
            Write(store);
            return count;
        }
    }

    /// <summary>
    /// Worth asking what to learn from this turn: it needed retries (2+ failed calls of a tool that later worked), the
    /// user's next message corrects it, or the user stopped it and asked again. Otherwise there is nothing to learn
    /// and no model is asked - the common case.
    /// </summary>
    public static bool ShouldPropose(TurnLog log, string? nextUserMessage, bool stoppedAndReasked)
    {
        if (stoppedAndReasked) return true;
        if (nextUserMessage is { Length: > 0 } next && SessionStats.Correction.IsMatch(next.Length > 200 ? next[..200] : next)) return true;
        List<TurnStep> own = [.. log.Steps.Where(s => !s.Sub && s.End != null)];
        int fixedFailures = own.Select((s, i) => (s, i)).Count(x => x.s.Error && own.Skip(x.i + 1).Any(later => later.Tool == x.s.Tool && !later.Error));
        return fixedFailures >= 2;
    }

    /// <summary>The model's lessons: a JSON array somewhere in the reply; kinds it does not know are dropped; names become slugs.</summary>
    public static List<FactoryLesson> Parse(string raw)
    {
        int start = raw.IndexOf('['), end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return [.. doc.RootElement.EnumerateArray()
                .Select(e => new FactoryLesson
                {
                    Kind = TurnLog.Str(e, "kind").ToLowerInvariant(), Name = Slug(TurnLog.Str(e, "name")),
                    Description = TurnLog.Str(e, "description").Replace('\n', ' ').Trim(), Content = TurnLog.Str(e, "content").Trim(),
                    Status = "pending",
                })
                .Where(l => Kinds.Contains(l.Kind) && l.Name.Length > 0 && l.Content.Length > 0)
                .Take(8)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Slug(string name)
    {
        string slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 50 ? slug[..50].Trim('-') : slug;
    }

    /// <summary>
    /// Asks the user's default model what this turn should teach, as 0-2 lessons; the folder's dismissed lessons go
    /// with it, so they are not proposed again. Nothing comes back on any failure.
    /// </summary>
    public static async Task<List<FactoryLesson>> ProposeAsync(string folder, TurnLog log, string? correction)
    {
        string record = string.Join("\n", log.Steps.Where(s => !s.Sub).Take(60)
            .Select(s => $"- {s.Tool}: {Clip(s.Summary, 140)} -> {(s.End == null ? "no result" : s.Error ? "error: " + Clip(s.Result, 160) : "ok")}"));
        string userSaid = correction != null ? "\nTHE USER THEN SAID\n" + Clip(correction, 1000) + "\n" : "";
        string prompt = $$"""
            A coding agent just finished a turn that went less well than it should have. Decide what it should learn,
            so the next session in this folder does better. Propose 0, 1 or 2 lessons - 0 is the usual answer; only
            propose what will clearly matter again.

            Kinds:
            - memory: a fact about this project (where something is, how it is built, what it needs)
            - skill: a procedure that worked here, step by step, with the exact commands, and the traps on the way
            - rule: an instruction the user gave that should hold from now on, in the user's own words

            THE USER ASKED
            {{Clip(SessionAnswers.WithoutShape(log.Message), 2000)}}

            WHAT IT DID (tool calls in order)
            {{(record.Length > 0 ? record : "(none)")}}

            ITS ANSWER
            {{Clip(log.Answer, 2000)}}
            {{userSaid}}
            THE USER DISMISSED THESE BEFORE - DO NOT PROPOSE THEM AGAIN
            {{Dismissed(folder)}}

            Reply with ONLY a JSON array (empty if nothing is worth keeping):
            [{"kind": "memory" | "skill" | "rule", "name": "short-kebab-name", "description": "one line", "content": "the lesson itself"}]
            """;
        return await AskForLessons(prompt, folder, TimeSpan.FromSeconds(120), 2);
    }

    /// <summary>A prompt that answers with lessons as JSON; empty on any failure.</summary>
    internal static async Task<List<FactoryLesson>> AskForLessons(string prompt, string folder, TimeSpan timeout, int max)
    {
        try
        {
            return [.. Parse(await ClaudeOnce.AskAsync(prompt, null, folder, "", "", timeout)).Take(max)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            SessionPane.Log("lessons: could not ask - " + ex.Message);
            return [];
        }
    }

    // the folder's dismissed lessons, for the prompt (the user's reason with each)
    internal static string Dismissed(string folder)
    {
        List<FactoryLesson> dismissed;
        lock (Gate) dismissed = [.. Load().Lessons.Where(l => Same(l.Folder, folder) && l.Lesson.Status == "dismissed").Select(l => l.Lesson).TakeLast(15)];
        return dismissed.Count == 0 ? "(none)"
            : string.Join("\n", dismissed.Select(l => $"- {l.Kind} {l.Name}: {l.Description}" + (l.Feedback.Length > 0 ? $" (dismissed: {l.Feedback})" : "")));
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes a kept lesson where the CLI reads it natively: a memory in the folder's project memory
    /// (<paramref name="memoryDir"/>, with its line in MEMORY.md, as the Factory writes them), a skill as
    /// <c>~/.claude/skills/name/SKILL.md</c>, a rule at the end of <c>~/.claude/CLAUDE.md</c>. Records how to take it
    /// back out; returns the file.
    /// </summary>
    public static string Save(FactoryLesson lesson, string? memoryDir, string home)
    {
        string file;
        switch (lesson.Kind)
        {
            case "memory":
                if (memoryDir == null) throw new InvalidOperationException("the CLI did not report this folder's memory folder");
                file = Factory.SaveMemory(memoryDir, lesson);
                lesson.UndoData = Path.Combine(memoryDir, "MEMORY.md");
                break;
            case "skill":
                string skills = Path.Combine(home, ".claude", "skills");
                string dir = Path.Combine(skills, lesson.Name);
                for (int n = 2; Directory.Exists(dir); n++) dir = Path.Combine(skills, $"{lesson.Name}-{n}");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, "SKILL.md");
                File.WriteAllText(file, $"---\nname: {Path.GetFileName(dir)}\ndescription: {lesson.Description.Replace('\n', ' ')}\n---\n\n{lesson.Content.Trim()}\n");
                lesson.UndoData = dir;
                break;
            case "rule":
                file = Path.Combine(home, ".claude", "CLAUDE.md");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string existing = File.Exists(file) ? File.ReadAllText(file) : "";
                string appended = (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + "\n" + lesson.Content.Trim() + "\n";
                File.AppendAllText(file, appended);
                lesson.UndoData = appended;
                break;
            default:
                throw new InvalidOperationException("not a lesson kind: " + lesson.Kind);
        }
        lesson.File = file;
        lesson.Status = "saved";
        return file;
    }

    /// <summary>Takes a saved lesson back out: the memory file and its index line, the skill's folder, the rule's text.</summary>
    public static void Undo(FactoryLesson lesson)
    {
        if (lesson.Status != "saved" || lesson.File == null) return;
        switch (lesson.Kind)
        {
            case "memory":
                if (File.Exists(lesson.File)) File.Delete(lesson.File);
                string name = Path.GetFileName(lesson.File);
                if (File.Exists(lesson.UndoData))
                {
                    File.WriteAllLines(lesson.UndoData, File.ReadAllLines(lesson.UndoData).Where(l => !l.Contains($"]({name})", StringComparison.Ordinal)));
                }
                break;
            case "skill":
                if (Directory.Exists(lesson.UndoData) && File.Exists(Path.Combine(lesson.UndoData, "SKILL.md"))) Directory.Delete(lesson.UndoData, recursive: true);
                break;
            case "rule":
                if (File.Exists(lesson.File))
                {
                    string text = File.ReadAllText(lesson.File);
                    int at = text.LastIndexOf(lesson.UndoData, StringComparison.Ordinal);
                    if (at >= 0) File.WriteAllText(lesson.File, text.Remove(at, lesson.UndoData.Length));
                }
                break;
        }
        lesson.Status = "undone";
    }

    /// <summary>Keeps a lesson's record (proposed, saved, dismissed, undone) for Insights and for not proposing it again.</summary>
    public static void Record(string folder, string session, FactoryLesson lesson)
    {
        lock (Gate)
        {
            Store store = Load();
            if (store.Lessons.All(l => !ReferenceEquals(l.Lesson, lesson)))
            {
                store.Lessons.Add(new SavedLesson { Folder = folder, Session = session, Created = DateTime.Now, Lesson = lesson });
            }
            Write(store);
        }
    }

    /// <summary>Every lesson Sessions proposed, newest first.</summary>
    public static List<SavedLesson> All()
    {
        lock (Gate) return [.. Load().Lessons.OrderByDescending(l => l.Created)];
    }

    private static Store Load()
    {
        if (_store != null) return _store;
        try
        {
            _store = File.Exists(FileName) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(FileName)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _store = null;
        }
        return _store ??= new Store();
    }

    private static void Write(Store store)
    {
        try
        {
            File.WriteAllText(FileName, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory for this run
        }
    }

    public sealed class SavedLesson
    {
        public string Folder { get; set; } = "";
        public string Session { get; set; } = "";
        public DateTime Created { get; set; }
        public FactoryLesson Lesson { get; set; } = new();
    }

    public sealed class Store
    {
        public Dictionary<string, int> Violations { get; set; } = [];
        public List<SavedLesson> Lessons { get; set; } = [];
    }
}
