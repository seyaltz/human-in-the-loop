using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// How a session's answers read: Claude is asked for one shape (Result, What I did, Worth knowing, Decisions for you,
/// Details), the decisions show in a tinted box, the details fold away, and every turn but the newest folds to one line.
/// </summary>
public static class SessionAnswers
{
    // how the note starts: the transcript finds it by this and hides it
    private const string ShapeStart = "[Dev break - answer shape:";

    /// <summary>
    /// Added to the end of every message sent from a session pane. In the message, not the system prompt: measured, an
    /// --append-system-prompt with the same words was not followed (2 of 2 answers without the headings), the note was.
    /// </summary>
    public const string Shape =
        ShapeStart + " use these markdown headings, in this order, leaving out one with nothing under it: " +
        "## Result (one or two lines: what came out) / ## What I did (the important actions, short bullets) / " +
        "## Worth knowing (what to learn: traps found, why something works the way it does) / " +
        "## Decisions for you (what I must decide for the next step, numbered questions) / " +
        "## Details (tables, logs, command output, long detail - last). A short reply to a short question needs no headings. " +
        "On work longer than a minute, write a one-line \"Now: ... · Done so far: ...\" note between steps about once a minute.]";

    /// <summary>A message with the shape note at its end.</summary>
    public static string WithShape(string message) => message + "\n\n" + Shape;

    /// <summary>A sent message without the shape note (a message sent elsewhere has none and comes back as it was).</summary>
    public static string WithoutShape(string message)
    {
        int at = message.LastIndexOf(ShapeStart, StringComparison.Ordinal);
        return at < 0 ? message : message[..at].TrimEnd();
    }

    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(.+?)\s*:?\s*$");

    /// <summary>Adds an answer to a transcript: its decisions in a tinted box, its details (and all after them) folded.</summary>
    public static void Append(FlowDocument document, string text, FrameworkElement resources)
    {
        Brush Res(string key) => (Brush)resources.FindResource(key);
        foreach ((string? title, string body) in Sections(text))
        {
            if (Is(title, "details"))
            {
                document.Blocks.Add(Folded("Details", body, resources));
            }
            else if (Is(title, "decisions for you"))
            {
                var box = new Section
                {
                    Background = Res("CoralTintBrush"), BorderBrush = Res("CoralInkBrush"), BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 8, 12, 2), Margin = new Thickness(0, 10, 0, 10),
                };
                Move(CodeRenderer.Render("## " + title + "\n" + body), box.Blocks);
                document.Blocks.Add(box);
            }
            else
            {
                CodeRenderer.Append(document, title == null ? body : "## " + title + "\n" + body);
            }
        }
    }

    /// <summary>The answer's result in one line: the first line under "Result", else its first line of text.</summary>
    public static string ResultLine(string answer)
    {
        List<(string? Title, string Body)> sections = Sections(answer);
        string body = sections.FirstOrDefault(s => Is(s.Title, "result")).Body ?? "";
        string line = FirstLine(body);
        if (line.Length == 0) line = FirstLine(string.Join("\n", sections.Select(s => s.Body)));
        return line;
    }

    /// <summary>The first line of text with its markdown marks taken off.</summary>
    public static string FirstLine(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith('|')) continue;
            line = line.TrimStart('#', '-', '*', '>', ' ').Replace("**", "").Replace("`", "");
            if (line.Length > 0) return line;
        }
        return "";
    }

    // the answer cut at its headings (not those inside a code block); text before the first heading has no title
    private static List<(string? Title, string Body)> Sections(string text)
    {
        var sections = new List<(string? Title, string Body)>();
        string? title = null;
        var body = new StringBuilder();
        bool inCode = false;
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inCode = !inCode;
            Match heading = inCode ? Match.Empty : Heading.Match(line);
            // the details hold everything after them
            if (heading.Success && !Is(title, "details"))
            {
                if (title != null || body.ToString().Trim().Length > 0) sections.Add((title, body.ToString().Trim('\n')));
                title = heading.Groups[1].Value.Replace("**", "").Trim();
                body.Clear();
                continue;
            }
            body.Append(line).Append('\n');
        }
        if (title != null || body.ToString().Trim().Length > 0) sections.Add((title, body.ToString().Trim('\n')));
        return sections;
    }

    private static bool Is(string? title, string name) => string.Equals(title?.Trim(), name, StringComparison.OrdinalIgnoreCase);

    // a part that opens on a click: "▸ Details", rendered like the answer
    private static Block Folded(string name, string body, FrameworkElement resources)
    {
        var viewer = new FlowDocumentScrollViewer
        {
            Document = CodeRenderer.Render(body), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false, Focusable = false,
        };
        viewer.Document.FontSize = 13.5;
        viewer.Document.Foreground = (Brush)resources.FindResource("InkBrush");
        var header = new TextBlock { Text = name, FontWeight = FontWeights.Bold, FontSize = 13.5, Foreground = (Brush)resources.FindResource("VioletBrush") };
        return new BlockUIContainer(new Expander { Header = header, Content = viewer, IsExpanded = false }) { Margin = new Thickness(0, 4, 0, 8) };
    }

    private static void Move(FlowDocument from, BlockCollection to)
    {
        foreach (Block block in from.Blocks.ToList())
        {
            from.Blocks.Remove(block);
            to.Add(block);
        }
    }
}

/// <summary>
/// A finished turn folded to one line - "You: <question> · Claude: <result> · <time>" - that opens to show the
/// turn as it was (its blocks go back into the transcript under the line) and closes again.
/// </summary>
internal sealed class TurnFold
{
    private readonly List<Block> _blocks;
    private readonly FlowDocument _document;

    public BlockUIContainer Line { get; }

    /// <summary>Takes the turn's blocks - <paramref name="start"/> to the end of the document - out, and puts the line in their place.</summary>
    public TurnFold(FlowDocument document, Block start, string question, string result, string done, bool failed, FrameworkElement resources,
        Action? rewind = null)
    {
        Brush Res(string key) => (Brush)resources.FindResource(key);
        _document = document;
        _blocks = [];
        for (Block? b = start; b != null; b = b.NextBlock) _blocks.Add(b);
        foreach (Block b in _blocks) document.Blocks.Remove(b);

        var header = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        header.Inlines.Add(new Run("You: ") { FontWeight = FontWeights.Bold, Foreground = Res("VioletBrush") });
        header.Inlines.Add(new Run(Short(question, 90)) { Foreground = Res("InkBrush") });
        if (result.Length > 0)
        {
            header.Inlines.Add(new Run("  ·  Claude: ") { FontWeight = FontWeights.Bold, Foreground = Res("CoralInkBrush") });
            header.Inlines.Add(new Run(Short(result, 140)) { Foreground = failed ? Res("CoralInkBrush") : Res("InkSoftBrush") });
        }
        if (done.Length > 0) header.Inlines.Add(new Run("  ·  " + done) { FontSize = 11.5, Foreground = Res("MutedBrush") });
        object head = header;
        if (rewind != null)
        {
            // the turn's way back, as Esc Esc offers it
            var back = new Button
            {
                Content = "↶", Style = (Style)resources.FindResource("SecondaryButton"), Height = 24, Width = 30, Padding = new Thickness(0),
                FontSize = 13, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Rewind to before this message (code, conversation or both)",
            };
            back.Click += (_, e) =>
            {
                e.Handled = true;
                rewind();
            };
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(back, Dock.Right);
            row.Children.Add(back);
            row.Children.Add(header);
            head = row;
        }
        var expander = new Expander { Header = head, IsExpanded = false, ToolTip = "Show this turn", HorizontalContentAlignment = HorizontalAlignment.Stretch };
        expander.Expanded += (_, _) => Open();
        expander.Collapsed += (_, _) => Close();
        Line = new BlockUIContainer(new Border
        {
            Background = Res("PaperBrush"), BorderBrush = Res("RuleBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), Child = expander,
        })
        { Margin = new Thickness(0, 0, 0, 6) };
        document.Blocks.Add(Line);
    }

    private void Open()
    {
        Block after = Line;
        foreach (Block b in _blocks)
        {
            _document.Blocks.InsertAfter(after, b);
            after = b;
        }
    }

    private void Close()
    {
        foreach (Block b in _blocks) _document.Blocks.Remove(b);
    }

    private static string Short(string text, int max)
    {
        string line = SessionAnswers.FirstLine(text);
        return line.Length > max ? line[..max].TrimEnd() + "…" : line;
    }
}
