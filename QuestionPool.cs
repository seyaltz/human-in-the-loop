using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>A question being generated (or already done) for one topic.</summary>
public sealed record PendingQuestion(string Topic, Task<Question> Question);

/// <summary>
/// Keeps <see cref="ReadyPerTab"/> questions generated ahead for each tab and level, so a tab, Next question
/// and the hourly popup usually show a question at once. Taking one starts generating its replacement.
/// Used from the UI thread only.
/// </summary>
public sealed class QuestionPool
{
    public const int ReadyPerTab = 2;

    // background generation runs a few at a time; a question someone is waiting for skips the line
    private const int BackgroundParallel = 3;

    private static readonly Random Random = new();
    private readonly Dictionary<(Category, Level), Queue<PendingQuestion>> _ready = [];
    private readonly SemaphoreSlim _background = new(BackgroundParallel);

    public PendingQuestion Take(Category category, Level level)
    {
        Queue<PendingQuestion> queue = QueueFor(category, level);
        PendingQuestion next = queue.Count > 0 ? queue.Dequeue() : Start(category, level, isWaited: true);
        Fill(category, level);
        return next;
    }

    /// <summary>Tops up every tab's queue for <paramref name="level"/>; tabs that are already full are left alone.</summary>
    public void FillAll(Level level)
    {
        foreach (Category category in Category.All)
        {
            Fill(category, level);
        }
    }

    private void Fill(Category category, Level level)
    {
        Queue<PendingQuestion> queue = QueueFor(category, level);
        while (queue.Count < ReadyPerTab)
        {
            queue.Enqueue(Start(category, level, isWaited: false));
        }
    }

    private Queue<PendingQuestion> QueueFor(Category category, Level level)
    {
        if (!_ready.TryGetValue((category, level), out Queue<PendingQuestion>? queue))
        {
            queue = new Queue<PendingQuestion>();
            _ready[(category, level)] = queue;
        }
        return queue;
    }

    private PendingQuestion Start(Category category, Level level, bool isWaited)
    {
        // topics answered badly come up more often (up to 3x)
        IReadOnlyList<string> topics = category.TopicsFor(level);
        double[] weights = topics.Select(Game.TopicWeight).ToArray();
        double pick = Random.NextDouble() * weights.Sum();
        int i = 0;
        while (i < topics.Count - 1 && (pick -= weights[i]) >= 0) i++;
        string topic = topics[i];
        return new PendingQuestion(topic, isWaited ? QuestionGenerator.GenerateAsync(level, topic) : InBackground(level, topic));
    }

    private async Task<Question> InBackground(Level level, string topic)
    {
        await _background.WaitAsync();
        try
        {
            return await QuestionGenerator.GenerateAsync(level, topic);
        }
        finally
        {
            _background.Release();
        }
    }
}
