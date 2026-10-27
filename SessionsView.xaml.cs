using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DevBreak;

/// <summary>
/// The Sessions view: the user's own Claude Code sessions - start one in a folder or continue one - worked in here
/// like in their CLI. One pane or several (+ Pane: side by side up to 3, then a grid - 4 as 2×2, up to 9); a session
/// picked in the list opens in the active pane.
/// Every session keeps its transcript and keeps running whichever pane shows it, and its light in the list shows
/// whether Claude is working on it (green) or its last turn failed (red).
/// </summary>
public partial class SessionsView : UserControl
{
    private readonly Dictionary<string, SessionState> _sessions = [];
    private readonly Dictionary<string, Ellipse> _lights = [];
    private IReadOnlyList<SessionInfo> _listed = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _titles = [];
    private readonly HashSet<string> _untitled = [];
    private bool _writingTitles;
    private readonly List<SessionPane> _panes = [];
    private SessionPane _active;
    private bool _started;

    // more than this and a pane is too small to read
    private const int MaxPanes = 9;

    public SessionsView()
    {
        InitializeComponent();
        _active = NewPane();
        Layout();
        Activate(_active);
        // a session's process closes after 10 minutes idle (the user's choice: the session may go on in a terminal);
        // all of them close with the app
        var idle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        idle.Tick += (_, _) =>
        {
            foreach (SessionState state in _sessions.Values)
            {
                if (state.Process is { } process && !state.Working && DateTime.Now - process.LastActivity > IdleClose)
                {
                    state.Process = null;
                    process.Dispose();
                }
            }
        };
        idle.Start();
        Application.Current.Exit += (_, _) =>
        {
            foreach (SessionState state in _sessions.Values) state.Process?.Dispose();
        };
    }

    private static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(10);

    /// <summary>Lists the sessions the first time the view is shown.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        RefreshList();
    }

    private void Activate(SessionPane pane)
    {
        _active = pane;
        foreach (SessionPane p in _panes) p.SetActive(p == pane, _panes.Count > 1);
        // the list follows the active pane
        SessionList.SelectionChanged -= SessionList_SelectionChanged;
        SessionList.SelectedItem = RowOf(pane.State?.Info.Id);
        SessionList.SelectionChanged += SessionList_SelectionChanged;
    }

    private SessionPane NewPane()
    {
        var pane = new SessionPane();
        pane.Activated += Activate;
        pane.CloseRequested += Close;
        pane.SessionSaved += RefreshList;
        pane.NewSessionRequested += StartSession;
        pane.Renamed += state =>
        {
            if (_titles.TryGetValue(state.Info.Id, out TextBlock? title)) title.Text = state.Info.Title;
        };
        _panes.Add(pane);
        Panes.Children.Add(pane);
        return pane;
    }

    private void AddPane_Click(object sender, RoutedEventArgs e)
    {
        if (_panes.Count >= MaxPanes) return;
        SessionPane pane = NewPane();
        pane.Clear();
        Layout();
        Activate(pane);
        // more panes need the room: the window grows to the work area's width, and its height once they are in rows
        if (Window.GetWindow(this) is Window window)
        {
            Rect area = SystemParameters.WorkArea;
            window.Width = Math.Max(window.Width, area.Width - 32);
            window.Left = area.Left + 16;
            if (Panes.RowDefinitions.Count > 1)
            {
                window.Height = Math.Max(window.Height, area.Height - 32);
                window.Top = area.Top + 16;
            }
        }
    }

    // a pane closes; its session keeps running and stays in the list
    private void Close(SessionPane pane)
    {
        if (_panes.Count < 2) return;
        pane.Clear();
        _panes.Remove(pane);
        Panes.Children.Remove(pane);
        Layout();
        Activate(_active == pane ? _panes[0] : _active);
    }

    // side by side up to 3; then rows of 2 for 4 (2×2), rows of 3 for 5 to 9
    private void Layout()
    {
        int n = _panes.Count;
        int columns = n <= 3 ? n : n == 4 ? 2 : 3;
        int rows = (n + columns - 1) / columns;
        Panes.ColumnDefinitions.Clear();
        Panes.RowDefinitions.Clear();
        for (int c = 0; c < columns; c++) Panes.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < rows; r++) Panes.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < n; i++)
        {
            int row = i / columns, column = i % columns;
            Grid.SetRow(_panes[i], row);
            Grid.SetColumn(_panes[i], column);
            _panes[i].Margin = new Thickness(column > 0 ? 14 : 0, row > 0 ? 14 : 0, 0, 0);
        }
        AddPaneButton.IsEnabled = n < MaxPanes;
    }

    private async void RefreshList()
    {
        RefreshListButton.IsEnabled = false;
        IReadOnlyList<SessionInfo> listed = await Task.Run(ClaudeSessions.List);
        // a session Claude Code did not title shows the title Claude wrote for it here, if it has one
        _listed = listed.Select(s => !s.HasAiTitle && SessionTitles.Get(s.Id) is { } written ? s with { Title = written } : s).ToList();
        // a fresh title for an open session (a new session gets its title from its first turn)
        foreach (SessionInfo session in _listed)
            if (_sessions.TryGetValue(session.Id, out SessionState? state)) state.Info = session;
        FillList();
        RefreshListButton.IsEnabled = true;
        WriteTitles();
    }

    // Claude writes a title for each listed session that has neither an ai-title nor one written before - one at a
    // time, in the background; each shows in its row (and its pane) as it comes
    private async void WriteTitles()
    {
        if (_writingTitles) return;
        _writingTitles = true;
        try
        {
            while (_listed.FirstOrDefault(s => !s.HasAiTitle && SessionTitles.Get(s.Id) == null && !_untitled.Contains(s.Id)) is { } session)
            {
                string? title = null;
                try
                {
                    title = await SessionTitles.WriteAsync(session.Id);
                }
                catch (Exception)
                {
                    // claude failed: this session keeps its title for this run
                }
                if (title == null)
                {
                    _untitled.Add(session.Id);
                    continue;
                }
                _listed = _listed.Select(s => s.Id == session.Id ? s with { Title = title } : s).ToList();
                if (_titles.TryGetValue(session.Id, out TextBlock? text)) text.Text = title;
                if (_sessions.TryGetValue(session.Id, out SessionState? state))
                {
                    state.Info = state.Info with { Title = title };
                    state.Notify();
                }
            }
        }
        finally
        {
            _writingTitles = false;
        }
    }

    private void Group_Changed(object sender, RoutedEventArgs e) => FillList();

    // the rows of the last listing: newest first, or under a header per folder (the folder with the newest session first)
    private void FillList()
    {
        SessionList.SelectionChanged -= SessionList_SelectionChanged;
        SessionList.Items.Clear();
        _lights.Clear();
        _titles.Clear();
        // sessions started here that the list does not show (no file yet, or a folder it skips) stay at the top
        foreach (SessionState state in _sessions.Values.Where(s => s.StartedHere && _listed.All(i => i.Id != s.Info.Id))) SessionList.Items.Add(Row(state.Info));
        if (GroupButton.IsChecked == true)
        {
            foreach (IGrouping<string, SessionInfo> folder in _listed.GroupBy(s => s.Folder.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase))
            {
                bool collapsed = _collapsed.Contains(folder.Key);
                SessionList.Items.Add(FolderHeader(folder.Key, folder.Count(), collapsed));
                if (collapsed) continue;
                foreach (SessionInfo session in folder) SessionList.Items.Add(Row(session, inFolder: true));
            }
        }
        else
        {
            foreach (SessionInfo session in _listed) SessionList.Items.Add(Row(session));
        }
        string? id = _active.State?.Info.Id;
        SessionList.SelectedItem = RowOf(id);
        SessionList.SelectionChanged += SessionList_SelectionChanged;
    }

    private ListBoxItem? RowOf(string? id) =>
        SessionList.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag is SessionInfo s && s.Id == id);

    // a folder's name and how many sessions it has; not selectable - a click folds or unfolds its sessions
    private ListBoxItem FolderHeader(string folder, int count, bool collapsed)
    {
        var text = new TextBlock { Margin = new Thickness(2, 12, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run(collapsed ? "▸ " : "▾ ") { FontSize = 12, Foreground = (Brush)FindResource("VioletBrush") });
        text.Inlines.Add(new Run(System.IO.Path.GetFileName(folder)) { FontWeight = FontWeights.Bold, FontSize = 13 });
        text.Inlines.Add(new Run($"  ·  {count}") { FontSize = 11.5, Foreground = (Brush)FindResource("InkSoftBrush") });
        var header = new ListBoxItem
        {
            Content = text, ToolTip = folder + (collapsed ? "\nClick to show its sessions" : "\nClick to fold its sessions"),
            Focusable = false, Cursor = System.Windows.Input.Cursors.Hand,
        };
        header.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (!_collapsed.Remove(folder)) _collapsed.Add(folder);
            FillList();
        };
        return header;
    }

    private ListBoxItem Row(SessionInfo session, bool inFolder = false)
    {
        var light = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 5, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        _lights[session.Id] = light;
        Lights.Set(light, _sessions.GetValueOrDefault(session.Id), this);
        var text = new StackPanel();
        var title = new TextBlock { Text = session.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 };
        _titles[session.Id] = title;
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            // under its folder's header the row needs only the time
            Text = inFolder ? Ago(session.LastActive) : $"{System.IO.Path.GetFileName(session.Folder.TrimEnd('\\'))} · {Ago(session.LastActive)}",
            FontSize = 11.5, Foreground = (Brush)FindResource("InkSoftBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var row = new DockPanel { Margin = new Thickness(inFolder ? 12 : 2, 6, 4, 6) };
        DockPanel.SetDock(light, Dock.Left);
        row.Children.Add(light);
        row.Children.Add(text);
        return new ListBoxItem { Content = row, Tag = session, ToolTip = session.Folder };
    }

    private static string Ago(DateTime when)
    {
        TimeSpan age = DateTime.Now - when;
        return age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago" : age.TotalDays < 7 ? $"{(int)age.TotalDays} d ago" : when.ToString("d MMM");
    }

    private void RefreshList_Click(object sender, RoutedEventArgs e) => RefreshList();

    private SessionState StateOf(SessionInfo info, bool isNew = false)
    {
        if (!_sessions.TryGetValue(info.Id, out SessionState? state))
        {
            state = new SessionState { Info = info, IsNew = isNew, StartedHere = isNew, Loaded = isNew };
            // the session's light in the list follows it
            state.Changed += s =>
            {
                if (_lights.TryGetValue(s.Info.Id, out Ellipse? light)) Lights.Set(light, s, this);
            };
            _sessions[info.Id] = state;
        }
        return state;
    }

    // /clear (a new session, in the pane) or Branch (a fork of the pane's session, in a new pane when there is room)
    private void StartSession(SessionPane from, SessionInfo info, string? forkFrom, bool newPane)
    {
        SessionState state = StateOf(info, isNew: forkFrom == null);
        if (forkFrom != null)
        {
            state.ForkFrom = forkFrom;
            state.StartedHere = true;
        }
        ListBoxItem row = Row(info);
        SessionList.Items.Insert(0, row);
        SessionPane target = from;
        if (newPane && _panes.Count < MaxPanes)
        {
            target = NewPane();
            target.Clear();
            Layout();
        }
        target.Show(state);
        Activate(target);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Start a Claude Code session in this folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string folder = dialog.FolderName;
        var info = new SessionInfo(Guid.NewGuid().ToString(), folder, "New session in " + System.IO.Path.GetFileName(folder.TrimEnd('\\')), DateTime.Now);
        StateOf(info, isNew: true);
        ListBoxItem row = Row(info);
        SessionList.Items.Insert(0, row);
        SessionList.SelectedItem = row;
    }

    /// <summary>Shows a session in the active pane (the factory opens its runs' sessions here).</summary>
    public void Open(SessionInfo info)
    {
        Start();
        SessionState state = StateOf(info);
        if (state.ShownIn != null) Activate(state.ShownIn);
        else _active.Show(state);
        RefreshList();
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not ListBoxItem { Tag: SessionInfo info }) return;
        SessionState state = StateOf(info);
        // already on screen in a pane: that pane becomes the active one; otherwise it opens in the active pane
        if (state.ShownIn != null) Activate(state.ShownIn);
        else _active.Show(state);
    }
}
