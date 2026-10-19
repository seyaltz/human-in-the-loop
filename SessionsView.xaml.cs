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
            Transcript.ScrollToEnd();
        }
    }

    private void Show(Open open)
    {
        Loading.Visibility = Visibility.Collapsed;
        SessionTitle.Text = open.Info.Title;
        SessionFolder.Text = open.Info.Folder + "   ·   session " + open.Info.Id;
        Transcript.Document = open.Document;
        Transcript.ScrollToEnd();
        CopyResumeButton.IsEnabled = OpenFolderButton.IsEnabled = true;
        UpdateRunning();
        InputBox.Focus();
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

    private void Add(Open open, SessionEntry entry, bool scroll)
    {
        FlowDocument document = open.Document;
        switch (entry.Kind)
        {
            case SessionEntryKind.You:
            case SessionEntryKind.Claude:
                bool mine = entry.Kind == SessionEntryKind.You;
                document.Blocks.Add(new Paragraph(new Run(mine ? "You" : "Claude") { FontWeight = FontWeights.Bold })
                {
                    Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 12, 0, 2),
                    Foreground = (Brush)FindResource(mine ? "VioletBrush" : "CoralInkBrush"),
                });
                CodeRenderer.Append(document, entry.Text);
                break;
            case SessionEntryKind.Tool:
                int space = entry.Text.IndexOf("  ", StringComparison.Ordinal);
                var step = new Paragraph { Margin = new Thickness(0, 0, 0, 3), FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12 };
                step.Inlines.Add(new Run("⚙ " + (space > 0 ? entry.Text[..space] : entry.Text)) { FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("VioletBrush") });
                if (space > 0) step.Inlines.Add(new Run("  " + entry.Text[(space + 2)..]) { Foreground = (Brush)FindResource("InkSoftBrush") });
                document.Blocks.Add(step);
                break;
            case SessionEntryKind.Done:
                document.Blocks.Add(new Paragraph(new Run("✓ done · " + entry.Text))
                {
                    FontSize = 11.5, Foreground = (Brush)FindResource("InkSoftBrush"), Margin = new Thickness(0, 4, 0, 4),
                });
                break;
            case SessionEntryKind.Error:
                document.Blocks.Add(new Paragraph(new Run(entry.Text))
                {
                    Foreground = (Brush)FindResource("CoralInkBrush"), Margin = new Thickness(0, 4, 0, 4),
                });
                break;
        }
        if (scroll) Transcript.ScrollToEnd();
    }
}
