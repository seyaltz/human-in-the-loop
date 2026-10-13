using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DevBreak;

/// <summary>Something that earned XP: a question answered or seen, or a lesson done.</summary>
public sealed record GameEvent(DateTime At, string Kind, string Tab, string Topic, int? Score, int Xp, int? Lesson = null);

public sealed record EarnedBadge(string Id, DateTime At);

public sealed record Rank(string Name, int From, string Color);

public sealed record Badge(string Id, string Icon, string Name, string Description);

/// <summary>
/// The game layer: XP and ranks, the daily streak and goal, badges and per-topic stats. Everything is kept locally in
/// game.json next to the exe. Used from the UI thread only.
/// </summary>
public static class Game
{
    public const string QuestionKind = "question";
    public const string LessonKind = "lesson";

    public const int GoalQuestions = 3;
    public const int GoalLessons = 1;
    public const int GoalBonusXp = 100;
    public const int SeenXp = 5;
    public const int LessonXp = 50;

    /// <summary>XP for a checked answer: 10 for trying plus 10 per point.</summary>
    public static int AnswerXp(int score) => 10 + score * 10;

    public static readonly IReadOnlyList<Rank> Ranks =
    [
        new("Junior", 0, "#607D8B"),
        new("Mid", 1000, "#2E7D32"),
        new("Senior", 3000, "#1565C0"),
        new("Staff", 7000, "#6A1B9A"),
        new("Principal", 15000, "#C62828"),
    ];

    public static readonly IReadOnlyList<Badge> Badges =
    [
        new("first-answer", "✍", "First answer", "Check your first answer"),
        new("perfect", "⭐", "Perfect 10", "Score 10/10"),
        new("hat-trick", "\U0001F3A9", "Hat-trick", "Three 10/10 in a row"),
        new("century", "\U0001F4AF", "Century", "100 questions"),
        new("polyglot", "\U0001F30D", "Polyglot", "Answer a question in every topic"),
        new("first-lesson", "\U0001F4D6", "First lesson", "Finish a lesson"),
        new("scholar", "\U0001F393", "Scholar", "Finish 25 lessons"),
        new("graduate", "\U0001F3C6", "Graduate", "Finish a whole course"),
        new("streak-5", "\U0001F525", "On fire", "5-day streak"),
        new("streak-14", "⚡", "Unstoppable", "14-day streak"),
        new("goal-7", "\U0001F3AF", "Goal getter", "Meet the daily goal on 7 days"),
        new("night-owl", "\U0001F989", "Night owl", "A question after 22:00"),
        new("early-bird", "\U0001F426", "Early bird", "A question before 08:00"),
    ];

    private sealed class Data
    {
        public List<GameEvent> Events { get; set; } = [];
        public List<EarnedBadge> Badges { get; set; } = [];
        public List<DateTime> GoalDays { get; set; } = [];
    }

    private static readonly string DataPath = Path.Combine(AppContext.BaseDirectory, "game.json");
    private static Data? _data;

    /// <summary>Raised after anything that earns XP, with the lines to celebrate ("+70 XP", "Badge: Perfect 10", ...).</summary>
    public static event Action<IReadOnlyList<string>>? Awarded;

    private static Data Store => _data ??= File.Exists(DataPath)
        ? JsonSerializer.Deserialize<Data>(File.ReadAllText(DataPath)) ?? new Data()
        : new Data();

    public static IReadOnlyList<GameEvent> Events => Store.Events;

    public static int Xp => Store.Events.Sum(e => e.Xp) + Store.GoalDays.Count * GoalBonusXp;

    public static Rank RankOf(int xp) => Ranks.Last(r => xp >= r.From);

    public static Rank? NextRank(int xp) => Ranks.FirstOrDefault(r => r.From > xp);

    public static bool HasBadge(string id) => Store.Badges.Any(b => b.Id == id);

    public static int GoalsMet => Store.GoalDays.Count;

    /// <summary>XP earned on a day, the daily-goal bonus included.</summary>
    public static int XpOn(DateTime day) =>
        Store.Events.Where(e => e.At.Date == day).Sum(e => e.Xp) + (Store.GoalDays.Contains(day) ? GoalBonusXp : 0);

    public static int QuestionsOn(DateTime day) => Store.Events.Count(e => e.Kind == QuestionKind && e.At.Date == day);

    public static int LessonsOn(DateTime day) => Store.Events.Count(e => e.Kind == LessonKind && e.At.Date == day);

    public static void QuestionAnswered(Category tab, string topic, int score) =>
        Record(new GameEvent(DateTime.Now, QuestionKind, tab.Name, topic, score, AnswerXp(score)));

    public static void QuestionSeen(Category tab, string topic) =>
        Record(new GameEvent(DateTime.Now, QuestionKind, tab.Name, topic, null, SeenXp));

    public static void LessonDone(Category course, int lesson, string title, bool courseFinished) =>
        Record(new GameEvent(DateTime.Now, LessonKind, course.Name, title, null, LessonXp, lesson), courseFinished);

    /// <summary>Mark as done was undone: its XP goes again. Badges stay.</summary>
    public static void LessonUndone(Category course, int lesson)
    {
        Store.Events.RemoveAll(e => e.Kind == LessonKind && e.Tab == course.Name && e.Lesson == lesson);
        Save();
        Awarded?.Invoke([]);
    }

    /// <summary>Days in a row with something done, up to today (or up to yesterday while today is still empty).</summary>
    public static int Streak()
    {
        HashSet<DateTime> days = Store.Events.Select(e => e.At.Date).ToHashSet();
        DateTime day = days.Contains(DateTime.Today) ? DateTime.Today : DateTime.Today.AddDays(-1);
        int streak = 0;
        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    public static int BestStreak()
    {
        List<DateTime> days = Store.Events.Select(e => e.At.Date).Distinct().Order().ToList();
        int best = 0, run = 0;
        for (int i = 0; i < days.Count; i++)
        {
            run = i > 0 && days[i] == days[i - 1].AddDays(1) ? run + 1 : 1;
            best = Math.Max(best, run);
        }
        return best;
    }

    /// <summary>How much more often a topic is asked: 1 for a new topic, up to 3 for one answered badly.</summary>
    public static double TopicWeight(string topic)
    {
        int[] scores = ScoresFor(e => e.Topic == topic);
        return scores.Length == 0 ? 1 : 1 + (10 - scores.Average()) / 5;
    }

    public static int[] ScoresFor(Func<GameEvent, bool> which) =>
        Store.Events.Where(e => e.Kind == QuestionKind && e.Score != null && which(e)).Select(e => e.Score!.Value).ToArray();

    private static void Record(GameEvent happened, bool courseFinished = false)
    {
        int xpBefore = Xp;
        Store.Events.Add(happened);
        var lines = new List<string> { $"+{happened.Xp} XP" };

        if (QuestionsOn(DateTime.Today) >= GoalQuestions && LessonsOn(DateTime.Today) >= GoalLessons
            && !Store.GoalDays.Contains(DateTime.Today))
        {
            Store.GoalDays.Add(DateTime.Today);
            lines.Add($"Daily goal complete! +{GoalBonusXp} XP");
        }

        foreach (Badge badge in Badges.Where(b => !HasBadge(b.Id) && Earned(b.Id, happened, courseFinished)))
        {
            Store.Badges.Add(new EarnedBadge(badge.Id, DateTime.Now));
            lines.Add($"{badge.Icon} Badge unlocked: {badge.Name}");
        }

        if (RankOf(Xp) != RankOf(xpBefore))
        {
            lines.Add($"Rank up: {RankOf(Xp).Name}!");
        }
        Save();
        Awarded?.Invoke(lines);
    }

    private static bool Earned(string id, GameEvent happened, bool courseFinished)
    {
        List<GameEvent> questions = Store.Events.Where(e => e.Kind == QuestionKind).ToList();
        List<int> scored = questions.Where(e => e.Score != null).Select(e => e.Score!.Value).ToList();
        bool isQuestion = happened.Kind == QuestionKind;
        return id switch
        {
            "first-answer" => scored.Count >= 1,
            "perfect" => scored.Contains(10),
            "hat-trick" => scored.Count >= 3 && scored.TakeLast(3).All(s => s == 10),
            "century" => questions.Count >= 100,
            "polyglot" => LearnPath.Courses.All(c => questions.Any(e => e.Score != null && e.Tab == c.Name)),
            "first-lesson" => Store.Events.Any(e => e.Kind == LessonKind),
            "scholar" => Store.Events.Count(e => e.Kind == LessonKind) >= 25,
            "graduate" => courseFinished,
            "streak-5" => Streak() >= 5,
            "streak-14" => Streak() >= 14,
            "goal-7" => Store.GoalDays.Count >= 7,
            "night-owl" => isQuestion && happened.At.Hour >= 22,
            "early-bird" => isQuestion && happened.At.Hour < 8,
            _ => false,
        };
    }

    private static void Save() => File.WriteAllText(DataPath, JsonSerializer.Serialize(Store));
}
