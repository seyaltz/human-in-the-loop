using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevBreak;

/// <summary>A question the task's answer asks the developer, with the answer it suggests.</summary>
public sealed record FactoryQuestion(int Number, string Ask, string Suggested);

/// <summary>
/// A task's answer read for the developer: its headline, the questions it asks (to answer with one click), its findings
/// without the parts shown elsewhere, and the manager's note split into what to do and why the task stopped.
/// </summary>
public static class FactoryDigest
{
    /// <summary>The one-click answers; anything else is the developer's own words.</summary>
    public const string Yes = "(yes)", No = "(no)";

    private static readonly Regex Item = new(@"^\s*(?:\*\*)?(\d+)[.)](?:\*\*)?\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Field = new(@"^\s*[A-Z][A-Z_]+:", RegexOptions.Compiled);
    private static readonly Regex BulletLine = new(@"^\s*[-*•]\s", RegexOptions.Compiled);
    private static readonly Regex SuggestedLead = new(@"^(?:suggested answer:|suggested:|my suggestion:|i suggest)\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string[] Lines(string text) => text.Replace("\r", "").Split('\n');

    private static string Plain(string line) => line.Replace("**", "").Trim();

    /// <summary>Text for a plain label: without the bold and code marks markdown would have rendered.</summary>
    public static string Clean(string text) => text.Replace("**", "").Replace("`", "");

    /// <summary>The answer's TITLE line; else its opening bold sentence; else the task's own title.</summary>
    public static string Headline(string result, string fallback)
    {
        string? title = Lines(result).Select(Plain).FirstOrDefault(l => l.StartsWith("TITLE:", StringComparison.Ordinal));
        if (title != null && title[6..].Trim() is { Length: > 0 } named) return named;
        string start = result.TrimStart();
        if (start.StartsWith("**", StringComparison.Ordinal) && start.IndexOf("**", 2, StringComparison.Ordinal) is var end and > 2 and <= 202)
        {
            return start[2..end].Trim();
        }
        return fallback;
    }

    // the QUESTIONS: block - its first and last line, and its numbered items (an item may wrap onto indented lines)
    private static (int Start, int End, List<(int Number, string Text)> Items) QuestionBlock(string[] lines)
    {
        int start = Array.FindIndex(lines, l => Plain(l).StartsWith("QUESTIONS:", StringComparison.Ordinal));
        var items = new List<(int, string)>();
        if (start < 0) return (-1, -1, items);
        int end = start;
        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) continue;
            // the answer is the model's text: a number that does not fit is not a question, never an exception
            if (Item.Match(line) is { Success: true } m)
            {
                if (int.TryParse(m.Groups[1].Value, out int number)) items.Add((number, m.Groups[2].Value.Trim()));
            }
            else if (items.Count > 0 && char.IsWhiteSpace(line[0]) && !Field.IsMatch(line)) items[^1] = (items[^1].Item1, items[^1].Item2 + " " + line.Trim());
            else break;
            end = i;
        }
        return (start, end, items);
    }

    /// <summary>The questions under the answer's QUESTIONS: line, each split into the ask and the answer it suggests.</summary>
    public static List<FactoryQuestion> Questions(string result) =>
        [.. QuestionBlock(Lines(result)).Items.Select(item =>
        {
            int mark = item.Text.IndexOf('?');
            if (mark < 0) return new FactoryQuestion(item.Number, item.Text, "");
            return new FactoryQuestion(item.Number, item.Text[..(mark + 1)].Trim(), SuggestedLead.Replace(item.Text[(mark + 1)..].Trim(), ""));
        })];

    /// <summary>
    /// The findings to read: without the TITLE line (it is the headline), the questions (they are the next steps) and the
    /// EVIDENCE line; what it could not verify comes apart, to fold away.
    /// </summary>
    public static (string Body, string NotVerified) Body(string result)
    {
        string[] lines = Lines(result);
        (int qStart, int qEnd, _) = QuestionBlock(lines);
        var body = new List<string>();
        var notVerified = new List<string>();
        bool titleSeen = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string plain = Plain(line);
            if (i >= qStart && i <= qEnd && qStart >= 0) continue;
            if (plain.StartsWith("EVIDENCE:", StringComparison.Ordinal)) continue;
            if (!titleSeen && plain.Length > 0)
            {
                titleSeen = true;
                if (plain.StartsWith("TITLE:", StringComparison.Ordinal)) continue;
            }
            if (plain.StartsWith("Not verified", StringComparison.OrdinalIgnoreCase))
            {
                // its bullets (and their indented lines), up to the first line that is neither
                int j = i + 1;
                for (; j < lines.Length; j++)
                {
                    string next = lines[j];
                    if (next.Trim().Length == 0)
                    {
                        if (j + 1 < lines.Length && BulletLine.IsMatch(lines[j + 1])) continue;
                        break;
                    }
                    if (!BulletLine.IsMatch(next) && !char.IsWhiteSpace(next[0])) break;
                    notVerified.Add(next);
                }
                i = j - 1;
                continue;
            }
            body.Add(line);
        }
        return (Regex.Replace(string.Join("\n", body), @"\n{3,}", "\n\n").Trim(), string.Join("\n", notVerified).Trim());
    }

    /// <summary>The manager's note: what the developer should do, and the technical reason the task stopped.</summary>
    public static (string Action, string Why) SplitNote(string note)
    {
        int why = note.IndexOf("Why it stopped:", StringComparison.Ordinal);
        if (why < 0) return (note.Trim(), "");
        string rest = note[(why + "Why it stopped:".Length)..];
        int proposals = rest.IndexOf("Proposed changes to the factory", StringComparison.Ordinal);
        return (note[..why].Trim(), (proposals >= 0 ? rest[..proposals] : rest).Trim());
    }

    /// <summary>The developer's answers, as one message to the task: each answered question, then the answer.</summary>
    public static string Answers(IReadOnlyList<FactoryQuestion> questions, IReadOnlyDictionary<int, string> answers)
    {
        var text = new StringBuilder("My answers to your questions:");
        foreach (FactoryQuestion q in questions)
        {
            if (!answers.TryGetValue(q.Number, out string? answer) || answer.Trim().Length == 0) continue;
            string said = answer == Yes ? (q.Suggested.Length > 0 ? "Yes, as you suggested: " + q.Suggested : "Yes.")
                : answer == No ? "No."
                : answer.Trim();
            text.Append($"\n{q.Number}. {q.Ask}\n   → {said}");
        }
        return text.ToString();
    }

    /// <summary>When something happened, and how long ago: "11:58 · 2 h ago".</summary>
    public static string Ago(DateTime at, DateTime now)
    {
        TimeSpan since = now - at;
        string when = at.Date == now.Date ? at.ToString("HH:mm") : at.ToString("dd/MM HH:mm");
        string ago = since.TotalMinutes < 1 ? "just now"
            : since.TotalHours < 1 ? $"{(int)since.TotalMinutes} min ago"
            : since.TotalDays < 1 ? $"{(int)since.TotalHours} h ago"
            : $"{(int)since.TotalDays} d ago";
        return $"{when} · {ago}";
    }
}
