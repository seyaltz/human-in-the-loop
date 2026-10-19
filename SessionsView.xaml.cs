using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DevBreak;

/// <summary>
/// The Sessions view: the user's own Claude Code sessions - start one in a folder or continue one - worked in here
/// like in their CLI. One pane, or two side by side (Split); a session picked in the list opens in the active pane.
/// Every session keeps its transcript and keeps running whichever pane shows it, and its light in the list shows
/// whether Claude is working on it (green) or its last turn failed (red).
/// </summary>
public partial class SessionsView : UserControl
{
    private readonly Dictionary<string, SessionState> _sessions = [];
    private readonly Dictionary<string, Ellipse> _lights = [];
    private IReadOnlyList<SessionInfo> _listed = [];
    private SessionPane _active;
    private bool _split;
    private bool _started;

    public SessionsView()
    {
        InitializeComponent();
        _active = PaneA;
        foreach (SessionPane pane in new[] { PaneA, PaneB })
        {
            pane.Activated += Activate;
            pane.CloseRequested += Close;
            pane.SessionSaved += RefreshList;
        }
        Activate(PaneA);
    }

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
        PaneA.SetActive(pane == PaneA, _split);
        PaneB.SetActive(pane == PaneB, _split);
        // the list follows the active pane
        SessionList.SelectionChanged -= SessionList_SelectionChanged;
        SessionList.SelectedItem = RowOf(pane.State?.Info.Id);
        SessionList.SelectionChanged += SessionList_SelectionChanged;
    }

    private void Split_Changed(object sender, RoutedEventArgs e) => SetSplit(SplitButton.IsChecked == true);

    private void SetSplit(bool split)
    {
        if (_split == split) return;
        _split = split;
        SplitButton.IsChecked = split;
        SecondColumn.Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PaneB.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        if (!split)
        {
            // one pane again: it keeps the active pane's session
            if (_active == PaneB && PaneB.State != null)
            {
                SessionState state = PaneB.State;
                PaneB.Clear();
                PaneA.Show(state);
            }
            else
            {
                PaneB.Clear();
            }
            Activate(PaneA);
        }
        else
        {
            if (PaneB.State == null) PaneB.Clear();
            Activate(PaneB);
        }
        // a split needs the room: the window grows to the work area's width
        if (split && Window.GetWindow(this) is Window window)
        {
            Rect area = SystemParameters.WorkArea;
            window.Width = Math.Max(window.Width, area.Width - 32);
            window.Left = area.Left + 16;
        }
    }

    private void Close(SessionPane pane)
    {
        if (!_split) return;
        if (pane == PaneA && PaneB.State != null)
        {
            // closing the left pane: the right one's session moves over
            SessionState state = PaneB.State;
            PaneB.Clear();
            PaneA.Show(state);
        }
        _active = PaneA;
        SetSplit(false);
    }

    private async void RefreshList()
    {
        RefreshListButton.IsEnabled = false;
        _listed = await Task.Run(ClaudeSessions.List);
        // a fresh title for an open session (a new session gets its title from its first turn)
        foreach (SessionInfo session in _listed)
            if (_sessions.TryGetValue(session.Id, out SessionState? state)) state.Info = session;
        FillList();
        RefreshListButton.IsEnabled = true;
    }

    private void Group_Changed(object sender, RoutedEventArgs e) => FillList();

    // the rows of the last listing: newest first, or under a header per folder (the folder with the newest session first)
    private void FillList()
    {
        SessionList.SelectionChanged -= SessionList_SelectionChanged;
        SessionList.Items.Clear();
        _lights.Clear();
        // sessions started here that the list does not show (no file yet, or a folder it skips) stay at the top
        foreach (SessionState state in _sessions.Values.Where(s => s.StartedHere && _listed.All(i => i.Id != s.Info.Id))) SessionList.Items.Add(Row(state.Info));
        if (GroupButton.IsChecked == true)
        {
            foreach (IGrouping<string, SessionInfo> folder in _listed.GroupBy(s => s.Folder.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase))
            {
                SessionList.Items.Add(FolderHeader(folder.Key, folder.Count()));
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

    // a folder's name and how many sessions it has; not selectable
    private ListBoxItem FolderHeader(string folder, int count)
    {
        var text = new TextBlock { Margin = new Thickness(2, 12, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run(System.IO.Path.GetFileName(folder)) { FontWeight = FontWeights.Bold, FontSize = 13 });
        text.Inlines.Add(new Run($"  ·  {count}") { FontSize = 11.5, Foreground = (Brush)FindResource("InkSoftBrush") });
        return new ListBoxItem { Content = text, ToolTip = folder, Focusable = false, IsHitTestVisible = false };
    }

    private ListBoxItem Row(SessionInfo session, bool inFolder = false)
    {
        var light = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 5, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        _lights[session.Id] = light;
        Lights.Set(light, _sessions.GetValueOrDefault(session.Id), this);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = session.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 });
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

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not ListBoxItem { Tag: SessionInfo info }) return;
        SessionState state = StateOf(info);
        // already on screen in a pane: that pane becomes the active one; otherwise it opens in the active pane
        if (state.ShownIn != null && (_split || state.ShownIn == PaneA)) Activate(state.ShownIn);
        else _active.Show(state);
    }
}
