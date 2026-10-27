using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// The message box, as the terminal's prompt works: "/" commands and skills, "@" files and folders, Ctrl+R over
/// earlier messages - one completion box above the input - plus ↑ / ↓ through earlier messages, Esc Esc, Ctrl+J,
/// "!" shell commands and the hint line under the box.
/// </summary>
public partial class SessionPane
{
    private const int MaxShown = 60;

    // the CLI's commands in the session's folder (with Dev break's own), and whether reading them failed
    private IReadOnlyList<SlashCommand>? _commands;
    private bool _commandsFailed;

    // what the completion box shows now: which list, its rows, the chosen one, and the word it was built for
    private enum Completing { None, Command, File, History }
    private sealed record Completion(string Title, string Hint, string Tag, string Description, string Value);
    private Completing _completing;
    private List<Completion> _matches = [];
    private int _selected;
    private string? _shownTyped;
    private bool _menuDismissed;
    private bool _historyOpen;
    private IReadOnlyList<string>? _files;

    // ↑ / ↓ through the session's earlier messages: where in them (-1: the draft), and the draft kept meanwhile
    private int _historyAt = -1;
    private string _draft = "";
    private bool _settingText;

    // Esc Esc: the time of the first Esc
    private DateTime _lastEscape;

    private async void LoadCommands(string folder)
    {
        try
        {
            IReadOnlyList<SlashCommand> commands = await SlashCommands.For(folder);
            if (_state?.Info.Folder != folder) return;
            _commands = [.. OwnCommands, .. commands];
        }
        catch (Exception)
        {
            if (_state?.Info.Folder != folder) return;
            _commandsFailed = true;
        }
        _shownTyped = null;
        UpdateMenu();
        // the model names came with the commands
        UpdateBar();
        try
        {
            List<string> files = await FileMentions.For(folder);
            if (_state?.Info.Folder == folder) _files = files;
        }
        catch (Exception)
        {
            // "@" then lists nothing
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // typing leaves the walk through earlier messages
        if (!_settingText) _historyAt = -1;
        UpdateMenu();
    }

    private void InputBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateMenu();

    // another control of the window took the keys: the menu closes (another window being in front leaves it open)
    private void InputBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus == null) return;
        CommandMenu.Visibility = Visibility.Collapsed;
        _historyOpen = false;
        _shownTyped = null;
    }

    private void InputBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateMenu();

    // the "/name" being typed: the message starts with "/" and the caret is still in its first word
    private string? TypedCommand()
    {
        string text = InputBox.Text;
        if (!text.StartsWith('/')) return null;
        int end = text.IndexOfAny([' ', '\t', '\r', '\n']);
        if (end < 0) end = text.Length;
        return InputBox.CaretIndex > 0 && InputBox.CaretIndex <= end ? text[1..end] : null;
    }

    // the "@path" word the caret is in: where it starts (the "@") and what follows it up to the caret
    private (int Start, string Typed)? TypedFile()
    {
        string text = InputBox.Text;
        int caret = InputBox.CaretIndex;
        int start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start >= text.Length || text[start] != '@' || caret <= start) return null;
        // an e-mail address is not a mention: the "@" starts its word
        return (start, text[(start + 1)..caret]);
    }

    // the box follows what is typed: "/" at the start, "@" in a word, or the history Ctrl+R opened; Esc closes it
    // until the word is gone
    private void UpdateMenu()
    {
        if (!InputBox.Text.StartsWith('/') && TypedFile() == null) _menuDismissed = false;
        bool focused = InputBox.IsKeyboardFocused || CommandMenu.Visibility == Visibility.Visible;
        Completing completing = Completing.None;
        string typed = "";
        if (_state != null && focused && !_menuDismissed)
        {
            if (_historyOpen)
            {
                completing = Completing.History;
                typed = InputBox.Text;
            }
            else if (TypedCommand() is { } command)
            {
                completing = Completing.Command;
                typed = command;
            }
            else if (TypedFile() is { } file)
            {
                completing = Completing.File;
                typed = file.Typed;
            }
        }
        UpdateHint(completing == Completing.None);
        if (completing == Completing.None)
        {
            CommandMenu.Visibility = Visibility.Collapsed;
            _shownTyped = null;
            _completing = Completing.None;
            return;
        }
        CommandMenu.Visibility = Visibility.Visible;
        string key = completing + "|" + typed;
        if (key == _shownTyped) return;
        _shownTyped = key;
        _completing = completing;
        (List<Completion> matches, int total, string empty, string waiting) = completing switch
        {
            Completing.Command => Commands(typed),
            Completing.File => Files(typed),
            _ => History(typed),
        };
        _matches = matches;
        CommandList.Children.Clear();
        _selected = 0;
        if (waiting.Length > 0)
        {
            CommandKeys.Text = waiting;
            return;
        }
        foreach (Completion match in _matches.Take(MaxShown)) CommandList.Children.Add(Row(match));
        Select(0);
        string count = _matches.Count > MaxShown ? $"{MaxShown} of {_matches.Count} - type more to narrow" : $"{_matches.Count} of {total}";
        CommandKeys.Text = _matches.Count == 0 ? empty + "  ·  Esc close" : $"↑ ↓ move  ·  Tab or Enter pick  ·  Esc close  ·  {count}";
    }

    private (List<Completion>, int, string, string) Commands(string typed)
    {
        if (_commands == null)
        {
            return ([], 0, "", _commandsFailed ? "Could not read the CLI's commands - type the command, it is sent as it is.  ·  Esc close" : "Reading the CLI's commands...");
        }
        List<Completion> rows = SlashCommands.Match(_commands, typed)
            .Select(c => new Completion("/" + c.Name, c.ArgumentHint, c.Source, c.Description, c.Name)).ToList();
        return (rows, _commands.Count, $"No command or skill matches /{typed} - Enter sends it as it is", "");
    }

    private (List<Completion>, int, string, string) Files(string typed)
    {
        if (_files == null) return ([], 0, "", "Listing the folder's files...  ·  Esc close");
        List<Completion> rows = FileMentions.Match(_files, typed)
            .Select(p => new Completion("@" + p, "", p.EndsWith('/') ? "folder" : "file", "", p)).ToList();
        return (rows, _files.Count, $"No file or folder matches @{typed}", "");
    }

    // Ctrl+R: the session's earlier messages holding what is in the box, newest first
    private (List<Completion>, int, string, string) History(string typed)
    {
        List<string> all = _state?.History ?? [];
        List<Completion> rows = all.AsEnumerable().Reverse().Distinct()
            .Where(m => m.Contains(typed.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(m => new Completion(SessionAnswers.FirstLine(m), "", "earlier", m.Contains('\n') ? m : "", m)).ToList();
        return (rows, all.Count, "No earlier message holds this", "");
    }

    private Border Row(Completion match)
    {
        Brush Res(string key) => (Brush)FindResource(key);
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        name.Inlines.Add(new Run(match.Title)
        {
            FontWeight = FontWeights.Bold, Foreground = Res("InkBrush"),
            FontFamily = _completing == Completing.History ? FontFamily : (FontFamily)FindResource("MonoFont"),
        });
        if (match.Hint.Length > 0) name.Inlines.Add(new Run("  " + match.Hint) { Foreground = Res("MutedBrush") });
        var tag = new Border
        {
            Background = Res("PaperBrush"), BorderBrush = Res("RuleBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = match.Tag, FontSize = 10.5, Foreground = Res("InkSoftBrush") },
        };
        DockPanel.SetDock(tag, Dock.Right);
        var top = new DockPanel();
        top.Children.Add(tag);
        top.Children.Add(name);
        var rows = new StackPanel();
        rows.Children.Add(top);
        if (match.Description.Length > 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = match.Description.Replace('\n', ' '), FontSize = 12, Foreground = Res("InkSoftBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        var row = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 4, 8, 5), Child = rows, Cursor = Cursors.Hand,
            ToolTip = match.Description.Length > 0 ? match.Description : null,
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Pick(match);
        };
        return row;
    }

    private void Select(int index)
    {
        if (CommandList.Children.Count == 0) return;
        _selected = (index + CommandList.Children.Count) % CommandList.Children.Count;
        for (int i = 0; i < CommandList.Children.Count; i++)
        {
            ((Border)CommandList.Children[i]).Background = i == _selected ? (Brush)FindResource("VioletTintBrush") : Brushes.Transparent;
        }
        ((FrameworkElement)CommandList.Children[_selected]).BringIntoView();
    }

    // a command replaces the first word ("/name " and the rest); a path replaces the "@" word ("@path "); an earlier
    // message replaces the whole box
    private void Pick(Completion match)
    {
        string text = InputBox.Text;
        switch (_completing)
        {
            case Completing.Command:
            {
                int end = text.IndexOfAny([' ', '\t', '\r', '\n']);
                string rest = end < 0 ? "" : text[end..].TrimStart(' ', '\t');
                InputBox.Text = "/" + match.Value + " " + rest;
                InputBox.CaretIndex = match.Value.Length + 2;
                break;
            }
            case Completing.File when TypedFile() is { } file:
            {
                int end = file.Start;
                while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                string inserted = "@" + match.Value + " ";
                InputBox.Text = text[..file.Start] + inserted + text[end..].TrimStart(' ');
                InputBox.CaretIndex = file.Start + inserted.Length;
                break;
            }
            case Completing.History:
                _historyOpen = false;
                SetText(match.Value);
                break;
        }
        InputBox.Focus();
    }

    private void SetText(string text)
    {
        _settingText = true;
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        _settingText = false;
    }

    // the line under the box: what a written "/command" takes and does, that "!" runs a shell command here, and what
    // "ultrathink" does
    private void UpdateHint(bool show)
    {
        CommandHint.Inlines.Clear();
        CommandHint.ToolTip = null;
        string text = InputBox.Text;
        Brush violet = (Brush)FindResource("VioletBrush"), muted = (Brush)FindResource("MutedBrush");
        if (show && text.StartsWith('!'))
        {
            CommandHint.Inlines.Add(new Run("! shell") { FontWeight = FontWeights.Bold, Foreground = violet });
            CommandHint.Inlines.Add(new Run($"  ·  runs in this folder ({ShellCommand.ShellName}), not sent to Claude - its output goes with your next message"));
        }
        else if (show && _commands != null && SlashCommands.Find(_commands, text) is { } command)
        {
            CommandHint.Inlines.Add(new Run("/" + command.Name) { FontWeight = FontWeights.Bold, Foreground = violet });
            if (command.ArgumentHint.Length > 0) CommandHint.Inlines.Add(new Run(" " + command.ArgumentHint) { Foreground = muted });
            if (command.Description.Length > 0) CommandHint.Inlines.Add(new Run("  ·  " + command.Description));
            CommandHint.ToolTip = command.Description.Length > 0 ? command.Description : null;
        }
        else if (show && text.Contains("ultrathink", StringComparison.OrdinalIgnoreCase))
        {
            CommandHint.Inlines.Add(new Run("ultrathink") { FontWeight = FontWeights.Bold, Foreground = violet });
            CommandHint.Inlines.Add(new Run("  ·  Claude thinks harder on this message"));
        }
        else if (show && _state?.PendingShell.Count > 0)
        {
            CommandHint.Inlines.Add(new Run("! " + _state.PendingShell.Count + " shell result" + (_state.PendingShell.Count == 1 ? "" : "s")) { FontWeight = FontWeights.Bold, Foreground = violet });
            CommandHint.Inlines.Add(new Run("  ·  go with your next message"));
        }
        CommandHint.Visibility = CommandHint.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CommandMenu.Visibility == Visibility.Visible && MenuKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.Enter && !shift)
        {
            e.Handled = true;
            Send(now: ctrl);
        }
        else if (e.Key == Key.J && ctrl)
        {
            // a new line, as Ctrl+J in the terminal
            e.Handled = true;
            int at = InputBox.CaretIndex;
            InputBox.Text = InputBox.Text.Insert(at, "\n");
            InputBox.CaretIndex = at + 1;
        }
        else if (e.Key == Key.R && ctrl && _state != null)
        {
            e.Handled = true;
            _historyOpen = true;
            _menuDismissed = false;
            _shownTyped = null;
            UpdateMenu();
        }
        else if (e.Key == Key.Tab && shift && _state != null)
        {
            e.Handled = true;
            _ = CycleMode();
        }
        else if (e.Key == Key.System && e.SystemKey == Key.P && _state != null)
        {
            e.Handled = true;
            OpenModels();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = Escape();
        }
        else if (e.Key == Key.OemQuestion && shift && InputBox.Text.Length == 0 && _state != null)
        {
            // "?" on an empty box: the keys and tips
            e.Handled = true;
            ShowSheet(true);
        }
        else if (e.Key == Key.Up && InputBox.Text.Length == 0 && _state?.Queued.Count > 0)
        {
            // the last queued message back into the box
            e.Handled = true;
            string message = _state.Queued[^1];
            _state.Queued.RemoveAt(_state.Queued.Count - 1);
            UpdateQueued();
            (string text, List<string> files) = AttachedFiles.Split(SessionAnswers.WithoutShape(message));
            SetText(text);
            foreach (string file in files) Attach(file);
        }
        else if (e.Key is Key.Up or Key.Down && !ctrl && !shift && Walk(e.Key == Key.Up))
        {
            e.Handled = true;
        }
        else if (e.Key == Key.V && ctrl && PasteFiles())
        {
            e.Handled = true;
        }
    }

    // ↑ on the box's first line goes to the message before, ↓ on its last line to the one after, and past the newest back
    // to what was being written - as in the terminal
    private bool Walk(bool up)
    {
        List<string> history = _state?.History ?? [];
        string text = InputBox.Text;
        int caret = InputBox.CaretIndex;
        if (up && text[..caret].Contains('\n')) return false;
        if (!up && text[caret..].Contains('\n')) return false;
        if (history.Count == 0 || (!up && _historyAt < 0)) return false;
        if (_historyAt < 0) _draft = text;
        int next = up ? (_historyAt < 0 ? history.Count - 1 : _historyAt - 1) : _historyAt + 1;
        if (next < 0) return true;
        if (next >= history.Count)
        {
            _historyAt = -1;
            SetText(_draft);
            return true;
        }
        SetText(history[next]);
        _historyAt = next;
        return true;
    }

    // Esc: stops a running turn; Esc Esc on the box: with text it clears it, empty it opens the rewind list
    private bool Escape()
    {
        if (_state?.Working == true)
        {
            _ = StopTurn();
            return true;
        }
        bool twice = DateTime.Now - _lastEscape < TimeSpan.FromMilliseconds(600);
        _lastEscape = twice ? DateTime.MinValue : DateTime.Now;
        if (!twice || _state == null) return false;
        if (InputBox.Text.Length > 0)
        {
            SetText("");
            _pending.Clear();
            Attachments.Children.Clear();
            Attachments.Visibility = Visibility.Collapsed;
        }
        else
        {
            OpenRewind(null);
        }
        return true;
    }

    // the box's keys, as in the CLI: up and down move, Tab or Enter picks, Esc closes; true when the key was the box's.
    // Enter on a command already written out in full sends it.
    private bool MenuKey(Key key)
    {
        if (key == Key.Escape)
        {
            _menuDismissed = true;
            _historyOpen = false;
            UpdateMenu();
            return true;
        }
        if (_matches.Count == 0) return false;
        switch (key)
        {
            case Key.Down:
                Select(_selected + 1);
                return true;
            case Key.Up:
                Select(_selected - 1);
                return true;
            case Key.Tab:
                Pick(_matches[_selected]);
                return true;
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                if (_completing == Completing.Command && string.Equals(TypedCommand(), _matches[_selected].Value, StringComparison.OrdinalIgnoreCase)) return false;
                Pick(_matches[_selected]);
                return true;
        }
        return false;
    }

    // "!command": run here, shown in the transcript, handed to the next message
    private async Task RunShell(SessionState state, string command)
    {
        using var cancel = new CancellationTokenSource();
        state.Shell = cancel;
        state.Notify();
        Paragraph shown = ShellBlock(state, command, "running...", null);
        try
        {
            (int code, string output) = await ShellCommand.RunAsync(state.Info.Folder, command, cancel.Token);
            state.Document.Blocks.Remove(shown);
            ShellBlock(state, command, output, code);
            state.PendingShell.Add(ShellCommand.Block(command, code, output));
        }
        catch (OperationCanceledException)
        {
            state.Document.Blocks.Remove(shown);
            ShellBlock(state, command, "Stopped.", -1);
        }
        catch (Exception ex)
        {
            state.Document.Blocks.Remove(shown);
            ShellBlock(state, command, "Could not run it: " + ex.Message, -1);
        }
        state.Shell = null;
        state.Notify();
        if (_state == state) UpdateHint(true);
    }

    // "$ command", its output and exit code, in mono, as a terminal shows it
    private Paragraph ShellBlock(SessionState state, string command, string output, int? code)
    {
        Brush Res(string key) => (Brush)FindResource(key);
        var paragraph = new Paragraph
        {
            FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, Background = Res("PaperBrush"),
            BorderBrush = Res("RuleBrush"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 4, 0, 8),
        };
        paragraph.Inlines.Add(new Run("$ " + command) { FontWeight = FontWeights.Bold, Foreground = Res("VioletDarkBrush") });
        if (output.Length > 0) paragraph.Inlines.Add(new Run("\n" + output) { Foreground = Res("InkBrush") });
        if (code is { } c) paragraph.Inlines.Add(new Run($"\nexit code {c}") { Foreground = c == 0 ? Res("MutedBrush") : Res("CoralInkBrush") });
        state.Document.Blocks.Add(paragraph);
        state.ShownIn?.Transcript.ScrollToEnd();
        return paragraph;
    }
}
