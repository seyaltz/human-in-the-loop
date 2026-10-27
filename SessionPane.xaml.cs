using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DevBreak;

/// <summary>
/// One session on screen: its header (a status light, title, folder, Setup, Copy resume, Open folder), its
/// transcript and the message box with the files added to it. The Sessions view shows one pane or several.
/// </summary>
public partial class SessionPane : UserControl
{
    private SessionState? _state;

    // files added to the message being written
    private readonly List<string> _pending = [];

    // ticks the live box (how long the turn has run, how old its last note is) while a turn runs
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    // the "/" menu: the CLI's commands in the session's folder, the ones matching what is typed, the one picked by the keys
    private const int MaxShownCommands = 60;
    private IReadOnlyList<SlashCommand>? _commands;
    private bool _commandsFailed;
    private List<SlashCommand> _matches = [];
    private int _selected;
    private string? _shownTyped;
    private bool _menuDismissed;

    /// <summary>The pane was clicked or typed in: it becomes the active one.</summary>
    public event Action<SessionPane>? Activated;

    /// <summary>The pane's close button was pressed.</summary>
    public event Action<SessionPane>? CloseRequested;

    /// <summary>A new session finished its first turn, so it now has a file and a title.</summary>
    public event Action? SessionSaved;

    public SessionState? State => _state;

    public SessionPane()
    {
        InitializeComponent();
        PreviewMouseDown += (_, _) => Activated?.Invoke(this);
        PreviewGotKeyboardFocus += (_, _) => Activated?.Invoke(this);
        SetupArea.BackRequested += () => ShowSetup(false);
        SetupArea.UseRequested += text =>
        {
            ShowSetup(false);
            InputBox.Text = text;
            InputBox.CaretIndex = text.Length;
            InputBox.Focus();
        };
        _clock.Tick += (_, _) => UpdateLive();
        SizeChanged += (_, _) =>
        {
            bool narrow = ActualWidth < 760;
            DockPanel.SetDock(HeaderButtons, narrow ? Dock.Bottom : Dock.Right);
            HeaderButtons.Margin = new Thickness(0, narrow ? 10 : 0, 0, 0);
        };
    }

    /// <summary>Marks this pane as the active one (outlined) when more than one pane is on screen.</summary>
    public void SetActive(bool active, bool several)
    {
        Outline.BorderBrush = active && several ? (Brush)FindResource("VioletBrush") : Brushes.Transparent;
        ClosePaneButton.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows a session here, taking it from the pane that had it; loads its transcript the first time.</summary>
    public async void Show(SessionState state)
    {
        if (_state == state) return;
        if (state.ShownIn != null && state.ShownIn != this) state.ShownIn.Clear();
        Release();
        _state = state;
        state.ShownIn = this;
        state.Changed += OnChanged;
        Loading.Visibility = Visibility.Collapsed;
        SessionTitle.Text = state.Info.Title;
        SessionFolder.Text = state.Info.Folder + "   ·   session " + state.Info.Id;
        Transcript.Document = state.Document;
        CopyResumeButton.IsEnabled = OpenFolderButton.IsEnabled = SetupButton.IsEnabled = true;
        ShowSetup(false);
        Update();
        UpdateQueued();
        LoadCommands(state.Info.Folder);
        if (!state.Loaded)
        {
            Loading.Visibility = Visibility.Visible;
            IReadOnlyList<SessionEntry> entries = await Task.Run(() => ClaudeSessions.Transcript(state.Info.Id));
            if (!state.Loaded)
            {
                state.Loaded = true;
                foreach (SessionEntry entry in entries) Add(state, entry);
            }
            if (_state == state) Loading.Visibility = Visibility.Collapsed;
        }
        if (_state == state) ShowLatest();
        InputBox.Focus();
    }

    /// <summary>Shows nothing (the session moved to the other pane, or this pane is closing).</summary>
    public void Clear()
    {
        Release();
        Transcript.Document = CodeRenderer.Render("");
        SessionTitle.Text = "Pick a session";
        SessionFolder.Text = "Pick one on the left for this pane.";
        CopyResumeButton.IsEnabled = OpenFolderButton.IsEnabled = SetupButton.IsEnabled = false;
        ShowSetup(false);
        Update();
    }

    private void Release()
    {
        if (_state == null) return;
        _state.Changed -= OnChanged;
        if (_state.ShownIn == this) _state.ShownIn = null;
        _state = null;
        // a FlowDocument can belong to one RichTextBox: give this one back before another pane takes it
        Transcript.Document = new FlowDocument();
        // the files were for that session's next message
        _pending.Clear();
        Attachments.Children.Clear();
        Attachments.Visibility = Visibility.Collapsed;
        // the commands were that folder's
        _commands = null;
        _commandsFailed = false;
        _shownTyped = null;
        CommandMenu.Visibility = CommandHint.Visibility = QueuedText.Visibility = Visibility.Collapsed;
    }

    private void OnChanged(SessionState state) => Update();

    // the light, the buttons and the working line follow the session's state
    private void Update()
    {
        bool running = _state?.Working == true;
        InputBox.IsEnabled = AddFilesButton.IsEnabled = _state != null;
        // while a turn runs, Send queues the message
        SendButton.IsEnabled = _state != null;
        StopButton.IsEnabled = running;
        if (running) WorkingText.Visibility = Visibility.Collapsed;
        UpdateLive();
        if (running) _clock.Start();
        else _clock.Stop();
        // the title may come later (Claude writes one for an untitled session)
        if (_state != null) SessionTitle.Text = _state.Info.Title;
        Lights.Set(Light, _state, this);
    }

    // the live box: "Working · 3 min 10 s · 41 steps", the last note Claude wrote on the way, and how old it is
    private void UpdateLive()
    {
        SessionState? state = _state;
        LiveBox.Visibility = state?.Working == true ? Visibility.Visible : Visibility.Collapsed;
        if (state?.Working != true) return;
        LiveTitle.Text = $"● Working · {Span(DateTime.Now - state.TurnStartedAt)} · {state.Steps} step{(state.Steps == 1 ? "" : "s")}";
        bool note = state.LastNote.Length > 0;
        LiveNote.Visibility = LiveAge.Visibility = note ? Visibility.Visible : Visibility.Collapsed;
        if (!note) return;
        LiveNote.Text = state.LastNote;
        LiveAge.Text = $"Claude wrote this {Span(DateTime.Now - state.LastNoteAt)} ago";
    }

    private static string Span(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} s" : $"{(int)span.TotalMinutes} min {span.Seconds} s";

    private void ShowLatest()
    {
        if (_state?.ReplyStart != null) Turns.ScrollToStart(Transcript, _state.ReplyStart);
        else Transcript.ScrollToEnd();
    }

    private void Setup_Click(object sender, RoutedEventArgs e) => ShowSetup(SetupArea.Visibility != Visibility.Visible);

    private void ShowSetup(bool show)
    {
        if (show && _state == null) return;
        SetupArea.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ChatArea.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SetupButton.Content = show ? "Chat" : "Setup";
        if (show) SetupArea.Show(_state!.Info.Folder);
    }

    private async void LoadCommands(string folder)
    {
        try
        {
            IReadOnlyList<SlashCommand> commands = await SlashCommands.For(folder);
            if (_state?.Info.Folder != folder) return;
            _commands = commands;
        }
        catch (Exception)
        {
            if (_state?.Info.Folder != folder) return;
            _commandsFailed = true;
        }
        _shownTyped = null;
        UpdateMenu();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateMenu();

    private void InputBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateMenu();

    // another control of the window took the keys: the menu closes (another window being in front leaves it open)
    private void InputBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus == null) return;
        CommandMenu.Visibility = Visibility.Collapsed;
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

    // the menu follows what is typed: open while the first word starting with "/" is written, closed by Esc until it is gone
    private void UpdateMenu()
    {
        if (!InputBox.Text.StartsWith('/')) _menuDismissed = false;
        string? typed = _state == null || _menuDismissed || (!InputBox.IsKeyboardFocused && CommandMenu.Visibility != Visibility.Visible) ? null : TypedCommand();
        UpdateHint(typed == null);
        if (typed == null)
        {
            CommandMenu.Visibility = Visibility.Collapsed;
            _shownTyped = null;
            return;
        }
        CommandMenu.Visibility = Visibility.Visible;
        if (typed == _shownTyped) return;
        _shownTyped = typed;
        CommandList.Children.Clear();
        _selected = 0;
        if (_commands == null)
        {
            _matches = [];
            CommandKeys.Text = _commandsFailed
                ? "Could not read the CLI's commands - type the command, it is sent as it is.  ·  Esc close"
                : "Reading the CLI's commands...";
            return;
        }
        _matches = SlashCommands.Match(_commands, typed);
        Brush Res(string key) => (Brush)FindResource(key);
        foreach (SlashCommand command in _matches.Take(MaxShownCommands))
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
            name.Inlines.Add(new Run("/" + command.Name) { FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("MonoFont"), Foreground = Res("InkBrush") });
            if (command.ArgumentHint.Length > 0) name.Inlines.Add(new Run("  " + command.ArgumentHint) { Foreground = Res("MutedBrush") });
            var tag = new Border
            {
                Background = Res("PaperBrush"), BorderBrush = Res("RuleBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = command.Source, FontSize = 10.5, Foreground = Res("InkSoftBrush") },
            };
            DockPanel.SetDock(tag, Dock.Right);
            var top = new DockPanel();
            top.Children.Add(tag);
            top.Children.Add(name);
            var rows = new StackPanel();
            rows.Children.Add(top);
            if (command.Description.Length > 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = command.Description, FontSize = 12, Foreground = Res("InkSoftBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0),
                });
            }
            var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 4, 8, 5), Child = rows, Cursor = Cursors.Hand, ToolTip = command.Description.Length > 0 ? command.Description : null };
            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                Pick(command);
            };
            CommandList.Children.Add(row);
        }
        Select(0);
        string count = _matches.Count > MaxShownCommands ? $"{MaxShownCommands} of {_matches.Count} - type more to narrow" : $"{_matches.Count} of {_commands.Count}";
        CommandKeys.Text = _matches.Count == 0
            ? $"No command or skill matches /{typed} - Enter sends it as it is  ·  Esc close"
            : $"↑ ↓ move  ·  Tab or Enter pick  ·  Esc close  ·  {count}";
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

    // the picked command replaces the word typed, followed by a space for its arguments
    private void Pick(SlashCommand command)
    {
        string text = InputBox.Text;
        int end = text.IndexOfAny([' ', '\t', '\r', '\n']);
        string rest = end < 0 ? "" : text[end..].TrimStart(' ', '\t');
        InputBox.Text = "/" + command.Name + " " + rest;
        InputBox.CaretIndex = command.Name.Length + 2;
        InputBox.Focus();
    }

    // under the menu, once a known command is written: "/model <model> · Set the AI model for Claude Code"
    private void UpdateHint(bool show)
    {
        SlashCommand? command = show && _commands != null ? SlashCommands.Find(_commands, InputBox.Text) : null;
        CommandHint.Visibility = command != null ? Visibility.Visible : Visibility.Collapsed;
        if (command == null) return;
        CommandHint.Inlines.Clear();
        CommandHint.Inlines.Add(new Run("/" + command.Name) { FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("VioletBrush") });
        if (command.ArgumentHint.Length > 0) CommandHint.Inlines.Add(new Run(" " + command.ArgumentHint) { Foreground = (Brush)FindResource("MutedBrush") });
        if (command.Description.Length > 0) CommandHint.Inlines.Add(new Run("  ·  " + command.Description));
        CommandHint.ToolTip = command.Description.Length > 0 ? command.Description : null;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CommandMenu.Visibility == Visibility.Visible && MenuKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send(now: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }
        else if (e.Key == Key.Escape && _state?.Working == true)
        {
            e.Handled = true;
            _ = StopTurn();
        }
        else if (e.Key == Key.Up && InputBox.Text.Length == 0 && _state?.Queued.Count > 0)
        {
            // the last queued message back into the box
            e.Handled = true;
            string message = _state.Queued[^1];
            _state.Queued.RemoveAt(_state.Queued.Count - 1);
            UpdateQueued();
            (string text, List<string> files) = AttachedFiles.Split(SessionAnswers.WithoutShape(message));
            InputBox.Text = text;
            InputBox.CaretIndex = text.Length;
            foreach (string file in files) Attach(file);
        }
        else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && PasteFiles())
        {
            e.Handled = true;
        }
    }

    // the menu's keys, as in the CLI: up and down move, Tab or Enter picks, Esc closes; true when the key was the menu's.
    // Enter on a command already written out in full sends it.
    private bool MenuKey(Key key)
    {
        if (key == Key.Escape)
        {
            _menuDismissed = true;
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
                if (string.Equals(TypedCommand(), _matches[_selected].Name, StringComparison.OrdinalIgnoreCase)) return false;
                Pick(_matches[_selected]);
                return true;
        }
        return false;
    }

    /// <summary>Ctrl+V with a screenshot or copied files on the clipboard adds them; plain text pastes as usual.</summary>
    private bool PasteFiles()
    {
        if (Clipboard.ContainsFileDropList())
        {
            string[] files = Clipboard.GetFileDropList().Cast<string>().ToArray();
            foreach (string file in files) Attach(file);
            return files.Length > 0;
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource image)
        {
            Attach(AttachedFiles.SaveImage(image));
            return true;
        }
        return false;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Add files to your message", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (string file in dialog.FileNames) Attach(file);
        InputBox.Focus();
    }

    private void ChatArea_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (_state == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void ChatArea_PreviewDragLeave(object sender, DragEventArgs e)
    {
        // moving from one part of the pane to another also leaves: hide only when the pointer left the pane
        Point at = e.GetPosition(ChatArea);
        if (at.X <= 0 || at.Y <= 0 || at.X >= ChatArea.ActualWidth || at.Y >= ChatArea.ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void ChatArea_PreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_state == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (string file in files) Attach(file);
        e.Handled = true;
        InputBox.Focus();
    }

    /// <summary>Adds a file to the next message: a chip above the box, with an × that takes it out again.</summary>
    private void Attach(string file)
    {
        if (_pending.Contains(file, StringComparer.OrdinalIgnoreCase)) return;
        _pending.Add(file);
        FrameworkElement? chip = null;
        chip = AttachedFiles.Chip(file, this, () =>
        {
            _pending.Remove(file);
            Attachments.Children.Remove(chip);
            Attachments.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        Attachments.Children.Add(chip);
        Attachments.Visibility = Visibility.Visible;
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    // Enter: the message is sent, or queued while a turn runs (as the CLI queues it); Ctrl+Enter while a turn runs: sent
    // now, into the running turn
    private void Send(bool now = false)
    {
        SessionState? state = _state;
        string text = InputBox.Text.Trim();
        if (state == null || (text.Length == 0 && _pending.Count == 0)) return;
        // Claude gets the files' paths in the message (it reads them itself; the transcript shows them as chips), and
        // the note asking for the answer's shape and for progress notes on long work (hidden in the transcript) - not after a
        // "/command": measured, the CLI then passes the note to the command as its arguments (<command-args>...</command-args>)
        string body = AttachedFiles.Message(text, _pending);
        string message = text.StartsWith('/') ? body : SessionAnswers.WithShape(body);
        InputBox.Clear();
        _pending.Clear();
        Attachments.Children.Clear();
        Attachments.Visibility = Visibility.Collapsed;
        if (!state.Working)
        {
            _ = RunTurn(state, message);
        }
        else if (now && state.Process != null)
        {
            // measured: a message written during a turn joins it; shown with the turn's work
            Add(state, new SessionEntry(SessionEntryKind.Note, "↳ **You, during the turn:** " + text));
            _ = state.Process.SendNowAsync(message);
        }
        else
        {
            state.Queued.Add(message);
            UpdateQueued();
        }
    }

    // the messages waiting for the running turn to end, above the box
    private void UpdateQueued()
    {
        List<string> queued = _state?.Queued ?? [];
        QueuedText.Visibility = queued.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (queued.Count == 0) return;
        string first = SessionAnswers.FirstLine(AttachedFiles.Split(SessionAnswers.WithoutShape(queued[0])).Text);
        QueuedText.Text = $"Queued{(queued.Count > 1 ? $" ({queued.Count})" : "")}: {first}  ·  sent when this turn ends · ↑ takes it back";
    }

    private async Task RunTurn(SessionState state, string message)
    {
        Add(state, new SessionEntry(SessionEntryKind.You, message));
        state.Running = new CancellationTokenSource();
        state.Steps = 0;
        state.Failed = false;
        state.Stopping = false;
        state.TurnStartedAt = DateTime.Now;
        state.LastNote = "";
        state.Notify();
        bool wasNew = state.IsNew;
        try
        {
            SessionProcess process = await ProcessFor(state);
            bool finished = await process.TurnAsync(message);
            if (!finished && state.Stopping) Add(state, new SessionEntry(SessionEntryKind.Error, "Stopped."));
            if (finished || ClaudeSessions.FileOf(state.Info.Id) != null) state.IsNew = false;
        }
        catch (Exception ex)
        {
            Add(state, new SessionEntry(SessionEntryKind.Error, "Could not run the claude CLI: " + ex.Message));
        }
        state.Running.Dispose();
        state.Running = null;
        state.Stopping = false;
        state.Notify();
        if (wasNew && !state.IsNew) SessionSaved?.Invoke();
        Stamp(state);
        StampLater(state);
        if (state.Queued.Count > 0)
        {
            string next = state.Queued[0];
            state.Queued.RemoveAt(0);
            if (_state == state) UpdateQueued();
            await RunTurn(state, next);
        }
    }

    /// <summary>
    /// The session's open process, started (resuming the session) when it has none, it ended, its model or effort was
    /// changed, or the session file was written by someone else since (the session used in a terminal): two writers
    /// would split its history.
    /// </summary>
    private async Task<SessionProcess> ProcessFor(SessionState state)
    {
        SessionSetting setting = SessionSettings.Of(state.Info.Id);
        SessionProcess? process = state.Process;
        if (process != null && process.Usable && process.Launch.Model == setting.Model && process.Launch.Effort == setting.Effort
            && !ChangedOutside(state))
        {
            return process;
        }
        process?.Dispose();
        state.Process = null;
        process = await SessionProcess.StartAsync(new SessionLaunch(state.Info.Folder, state.Info.Id, state.IsNew, setting.Mode,
            setting.Model, setting.Effort));
        Wire(state, process);
        state.Process = process;
        return process;
    }

    // what the process reports goes to the session's transcript and state, whichever pane shows it
    private void Wire(SessionState state, SessionProcess process)
    {
        process.Entry += entry => Dispatcher.Invoke(() =>
        {
            if (state.Process != process) return;
            if (state.Stopping && entry.Kind == SessionEntryKind.Error) entry = entry with { Text = "Stopped." };
            if (entry.Kind == SessionEntryKind.Tool) state.Steps++;
            if (entry.Kind == SessionEntryKind.Note)
            {
                state.LastNote = SessionAnswers.FirstLine(entry.Text);
                state.LastNoteAt = DateTime.Now;
            }
            Add(state, entry);
            state.Notify();
        });
        process.Result += root => Dispatcher.Invoke(() =>
        {
            if (root.TryGetProperty("total_cost_usd", out System.Text.Json.JsonElement cost) && cost.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                state.Cost += cost.GetDouble();
            }
            state.Notify();
        });
        process.UserMessage += (uuid, before) => Dispatcher.Invoke(() =>
        {
            // the turn on screen gets its rewind point once (a message sent during the turn joins it)
            if (state.TurnStart != null && state.Marks.All(m => m.Start != state.TurnStart))
            {
                state.Marks.Add(new TurnMark(state.TurnStart, state.TurnQuestion, uuid, before));
            }
        });
        process.Exited += err => Dispatcher.Invoke(() =>
        {
            // a process replaced or closed on purpose is not the session's any more: its end is no error
            if (state.Process != process) return;
            state.Process = null;
            if (state.Working && !state.Stopping)
            {
                Add(state, new SessionEntry(SessionEntryKind.Error, "claude exited" + (err.Length > 0 ? ": " + err : "")));
            }
        });
    }

    private static bool ChangedOutside(SessionState state)
    {
        if (state.FileStamp is not { } stamp || ClaudeSessions.FileOf(state.Info.Id) is not { } file) return false;
        var info = new System.IO.FileInfo(file);
        return info.Length != stamp.Length || info.LastWriteTimeUtc != stamp.Time;
    }

    // the session file as its process left it: taken when the turn ends and again a few seconds later, as the CLI
    // writes a title and the last prompt after the result
    private static void Stamp(SessionState state)
    {
        if (ClaudeSessions.FileOf(state.Info.Id) is not { } file) return;
        var info = new System.IO.FileInfo(file);
        state.FileStamp = (info.Length, info.LastWriteTimeUtc);
    }

    private static async void StampLater(SessionState state)
    {
        SessionProcess? process = state.Process;
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (state.Process == process && !state.Working) Stamp(state);
    }

    // Stop (or Esc while a turn runs): the turn is interrupted and the session's process stays; if claude does not
    // answer in 10 s, it is killed as before
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopTurn();

    private async Task StopTurn()
    {
        SessionState? state = _state;
        if (state?.Working != true) return;
        state.Stopping = true;
        state.Queued.Clear();
        UpdateQueued();
        SessionProcess? process = state.Process;
        if (process == null) return;
        try
        {
            await process.ControlAsync("interrupt", timeout: TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            process.Dispose();
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (state.Working && state.Stopping && state.Process == process) process.Dispose();
    }

    private void CopyResume_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        Clipboard.SetText(ClaudeSessions.ResumeCommand(_state.Info));
        WorkingText.Text = "Copied - paste it in a terminal to continue there.";
        WorkingText.Visibility = Visibility.Visible;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_state != null) Process.Start(new ProcessStartInfo(_state.Info.Folder) { UseShellExecute = true });
    }

    private void ClosePane_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);

    /// <summary>
    /// Adds a step of a session to its transcript: the developer's message in its box, with its files as chips; one
    /// "Claude" label per turn with only the turn's answer under it (its decisions boxed, its details folded); the work on
    /// the way folded into one line; the done line. A new message folds the turn before it to one line. When the session
    /// is on screen (in whichever pane), the view goes to the start of the reply once and then stays.
    /// </summary>
    private void Add(SessionState state, SessionEntry entry)
    {
        FlowDocument document = state.Document;
        RichTextBox? shown = state.ShownIn?.Transcript;
        Brush Res(string key) => (Brush)FindResource(key);
        switch (entry.Kind)
        {
            case SessionEntryKind.You:
                FoldTurn(state);
                (string text, List<string> files) = AttachedFiles.Split(SessionAnswers.WithoutShape(entry.Text));
                state.TurnStart = Turns.Add(document, isMine: true, text.Length > 0 ? text : "(files)", this);
                state.TurnQuestion = text.Length > 0 ? text : string.Join(", ", files.Select(f => System.IO.Path.GetFileName(f.TrimEnd('\\', '/'))));
                if (files.Count > 0)
                {
                    var chips = new WrapPanel();
                    foreach (string file in files) chips.Children.Add(AttachedFiles.Chip(file, this));
                    ((Section)state.TurnStart).Blocks.Add(new BlockUIContainer(chips) { Margin = new Thickness(0, 4, 0, 6) });
                }
                state.ReplyStart = null;
                state.Group = null;
                shown?.ScrollToEnd();
                return;
            case SessionEntryKind.Note:
            case SessionEntryKind.Tool:
                StartReply(state, shown);
                if (state.Group == null)
                {
                    state.Group = new StepGroup(Res("VioletBrush"), Res("InkSoftBrush"), (FontFamily)FindResource("MonoFont"), this);
                    document.Blocks.Add(state.Group.Block);
                }
                if (entry.Kind == SessionEntryKind.Tool) state.Group.AddStep(entry.Text);
                else state.Group.AddNote(entry.Text);
                return;
            case SessionEntryKind.Answer:
                StartReply(state, shown);
                state.Group?.DropNote(entry.Text);
                SessionAnswers.Append(document, entry.Text, this);
                state.TurnResult = SessionAnswers.ResultLine(entry.Text);
                state.Group = null;
                return;
            case SessionEntryKind.Done:
                state.TurnDone = entry.Text;
                state.Group = null;
                document.Blocks.Add(new Paragraph(new Run("✓ done · " + entry.Text))
                {
                    FontSize = 11.5, Foreground = Res("InkSoftBrush"), Margin = new Thickness(0, 2, 0, 8),
                });
                return;
            case SessionEntryKind.Error:
                state.Group = null;
                state.Failed = true;
                state.TurnFailed = true;
                if (state.TurnResult.Length == 0) state.TurnResult = entry.Text;
                document.Blocks.Add(new Paragraph(new Run(entry.Text))
                {
                    Foreground = Res("CoralInkBrush"), Margin = new Thickness(0, 4, 0, 8),
                });
                return;
        }
    }

    // the turn on screen, finished, becomes one line; the new turn starts fresh
    private void FoldTurn(SessionState state)
    {
        if (state.TurnStart != null && state.TurnStart.Parent == state.Document)
            new TurnFold(state.Document, state.TurnStart, state.TurnQuestion, state.TurnResult, state.TurnDone, state.TurnFailed, this);
        state.TurnStart = null;
        state.TurnQuestion = state.TurnResult = state.TurnDone = "";
        state.TurnFailed = false;
    }

    // the "Claude" label, once per turn; on screen, the view goes to it the first time
    private void StartReply(SessionState state, RichTextBox? shown)
    {
        if (state.ReplyStart != null) return;
        state.ReplyStart = Turns.Add(state.Document, isMine: false, "", this);
        if (shown != null && state.Loaded && state.Working) Turns.ScrollToStart(shown, state.ReplyStart);
    }
}

/// <summary>The status light of a session: green and pulsing while Claude works, red after a failed or stopped turn.</summary>
public static class Lights
{
    public static void Set(Ellipse light, SessionState? state, FrameworkElement resources)
    {
        light.BeginAnimation(UIElement.OpacityProperty, null);
        light.Opacity = 1;
        if (state == null || (!state.Working && !state.Failed))
        {
            light.Visibility = Visibility.Collapsed;
            return;
        }
        light.Visibility = Visibility.Visible;
        light.Fill = state.Working ? Frozen(0x2E, 0xB8, 0x5C) : Frozen(0xE5, 0x48, 0x2E);
        light.Stroke = (Brush)resources.FindResource("InkBrush");
        light.StrokeThickness = 1.2;
        light.ToolTip = state.Working ? $"Claude is working ({state.Steps} steps)" : "The last turn failed or was stopped";
        if (state.Working)
        {
            light.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The work of a turn - tool steps and the notes Claude wrote on the way - folded into one line
/// ("⚙ 3 steps · 2 notes · last: ...") that opens to list it all in order.
/// </summary>
internal sealed class StepGroup
{
    private readonly StackPanel _items = new() { Margin = new Thickness(20, 4, 0, 2) };
    private readonly TextBlock _header;
    private readonly Brush _accent, _soft;
    private readonly FontFamily _mono;
    private readonly FrameworkElement _resources;
    private readonly List<(string Text, UIElement Element)> _notes = [];
    private int _steps;
    private string _last = "";

    public BlockUIContainer Block { get; }

    public StepGroup(Brush accent, Brush soft, FontFamily mono, FrameworkElement resources)
    {
        _accent = accent;
        _soft = soft;
        _mono = mono;
        _resources = resources;
        _header = new TextBlock { FontFamily = mono, FontSize = 12, Foreground = soft, TextTrimming = TextTrimming.CharacterEllipsis };
        var expander = new Expander { Header = _header, Content = _items, IsExpanded = false };
        Block = new BlockUIContainer(expander) { Margin = new Thickness(0, 2, 0, 8) };
    }

    public void AddStep(string step)
    {
        int space = step.IndexOf("  ", StringComparison.Ordinal);
        string tool = space > 0 ? step[..space] : step;
        string detail = space > 0 ? step[(space + 2)..] : "";
        var line = new TextBlock { FontFamily = _mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        line.Inlines.Add(new Run(tool) { FontWeight = FontWeights.Bold, Foreground = _accent });
        if (detail.Length > 0) line.Inlines.Add(new Run("  " + detail) { Foreground = _soft });
        _items.Children.Add(line);
        _steps++;
        _last = tool + (detail.Length > 0 ? " " + detail : "");
        Update();
    }

    // a note Claude wrote while working: shown in the fold, rendered like the answer but quieter
    public void AddNote(string text)
    {
        var viewer = new FlowDocumentScrollViewer
        {
            Document = CodeRenderer.Render(text), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false, Opacity = 0.75,
            FontSize = 13, Margin = new Thickness(0, 2, 0, 4), Focusable = false,
        };
        viewer.Document.FontSize = 13;
        viewer.Document.Foreground = (Brush)_resources.FindResource("InkBrush");
        var element = new Border
        {
            BorderBrush = (Brush)_resources.FindResource("LineBrush"), BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 3, 0, 3), Child = viewer,
        };
        _items.Children.Add(element);
        _notes.Add((text, element));
        Update();
    }

    // the answer was also streamed as the last note: keep it once, as the answer
    public void DropNote(string text)
    {
        int i = _notes.FindLastIndex(n => n.Text.Trim() == text.Trim());
        if (i < 0) return;
        _items.Children.Remove(_notes[i].Element);
        _notes.RemoveAt(i);
        Update();
    }

    private void Update()
    {
        _header.Inlines.Clear();
        var parts = new List<string>();
        if (_steps > 0) parts.Add($"{_steps} step{(_steps == 1 ? "" : "s")}");
        if (_notes.Count > 0) parts.Add($"{_notes.Count} note{(_notes.Count == 1 ? "" : "s")}");
        _header.Inlines.Add(new Run("⚙ " + (parts.Count > 0 ? string.Join(" · ", parts) : "working")) { FontWeight = FontWeights.Bold, Foreground = _accent });
        if (_steps > 0 && _last.Length > 0) _header.Inlines.Add(new Run($"  ·  last: {_last}"));
        Block.Child.Visibility = _steps == 0 && _notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
