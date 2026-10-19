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

namespace DevBreak;

/// <summary>
/// The Sessions view: the user's own Claude Code sessions - start one in a folder or continue one - worked in here
/// like in their CLI. Each session keeps its transcript and keeps running while another one is on screen.
/// </summary>
public partial class SessionsView : UserControl
{
    private sealed class Open
    {
        public required SessionInfo Info;
        public bool IsNew;
        public bool StartedHere;
        public bool Loaded;
        public FlowDocument Document = CodeRenderer.Render("");
        public CancellationTokenSource? Running;
        public int Steps;
        // where this turn's reply starts, and the tool steps being folded together
        public Block? ReplyStart;
        public StepGroup? Group;
    }

    private readonly Dictionary<string, Open> _open = [];
    private Open? _current;
    private bool _started;

    public SessionsView()
    {
        InitializeComponent();
    }

    /// <summary>Lists the sessions the first time the view is shown.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        RefreshList();
    }

    private async void RefreshList()
    {
        RefreshListButton.IsEnabled = false;
        string? selected = _current?.Info.Id;
        IReadOnlyList<SessionInfo> sessions = await Task.Run(ClaudeSessions.List);
        SessionList.SelectionChanged -= SessionList_SelectionChanged;
        SessionList.Items.Clear();
        // sessions started here that the list does not show (no file yet, or a folder it skips) stay at the top
        foreach (Open open in _open.Values.Where(o => o.StartedHere && sessions.All(s => s.Id != o.Info.Id))) SessionList.Items.Add(Row(open.Info));
        foreach (SessionInfo session in sessions) SessionList.Items.Add(Row(session));
        SessionList.SelectedItem = SessionList.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((SessionInfo)i.Tag).Id == selected);
        SessionList.SelectionChanged += SessionList_SelectionChanged;
        RefreshListButton.IsEnabled = true;
    }

    private ListBoxItem Row(SessionInfo session)
    {
        var text = new StackPanel { Margin = new Thickness(2, 6, 4, 6) };
        text.Children.Add(new TextBlock { Text = session.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 });
        text.Children.Add(new TextBlock
        {
            Text = $"{System.IO.Path.GetFileName(session.Folder.TrimEnd('\\'))} · {Ago(session.LastActive)}",
            FontSize = 11.5, Foreground = (Brush)FindResource("InkSoftBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return new ListBoxItem { Content = text, Tag = session, ToolTip = session.Folder };
    }

    private static string Ago(DateTime when)
    {
        TimeSpan age = DateTime.Now - when;
        return age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago" : age.TotalDays < 7 ? $"{(int)age.TotalDays} d ago" : when.ToString("d MMM");
    }

    private void RefreshList_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Start a Claude Code session in this folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string folder = dialog.FolderName;
        var info = new SessionInfo(Guid.NewGuid().ToString(), folder, "New session in " + System.IO.Path.GetFileName(folder.TrimEnd('\\')), DateTime.Now);
        var open = new Open { Info = info, IsNew = true, StartedHere = true, Loaded = true };
        _open[info.Id] = open;
        var row = Row(info);
        SessionList.Items.Insert(0, row);
        SessionList.SelectedItem = row;
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not ListBoxItem { Tag: SessionInfo info }) return;
        if (!_open.TryGetValue(info.Id, out Open? open))
        {
            open = new Open { Info = info };
            _open[info.Id] = open;
        }
        _current = open;
        Show(open);
        if (open.Loaded) return;
        Loading.Visibility = Visibility.Visible;
        IReadOnlyList<SessionEntry> entries = await Task.Run(() => ClaudeSessions.Transcript(info.Id));
        open.Loaded = true;
        foreach (SessionEntry entry in entries) Add(open, entry, scroll: false);
        if (_current == open)
        {
            Loading.Visibility = Visibility.Collapsed;
            ShowLatest(open);
        }
    }

    private void Show(Open open)
    {
        Loading.Visibility = Visibility.Collapsed;
        SessionTitle.Text = open.Info.Title;
        SessionFolder.Text = open.Info.Folder + "   ·   session " + open.Info.Id;
        Transcript.Document = open.Document;
        ShowLatest(open);
        CopyResumeButton.IsEnabled = OpenFolderButton.IsEnabled = true;
        UpdateRunning();
        InputBox.Focus();
    }

    // a session opens at the start of its latest reply (or the end, before any reply)
    private void ShowLatest(Open open)
    {
        if (open.ReplyStart != null) Turns.ScrollToStart(Transcript, open.ReplyStart);
        else Transcript.ScrollToEnd();
    }

    private void UpdateRunning()
    {
        bool running = _current?.Running != null;
        InputBox.IsEnabled = _current != null;
        SendButton.IsEnabled = _current != null && !running;
        StopButton.IsEnabled = running;
        WorkingText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running) WorkingText.Text = $"Claude is working... ({_current!.Steps} step{(_current.Steps == 1 ? "" : "s")})";
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private async void Send()
    {
        Open? open = _current;
        string text = InputBox.Text.Trim();
        if (open == null || open.Running != null || text.Length == 0) return;
        InputBox.Clear();
        Add(open, new SessionEntry(SessionEntryKind.You, text), scroll: true);
        open.Running = new CancellationTokenSource();
        open.Steps = 0;
        UpdateRunning();
        bool wasNew = open.IsNew;
        try
        {
            await ClaudeSessions.RunTurnAsync(open.Info.Folder, open.Info.Id, open.IsNew, text, entry =>
                Dispatcher.Invoke(() =>
                {
                    if (entry.Kind == SessionEntryKind.Tool) open.Steps++;
                    Add(open, entry, scroll: _current == open);
                    if (_current == open) UpdateRunning();
                }), open.Running.Token);
            open.IsNew = false;
        }
        catch (Exception ex)
        {
            Add(open, new SessionEntry(SessionEntryKind.Error, "Could not run the claude CLI: " + ex.Message), scroll: _current == open);
        }
        open.Running.Dispose();
        open.Running = null;
        if (_current == open) UpdateRunning();
        // the first turn gave the session its file and title
        if (wasNew && !open.IsNew) RefreshList();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _current?.Running?.Cancel();

    private void CopyResume_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        Clipboard.SetText(ClaudeSessions.ResumeCommand(_current.Info));
        WorkingText.Text = "Copied - paste it in a terminal to continue there.";
        WorkingText.Visibility = Visibility.Visible;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) Process.Start(new ProcessStartInfo(_current.Info.Folder) { UseShellExecute = true });
    }

    /// <summary>
    /// Adds a step of a session to its transcript, the way it reads best: the developer's message in its box; one
    /// "Claude" label per turn, its text under it; tool steps in a row folded into one line that opens; the done line.
    /// While a turn runs on screen, the view goes to the start of Claude's reply once and then stays, so it is read
    /// from the top while the steps keep counting.
    /// </summary>
    private void Add(Open open, SessionEntry entry, bool scroll)
    {
        FlowDocument document = open.Document;
        Brush Res(string key) => (Brush)FindResource(key);
        switch (entry.Kind)
        {
            case SessionEntryKind.You:
                Turns.Add(document, isMine: true, entry.Text, this);
                open.ReplyStart = null;
                open.Group = null;
                if (scroll) Transcript.ScrollToEnd();
                return;
            case SessionEntryKind.Note:
            case SessionEntryKind.Tool:
                // the work on the way - steps and notes - folds into one line per turn
                StartReply(open, scroll);
                if (open.Group == null)
                {
                    open.Group = new StepGroup(Res("VioletBrush"), Res("InkSoftBrush"), (FontFamily)FindResource("MonoFont"), this);
                    document.Blocks.Add(open.Group.Block);
                }
                if (entry.Kind == SessionEntryKind.Tool) open.Group.AddStep(entry.Text);
                else open.Group.AddNote(entry.Text);
                return;
            case SessionEntryKind.Answer:
                // only the answer is shown in full; the last streamed note is the same text, so it leaves the fold
                StartReply(open, scroll);
                open.Group?.DropNote(entry.Text);
                CodeRenderer.Append(document, entry.Text);
                open.Group = null;
                return;
            case SessionEntryKind.Done:
                open.Group = null;
                document.Blocks.Add(new Paragraph(new Run("✓ done · " + entry.Text))
                {
                    FontSize = 11.5, Foreground = Res("InkSoftBrush"), Margin = new Thickness(0, 2, 0, 8),
                });
                return;
            case SessionEntryKind.Error:
                open.Group = null;
                document.Blocks.Add(new Paragraph(new Run(entry.Text))
                {
                    Foreground = Res("CoralInkBrush"), Margin = new Thickness(0, 4, 0, 8),
                });
                return;
        }
    }

    // the "Claude" label, once per turn; on screen, the view goes to it the first time
    private void StartReply(Open open, bool scroll)
    {
        if (open.ReplyStart != null) return;
        open.ReplyStart = Turns.Add(open.Document, isMine: false, "", this);
        if (scroll) Turns.ScrollToStart(Transcript, open.ReplyStart);
    }

    /// <summary>
    /// The work of a turn - tool steps and the notes Claude wrote on the way - folded into one line
    /// ("⚙ 3 steps · 2 notes · last: ...") that opens to list it all in order.
    /// </summary>
    private sealed class StepGroup
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
}
