using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// The "?" sheet, as the CLI's "?" lists its shortcuts: every key of a pane, then the habits the Claude Code docs
/// recommend - each with a button that does it here, where there is one, and its page in the docs.
/// </summary>
public partial class SessionPane
{
    private const string Docs = "https://code.claude.com/docs/en/";

    private static readonly (string Keys, string What)[] Keys =
    [
        ("Enter", "send - while Claude works it queues, and goes when the turn ends"),
        ("Ctrl+Enter", "send now, into the running turn"),
        ("Shift+Enter  ·  Ctrl+J", "a new line"),
        ("Esc", "stop the turn - the session stays"),
        ("Esc Esc", "clear the box; on an empty box, rewind"),
        ("↑  ↓", "earlier messages (↑ on an empty box takes back a queued one)"),
        ("Ctrl+R", "search earlier messages"),
        ("Shift+Tab", "permission mode: Bypass, Ask, Accept edits, Plan"),
        ("Alt+P", "model"),
        ("/", "commands and skills"),
        ("@", "a file or folder, read with the message"),
        ("!", "run a shell command here; its output goes with your next message"),
        ("/btw", "an aside, answered outside the conversation"),
        ("Ctrl+V  ·  drop  ·  +", "add files, folders, screenshots"),
        ("?", "this sheet"),
    ];

    private void ShowSheet(bool show)
    {
        if (!show || _state == null)
        {
            SheetOverlay.Visibility = Visibility.Collapsed;
            InputBox.Focus();
            return;
        }
        SheetBody.Children.Clear();
        SheetBody.Children.Add(SheetHeading("Keys"));
        var keys = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keys.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < Keys.Length; i++)
        {
            keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock
            {
                Text = Keys[i].Keys, FontFamily = (FontFamily)FindResource("MonoFont"), FontWeight = FontWeights.Bold, FontSize = 12.5,
                Foreground = (Brush)FindResource("VioletDarkBrush"), Margin = new Thickness(0, 2, 16, 2),
            };
            var what = new TextBlock { Text = Keys[i].What, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), Foreground = (Brush)FindResource("InkBrush") };
            Grid.SetRow(key, i);
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 1);
            keys.Children.Add(key);
            keys.Children.Add(what);
        }
        SheetBody.Children.Add(keys);
        SheetBody.Children.Add(SheetHeading("Tips from the Claude Code docs"));
        Tip("Plan before editing: Claude reads and proposes, changes nothing until you approve.", "Plan mode", async () => await SetMode("plan"), "permission-modes");
        Tip("Give Claude a way to check its work - a test, a build, a script - so it can see when it is done.", null, null, "best-practices");
        Tip("When the context fills, /compact with what to keep - \"/compact keep the API decisions\".", "/compact", () => PutInBox("/compact "), "best-practices");
        Tip("/clear between unrelated tasks: a fresh session works better than a long mixed one.", "/clear", () => PutInBox("/clear"), "best-practices");
        Tip("Press Esc early when it drifts. After two corrections that did not help, /clear and say it better.", null, null, "best-practices");
        Tip("Name files with @ instead of asking Claude to find and read them.", "@", () => PutInBox("@"), "common-workflows");
        Tip("Paste a screenshot of the error or the screen (Ctrl+V) - it reads images.", null, null, "common-workflows");
        Tip("Send research to a subagent - \"use a subagent to find where X is handled\" - to keep this conversation clean.", null, null, "common-workflows");
        Tip("A second session reviews better than the one that wrote the code: + Pane, and ask it to review.", null, null, "best-practices");
        Tip("Rename sessions you will come back to; \"claude --resume <name>\" finds them in a terminal.", "Rename", () => AskRename(_state!), "best-practices");
        Tip("Try another way without losing this one: branch the session.", "Branch", Branch, "common-workflows");
        Tip("Went wrong? Rewind the code, the conversation or both to before a message.", "Rewind", () => OpenRewind(null), "checkpointing");
        Tip("Write ultrathink in a message for a hard problem: Claude thinks harder on it.", null, null, "model-config");
        Tip("A quick question that should not enter the conversation: /btw.", "/btw", () => PutInBox("/btw "), "interactive-mode");
        SheetOverlay.Visibility = Visibility.Visible;
    }

    private TextBlock SheetHeading(string text) => new()
    {
        Text = text, FontWeight = FontWeights.Bold, FontSize = 14.5, Foreground = (Brush)FindResource("InkBrush"), Margin = new Thickness(0, 4, 0, 6),
    };

    // a tip: its line, a button that does it (closing the sheet), and "docs" to its page
    private void Tip(string text, string? action, Action? act, string page)
    {
        var line = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("InkBrush"), VerticalAlignment = VerticalAlignment.Center };
        line.Inlines.Add(new Run("• " + text + "  "));
        var docs = new Hyperlink(new Run("docs")) { Foreground = (Brush)FindResource("VioletBrush"), ToolTip = Docs + page };
        docs.Click += (_, _) => Process.Start(new ProcessStartInfo(Docs + page) { UseShellExecute = true });
        line.Inlines.Add(docs);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        if (action != null && act != null)
        {
            Button button = CardButton(action, false, () =>
            {
                ShowSheet(false);
                act();
            });
            button.Height = 28;
            button.FontSize = 12;
            button.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
        row.Children.Add(line);
        SheetBody.Children.Add(row);
    }

    private void CloseSheet_Click(object sender, RoutedEventArgs e) => ShowSheet(false);
}
