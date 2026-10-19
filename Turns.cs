using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DevBreak;

/// <summary>
/// How a conversation reads, the same in every chat: the developer's message in a tinted box, Claude's answer plain
/// under a small label, room between turns - and a new answer is scrolled to its start, so it is read from the top.
/// </summary>
public static class Turns
{
    /// <summary>Adds a message to a chat transcript on screen and scrolls: to the bottom for the developer's, to its start for Claude's.</summary>
    public static Block Add(RichTextBox transcript, bool isMine, string text, FrameworkElement resources)
    {
        Block start = Add(transcript.Document, isMine, text, resources);
        if (isMine) transcript.ScrollToEnd();
        else ScrollToStart(transcript, start);
        return start;
    }

    /// <summary>Adds a message to a chat's document (on screen or not); returns the block where it starts.</summary>
    public static Block Add(FlowDocument document, bool isMine, string text, FrameworkElement resources)
    {
        Brush Res(string key) => (Brush)resources.FindResource(key);
        bool first = document.Blocks.Count == 0;
        Block start;
        if (isMine)
        {
            // the developer's message: a tinted box with a violet edge
            var box = new Section
            {
                Background = Res("VioletTintBrush"), BorderBrush = Res("VioletBrush"), BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(12, 8, 12, 2), Margin = new Thickness(0, first ? 0 : 18, 0, 10),
            };
            box.Blocks.Add(Label("You", Res("VioletBrush")));
            var body = CodeRenderer.Render(text);
            foreach (Block b in new System.Collections.Generic.List<Block>(body.Blocks))
            {
                body.Blocks.Remove(b);
                box.Blocks.Add(b);
            }
            document.Blocks.Add(box);
            start = box;
        }
        else
        {
            start = Label("Claude", Res("CoralInkBrush"));
            start.Margin = new Thickness(0, first ? 0 : 6, 0, 4);
            document.Blocks.Add(start);
            CodeRenderer.Append(document, text);
        }
        return start;
    }

    /// <summary>Scrolls a transcript so that a block is at the top (after layout, so its position is known).</summary>
    public static void ScrollToStart(RichTextBox transcript, Block block) =>
        transcript.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Rect at = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (at.IsEmpty) return;
            transcript.ScrollToVerticalOffset(Math.Max(0, transcript.VerticalOffset + at.Top - 6));
        });

    private static Paragraph Label(string who, Brush color) => new(new Run(who.ToUpperInvariant()))
    {
        FontWeight = FontWeights.Bold, FontSize = 11.5, Foreground = color, Margin = new Thickness(0, 0, 0, 4),
    };
}
