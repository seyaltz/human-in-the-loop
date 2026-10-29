using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DevBreak;

/// <summary>
/// The Factory view: on the left a new task, the floor and the inbox of tasks (needs you, working, done); on the
/// right the New task form, the floor, or the selected task - its production line, one card for what it needs now,
/// and its activity, evidence, trace and what it learned.
/// </summary>
public partial class FactoryView : UserControl
{
    private readonly Dictionary<string, (Ellipse Light, TextBlock Status)> _rows = [];
    // the inbox group each task was listed under: a task that moves to another group rebuilds the list
    private readonly Dictionary<string, Group> _listedIn = [];
    private FactoryRun? _shown;
    private int _shownLog = -1;
    private string? _shownLessons;
    private string? _shownTrace;
    private string? _shownEvidence;
    // the trace steps the developer opened stay open while the trace updates
    private readonly HashSet<int> _openSpans = [];
    private bool _started;
    // Request changes pressed: the card shows the kinds of change and the box for the next revision
    private bool _changing;

    private enum Group { NeedsYou, Working, Done }

    private enum Page { Form, Floor, Task }

    /// <summary>"Open in Sessions": the task's Claude Code session, to continue it there.</summary>
    public event Action<SessionInfo>? OpenSessionRequested;

    // the slow watchdog: every few seconds, a step or tool call that just passed a minute turns amber and is told
    private readonly System.Windows.Threading.DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(5) };
    private static readonly Brush Amber = Frozen(Color.FromRgb(0xE8, 0xA3, 0x3D));

    public FactoryView()
    {
        InitializeComponent();
        FloorHowText.Text = EmptyHowText.Text = FactoryLine.HowItWorks;
        Factory.Changed += run => Dispatcher.BeginInvoke(() => Update(run));
        // a linked task the factory started for another repo: it shows up in the inbox at once
        Factory.RunCreated += run => Dispatcher.BeginInvoke(() =>
        {
            if (_rows.ContainsKey(run.Id)) return;
            ShowList();
        });
        _watchdog.Tick += (_, _) =>
        {
            // the times on the line and the card move while a station works
            if (_shown is { Working: true } shown && RunPage.Visibility == Visibility.Visible) Show(shown);
            if (FloorArea.Visibility == Visibility.Visible) ShowFloor();
            foreach ((FactoryRun run, string text) in Factory.CheckSlow())
            {
                App.Alert("Dev break factory - slow step", $"{Short(run.Title, 60)}\n{text}");
                Update(run);
            }
        };
        _watchdog.Start();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Lists the tasks and the repositories the first time the view is shown.</summary>
    public async void Start()
    {
        if (_started) return;
        _started = true;
        ShowList();
        ShowPage(Factory.Runs.Count == 0 ? Page.Form : Page.Task);
        AutonomousBox.IsChecked = AppSettings.Get("factoryAutonomous") != "0";
        ReportBox.IsChecked = AppSettings.Get("factoryReport") == "1";
        if (AppSettings.Get("factoryDepartment") is "Troubleshooting") TroubleshootingDept.IsChecked = true;
        else if (AppSettings.Get("factoryDepartment") is "Research") ResearchDept.IsChecked = true;
        else if (AppSettings.Get("factoryDepartment") is "Development") DevelopmentDept.IsChecked = true;
        else if (AppSettings.Get("factoryDepartment") is "Management") ManagementDept.IsChecked = true;
        Department_Checked(this, new RoutedEventArgs());
        if (AppSettings.Get("factoryLane") is "Fast") FastLane.IsChecked = true;
        else if (AppSettings.Get("factoryLane") is "Curated") CuratedLane.IsChecked = true;
        else if (AppSettings.Get("factoryLane") is "Full") FullLane.IsChecked = true;
        Lane_Checked(this, new RoutedEventArgs());
        CuratedBox.Text = Factory.CuratedKeep;
        IReadOnlyList<string> repos = await Task.Run(ClaudeProjects.GitRepos);
        foreach (string repo in repos.OrderBy(r => r, StringComparer.OrdinalIgnoreCase)) RepoBox.Items.Add(repo);
        if (AppSettings.Get("factoryRepo") is { Length: > 0 } last) RepoBox.Text = last;
        await RepoChangedAsync();
    }

    // one page on the right: the New task form, the floor, or the selected task
    private void ShowPage(Page page)
    {
        FormArea.Visibility = page == Page.Form ? Visibility.Visible : Visibility.Collapsed;
        FloorArea.Visibility = page == Page.Floor ? Visibility.Visible : Visibility.Collapsed;
        RunPage.Visibility = page == Page.Task ? Visibility.Visible : Visibility.Collapsed;
        NewButton.Style = (Style)FindResource(page == Page.Form ? "SecondaryButton" : "PrimaryButton");
        if (page == Page.Floor) ShowFloor();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        RunList.SelectedItem = null;
        ShowPage(Page.Form);
        TaskBox.Focus();
    }

    private void Floor_Click(object sender, RoutedEventArgs e)
    {
        RunList.SelectedItem = null;
        ShowPage(Page.Floor);
    }

    // --- the form ---

    // null: Auto - the gate chooses when the task starts
    private FactoryDepartment? Department => AutoDept.IsChecked == true ? null
        : TroubleshootingDept.IsChecked == true ? FactoryDepartment.Troubleshooting
        : ResearchDept.IsChecked == true ? FactoryDepartment.Research
        : ManagementDept.IsChecked == true ? FactoryDepartment.Management : FactoryDepartment.Development;

    // troubleshooting and research change no code: no verify command, and they always end in a report
    private void Department_Checked(object sender, RoutedEventArgs e)
    {
        if (ReportBox == null || VerifyBox == null || DepartmentHint == null) return;
        bool dev = Department is null or FactoryDepartment.Development;
        VerifyBox.IsEnabled = dev;
        ReportBox.IsEnabled = dev;
        if (!dev) ReportBox.IsChecked = true;
        else ReportBox.IsChecked = AppSettings.Get("factoryReport") == "1";
        DepartmentHint.Text = Department switch
        {
            null => "A gate reads the task when it starts and chooses the department.",
            FactoryDepartment.Development => "Builds a change: plan, build, verify, review, prove, then a draft PR.",
            FactoryDepartment.Troubleshooting => "A customer's or a site's problem, for the call center: reproduce, compare with what works, root cause - no code; a report with a plain part for support.",
            FactoryDepartment.Research => "A question about the system: a checked answer and a report - no code.",
            _ => "A vision, an idea or a company-wide matter: a decision brief - options, value, effort, risk, a recommendation - no code.",
        };
        Source_Checked(sender, e);
    }

    private FactoryHarness Lane => FastLane.IsChecked == true ? FactoryHarness.Fast : CuratedLane.IsChecked == true ? FactoryHarness.Curated
        : FullLane.IsChecked == true ? FactoryHarness.Full : FactoryHarness.Auto;

    // what each harness level loads, with the cost measured on this machine for one call
    private void Lane_Checked(object sender, RoutedEventArgs e)
    {
        if (LaneHint == null) return;
        LaneHint.Text = Lane switch
        {
            FactoryHarness.Fast => "Fast lane: none of your plugins or MCP servers (your own settings.json guard hooks stay) - the factory's own guard and rules, the repo's CLAUDE.md, built-in tools. About 7 s per step to start instead of 19. If a step needs your tools, the task moves to Full by itself.",
            FactoryHarness.Curated => "Curated: your settings and connectors, with only the plugins listed under More options. About 8 s per step to start.",
            FactoryHarness.Full => "Full: everything you have - all plugins, hooks and MCP servers. About 19 s per step to start.",
            _ => "Auto: the gate reads the task and picks the fast lane (the code is enough) or full (data, cloud, browser, the issue tracker). The review always runs lean, and the draft PR step always runs on your full setup, so your own PR gate checks it.",
        };
    }

    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        if (TaskLabel == null) return;
        TaskLabel.Text = JiraSource.IsChecked == true ? "Jira issue key (e.g. ABC-123)"
            : SpecSource.IsChecked == true ? "The spec: a file path or a link"
            : Department switch
            {
                FactoryDepartment.Troubleshooting => "The case: what the customer or site has, where (site, URL, steps)",
                FactoryDepartment.Research => "The question",
                FactoryDepartment.Management => "The vision, idea or company-wide matter to think through",
                null => "The task, case or question - the gate chooses the department",
                _ => "What to build or fix",
            };
        BrowseSpecButton.Visibility = SpecSource.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TaskBox.Height = TypedSource.IsChecked == true ? 140 : 40;
    }

    private void BrowseSpec_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "The spec for this task" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) TaskBox.Text = dialog.FileName;
    }

    private async void BrowseRepo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "The repository for this task" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        RepoBox.Text = dialog.FolderName;
        await RepoChangedAsync();
    }

    private async void RepoBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepoBox.SelectedItem is string repo) RepoBox.Text = repo;
        await RepoChangedAsync();
    }

    private async void RepoBox_LostFocus(object sender, RoutedEventArgs e) => await RepoChangedAsync();

    private string _formRepo = "";

    // a repository picked: its current branch as the base, and the verify command used there last time; a folder
    // with repos in it: both empty - the task finds the repo and fills them
    private async Task RepoChangedAsync()
    {
        string repo = RepoBox.Text.Trim().TrimEnd('\\');
        if (repo == _formRepo) return;
        _formRepo = repo;
        if (!IsRepo(repo))
        {
            BaseBox.Text = VerifyBox.Text = "";
            return;
        }
        BaseBox.Text = await Factory.CurrentBranchAsync(repo);
        VerifyBox.Text = AppSettings.Get("factoryVerify:" + repo.ToLowerInvariant()) ?? "";
    }

    private static bool IsRepo(string folder) =>
        folder.Length > 0 && (Directory.Exists(System.IO.Path.Combine(folder, ".git")) || File.Exists(System.IO.Path.Combine(folder, ".git")));

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await RepoChangedAsync();
        string task = TaskBox.Text.Trim(), repo = RepoBox.Text.Trim().TrimEnd('\\'), baseBranch = BaseBox.Text.Trim();
        string? error = task.Length == 0 ? "Write the task first." : !Directory.Exists(repo) ? "Pick a folder that exists." : null;
        FormError.Text = error ?? "";
        FormError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        if (error != null) return;
        FactorySource source = JiraSource.IsChecked == true ? FactorySource.Jira : SpecSource.IsChecked == true ? FactorySource.Spec : FactorySource.Typed;
        AppSettings.Set("factoryRepo", repo);
        if (IsRepo(repo) && VerifyBox.Text.Trim().Length > 0) AppSettings.Set("factoryVerify:" + repo.ToLowerInvariant(), VerifyBox.Text.Trim());
        bool autonomous = AutonomousBox.IsChecked == true;
        AppSettings.Set("factoryAutonomous", autonomous ? "1" : "0");
        bool report = ReportBox.IsChecked == true;
        FactoryDepartment? department = Department;
        if (department is null or FactoryDepartment.Development) AppSettings.Set("factoryReport", report ? "1" : "0");
        AppSettings.Set("factoryDepartment", department?.ToString() ?? "Auto");
        FactoryHarness lane = Lane;
        AppSettings.Set("factoryLane", lane.ToString());
        if (CuratedBox.Text.Trim().Length > 0) Factory.CuratedKeep = CuratedBox.Text.Trim();
        FactoryRun run = Factory.Create(source, task, repo, baseBranch, department is null or FactoryDepartment.Development ? VerifyBox.Text : "", autonomous, report, department, lane);
        TaskBox.Clear();
        Select(run);
        await Factory.RunAsync(run);
    }

    // --- the inbox: needs you, working, done ---

    private static Group GroupOf(FactoryRun run) => run.Working ? Group.Working
        : run.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped && !run.CleanedUp ? Group.NeedsYou : Group.Done;

    // the list again, in its groups (newest first in each), keeping the task that was selected
    private void ShowList()
    {
        FactoryRun? selected = (RunList.SelectedItem as ListBoxItem)?.Tag as FactoryRun;
        RunList.Items.Clear();
        _rows.Clear();
        _listedIn.Clear();
        List<FactoryRun> runs = Factory.Runs.OrderByDescending(r => r.Created).ToList();
        foreach ((Group group, string name) in new[] { (Group.NeedsYou, "NEEDS YOU"), (Group.Working, "WORKING"), (Group.Done, "DONE") })
        {
            List<FactoryRun> mine = runs.Where(r => GroupOf(r) == group).ToList();
            if (mine.Count == 0) continue;
            RunList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = $"{name} ({mine.Count})", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource(group == Group.NeedsYou ? "CoralInkBrush" : "InkSoftBrush") },
                Focusable = false, IsHitTestVisible = false, Margin = new Thickness(0, RunList.Items.Count == 0 ? 0 : 10, 0, 0),
            });
            foreach (FactoryRun run in mine)
            {
                RunList.Items.Add(Row(run));
                _listedIn[run.Id] = group;
            }
        }
        if (runs.Count == 0)
        {
            RunList.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "No tasks yet.", Style = (Style)FindResource("Caption") }, Focusable = false, IsHitTestVisible = false });
        }
        if (selected != null && RunList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == selected) is { } again) RunList.SelectedItem = again;
        UpdateRunsSummary();
    }

    // the task picked: listed, selected and shown
    private void Select(FactoryRun run)
    {
        if (!_rows.ContainsKey(run.Id)) ShowList();
        if (RunList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == run) is { } item) RunList.SelectedItem = item;
        ShowPage(Page.Task);
        Show(run);
    }

    private ListBoxItem Row(FactoryRun run)
    {
        var light = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 5, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var status = new TextBlock { FontSize = 11.5, Foreground = (Brush)FindResource("InkSoftBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        _rows[run.Id] = (light, status);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = FactoryDigest.Headline(run.Result, run.Title), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 });
        text.Children.Add(status);
        var row = new DockPanel { Margin = new Thickness(2, 6, 4, 6) };
        DockPanel.SetDock(light, Dock.Left);
        row.Children.Add(light);
        row.Children.Add(text);
        var item = new ListBoxItem { Content = row, Tag = run, ToolTip = run.Repo.Length > 0 ? run.Repo : run.Folder };
        UpdateRow(run);
        return item;
    }

    private void UpdateRow(FactoryRun run)
    {
        if (!_rows.TryGetValue(run.Id, out var row)) return;
        SetLight(row.Light, run);
        row.Status.Text = $"{(run.Department != FactoryDepartment.Development ? run.Department + " · " : "")}{(run.Revision > 1 ? $"revision {run.Revision} · " : "")}{System.IO.Path.GetFileName(run.Repo.Length > 0 ? run.Repo : run.Folder)} · {StatusText(run)}";
        if (((row.Status.Parent as StackPanel)?.Children[0]) is TextBlock title) title.Text = FactoryDigest.Headline(run.Result, run.Title);
    }

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunList.SelectedItem is not ListBoxItem { Tag: FactoryRun run }) return;
        ShowPage(Page.Task);
        Show(run);
    }

    // the tasks that were working at the last update: one that stops needing the developer is told once
    private readonly HashSet<string> _working = [];

    private void Update(FactoryRun run)
    {
        if (run.Working) _working.Add(run.Id);
        else if (_working.Contains(run.Id) && run.Stage == FactoryStage.Done && run.Revision > 1 && !run.Accepted)
        {
            _working.Remove(run.Id);
            App.Alert("Dev break factory - revision ready", $"{Short(run.Title, 60)}\nRevision {run.Revision} is ready for your review.");
        }
        else if (_working.Remove(run.Id) && run.Stage == FactoryStage.NeedsYou)
        {
            // the manager could not get it going (or, not autonomous, the task asks as before)
            App.Alert("Dev break factory - needs you", $"{Short(run.Title, 60)}\n{Short(run.ManagerCase.Length > 0 ? run.ManagerCase : run.Note, 180)}");
        }
        if (!_listedIn.TryGetValue(run.Id, out Group listed) || listed != GroupOf(run)) ShowList();
        else UpdateRow(run);
        UpdateRunsSummary();
        if (run == _shown && RunPage.Visibility == Visibility.Visible) Show(run);
        if (FloorArea.Visibility == Visibility.Visible) ShowFloor();
    }

    // every department's line with the tasks standing on it now, and what the department managers are doing
    private void ShowFloor()
    {
        List<FactoryRun> runs = Factory.Runs;
        FloorLines.Children.Clear();
        (FactoryDepartment Department, string Name)[] departments =
        [
            (FactoryDepartment.Development, "Development - a change, from the task to a draft PR"),
            (FactoryDepartment.Troubleshooting, "Troubleshooting - a customer's or a site's problem, for the call center"),
            (FactoryDepartment.Research, "Research - a question about the system"),
            (FactoryDepartment.Management, "Management - a vision, an idea or a company-wide matter"),
        ];
        foreach ((FactoryDepartment department, string name) in departments)
        {
            List<FactoryRun> mine = runs.Where(r => r.Department == department).ToList();
            List<FactoryRun> onLine = mine.Where(r => r.Working || r.Stage == FactoryStage.NeedsYou && !r.CleanedUp).ToList();
            int done = mine.Count(r => !r.Working && r.Stage == FactoryStage.Done);
            string counts = $"   {onLine.Count(r => r.Working)} working · {onLine.Count(r => !r.Working)} need you · {done} done";
            FloorLines.Children.Add(FactoryLine.Floor(name + counts, FactoryLine.LineOf(department), onLine, k => (Brush)FindResource(k), Select));
        }
        int working = runs.Count(r => r.Working), waiting = runs.Count(r => !r.Working && r.Stage == FactoryStage.NeedsYou && !r.CleanedUp);
        FloorSummary.Text = $"{working} task{(working == 1 ? "" : "s")} on the lines, {waiting} need{(waiting == 1 ? "s" : "")} you, {runs.Count(r => r.PrUrl.Length > 0)} with a draft PR. " +
            "Hover a station for what it does, the check it runs and why; click a task to open it.";
        List<FactoryRun> human = runs.Where(r => !r.Working && r.Stage == FactoryStage.NeedsYou && r.ManagerCase.Length > 0 && !r.CleanedUp).ToList();
        int resumed = runs.Sum(r => r.ManagerResumes);
        FloorManager.Text = "Department managers: " + (human.Count == 0 ? "no task is stuck on something only you can do." : $"{human.Count} task{(human.Count == 1 ? " needs" : "s need")} you - " +
            string.Join("; ", human.Take(3).Select(r => Short(r.Title, 40)))) + $" They restarted stopped tasks {resumed} time{(resumed == 1 ? "" : "s")} so far.";
    }

    private void UpdateRunsSummary()
    {
        List<FactoryRun> runs = Factory.Runs;
        int working = runs.Count(r => r.Working), waiting = runs.Count(r => GroupOf(r) == Group.NeedsYou);
        RunsSummary.Text = runs.Count == 0 ? "" : $"{runs.Count} task{(runs.Count == 1 ? "" : "s")} · {working} working · {waiting} need{(waiting == 1 ? "s" : "")} you";
        CleanAllButton.IsEnabled = runs.Any(r => !r.Working && !r.CleanedUp);
    }

    // a passing line under the task's title: what a clean up freed, a result to know
    private void Notice(string text)
    {
        RunNoteText.Text = text;
        ExportRun.Text = "";
        ExportLink.Tag = null;
        RunNote.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- continue and clean up ---

    private Func<Task>? _confirmed;

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) await Factory.ContinueAsync(_shown, byDeveloper: true);
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is { Result.Length: > 0 } run) Clipboard.SetText(run.Result);
    }


    // a clean up shows what it deletes - and any work that would be lost - before it does
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } run) return;
        Factory.CleanupInfo info = await Factory.InspectAsync(run);
        var lose = new List<string>();
        if (info.Uncommitted > 0) lose.Add($"{info.Uncommitted} uncommitted change{(info.Uncommitted == 1 ? "" : "s")} in the worktree");
        if (info.HasWorktree && !info.Pushed) lose.Add("the branch was never pushed (its commits stay in the local branch)");
        string what = run.InCheckout
            ? $"Delete this task's evidence and exported trace{(info.Pushed ? ", and its local branch (it is on the remote)" : "")} - {Factory.Size(info.Bytes)}. "
            : $"Delete this task's worktree, evidence, exported trace and session files - {Factory.Size(info.Bytes)}. ";
        AskConfirm(what + "The record and its trace stay; your checkout, its memory and the remote branch and PR are not touched." +
            (lose.Count > 0 ? "\n⚠ This loses: " + string.Join("; ", lose) + "." : ""), lose.Count > 0, async () =>
            {
                string freed = await Factory.CleanUpAsync(run);
                Show(run);
                Notice(freed);
            });
    }

    private async void CleanAll_Click(object sender, RoutedEventArgs e)
    {
        List<FactoryRun> runs = Factory.Runs.Where(r => !r.Working && !r.CleanedUp).ToList();
        if (runs.Count == 0) return;
        long bytes = 0;
        var lose = new List<string>();
        foreach (FactoryRun run in runs)
        {
            Factory.CleanupInfo info = await Factory.InspectAsync(run);
            bytes += info.Bytes;
            if (info.Uncommitted > 0 || (info.HasWorktree && !info.Pushed)) lose.Add(run.Title);
        }
        if (_shown == null && Factory.Runs.FirstOrDefault(r => !r.Working) is { } first) Select(first);
        AskConfirm($"Clean up {runs.Count} task{(runs.Count == 1 ? "" : "s")}: worktrees, evidence, exported traces and session files - {Factory.Size(bytes)}. " +
            "The records stay; your checkouts, memory and remote branches and PRs are not touched." +
            (lose.Count > 0 ? $"\n⚠ Uncommitted or unpushed work is lost in: {string.Join("; ", lose)}." : ""), lose.Count > 0, async () =>
            {
                foreach (FactoryRun run in runs) await Factory.CleanUpAsync(run);
                if (_shown != null) Show(_shown);
                Notice($"Cleaned up {runs.Count} task{(runs.Count == 1 ? "" : "s")} - {Factory.Size(bytes)} freed.");
            });
    }

    // deleting one run: Claude first checks what is worth keeping; the developer ticks what to keep, then it all goes
    private FactoryRun? _deleting;
    private List<(CheckBox Box, Factory.KeepSuggestion Keep)> _keepBoxes = [];

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } run || run.Working) return;
        _deleting = run;
        ConfirmBar.Visibility = Visibility.Collapsed;
        DeletePanel.Visibility = Visibility.Visible;
        DeleteConfirmButton.IsEnabled = false;
        KeepList.Children.Clear();
        RemoteBox.Visibility = Visibility.Collapsed;
        DeleteText.Text = "Claude checks what is worth keeping from this task before it is deleted...";
        DeleteText.Foreground = (Brush)FindResource("InkBrush");
        List<Factory.KeepSuggestion> keep;
        Factory.CleanupInfo info;
        try
        {
            info = await Factory.InspectAsync(run);
            keep = await Factory.DiagnoseAsync(run);
        }
        catch (Exception ex)
        {
            DeleteText.Text = "Could not check the task: " + ex.Message;
            return;
        }
        if (_deleting != run) return;
        var lose = new List<string>();
        if (info.Uncommitted > 0) lose.Add($"{info.Uncommitted} uncommitted change{(info.Uncommitted == 1 ? "" : "s")}");
        int unpushed = await Factory.UnpushedCommitsAsync(run);
        if (unpushed > 0) lose.Add($"{unpushed} commit{(unpushed == 1 ? "" : "s")} on the local branch {run.Branch} that {(unpushed == 1 ? "is" : "are")} on no remote");
        DeleteText.Text = $"Delete \"{run.Title}\" for good: its record, trace, evidence, report{(info.HasWorktree ? ", worktree and its session files" : "")} " +
            $"and its local branch - {Factory.Size(info.Bytes)}. Your checkout and its files are not touched." +
            (keep.Count > 0 ? "\nBefore that, keep what is ticked:" : "\nThe check found nothing worth keeping.") +
            (lose.Count > 0 ? "\n⚠ This loses: " + string.Join("; ", lose) + "." : "");
        DeleteText.Foreground = (Brush)FindResource(lose.Count > 0 ? "CoralInkBrush" : "InkBrush");
        _keepBoxes = [];
        foreach (Factory.KeepSuggestion k in keep)
        {
            string where = k.Where switch { "repo" => "REPO MEMORY", "global" => "GLOBAL CLAUDE.md", _ => "INSIGHTS" };
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
            label.Inlines.Add(new Run(where + "  ") { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("VioletBrush") });
            label.Inlines.Add(new Run(k.Name) { FontWeight = FontWeights.SemiBold });
            label.Inlines.Add(new Run("  -  " + k.Description));
            var box = new CheckBox { Content = label, IsChecked = true, Margin = new Thickness(0, 3, 0, 3), ToolTip = k.Content };
            AutomationProperties.SetName(box, $"Keep {k.Where} {k.Name}");
            KeepList.Children.Add(box);
            _keepBoxes.Add((box, k));
        }
        // the remote branch and PR: offered, never ticked by default
        if (info.Pushed || run.PrUrl.Length > 0)
        {
            RemoteText.Text = "Also on the remote: " + (run.PrUrl.Length > 0 ? $"decline the PR {run.PrUrl} and " : "") + $"delete the branch {run.Branch}";
            RemoteBox.IsChecked = false;
            RemoteBox.Visibility = Visibility.Visible;
        }
        DeleteConfirmButton.Content = lose.Count > 0 ? "Keep the ticked and delete anyway" : "Keep the ticked and delete the task";
        DeleteConfirmButton.IsEnabled = true;
    }

    private async void DeleteConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_deleting is not { } run) return;
        DeleteConfirmButton.IsEnabled = false;
        DeleteText.Text = "Deleting...";
        var keep = _keepBoxes.Where(k => k.Box.IsChecked == true).Select(k => k.Keep).ToList();
        string result = await Factory.DeleteAsync(run, keep, RemoteBox.Visibility == Visibility.Visible && RemoteBox.IsChecked == true);
        _deleting = null;
        DeletePanel.Visibility = Visibility.Collapsed;
        if (Factory.Runs.Contains(run))
        {
            Notice(result);
            return;
        }
        // the task is gone: off the list, onto the next one
        _shown = null;
        RunList.SelectedItem = null;
        ShowList();
        if (Factory.Runs.OrderByDescending(r => r.Created).FirstOrDefault() is { } next)
        {
            Select(next);
            Notice(result);
        }
        else
        {
            RunArea.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private void DeleteCancel_Click(object sender, RoutedEventArgs e)
    {
        _deleting = null;
        DeletePanel.Visibility = Visibility.Collapsed;
    }

    private void AskConfirm(string text, bool loses, Func<Task> act)
    {
        ShowPage(Page.Task);
        ConfirmText.Text = text;
        ConfirmText.Foreground = (Brush)FindResource(loses ? "CoralInkBrush" : "InkBrush");
        ConfirmButton.Content = loses ? "Clean up anyway" : "Clean up";
        _confirmed = act;
        ConfirmBar.Visibility = Visibility.Visible;
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        Func<Task>? act = _confirmed;
        _confirmed = null;
        if (act != null) await act();
        UpdateRunsSummary();
    }

    private void CancelConfirm_Click(object sender, RoutedEventArgs e)
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        _confirmed = null;
    }

    // --- the selected task ---

    private void Show(FactoryRun run)
    {
        if (_shown != run)
        {
            _shown = run;
            _shownLog = -1;
            _shownLessons = null;
            _shownTrace = null;
            _shownEvidence = null;
            _openSpans.Clear();
            _changing = false;
            FeedbackBox.Clear();
            Notice("");
            if (_deleting != null) DeleteCancel_Click(this, new RoutedEventArgs());
        }
        string evidence = $"{run.EvidencePlan.Count}:" + string.Join(",", run.Evidence.Select(e => e.Kind + e.Result + e.File));
        if (evidence != _shownEvidence)
        {
            _shownEvidence = evidence;
            ShowEvidence(run);
        }
        string trace = $"{run.Trace.Count(s => s.Slow) + run.Trace.Sum(s => s.Tools.Count(t => t.Slow))}:{run.Trace.Count}:{run.Trace.LastOrDefault()?.Tools.Count}:{run.Trace.LastOrDefault()?.Ended}:{string.Concat(run.Trace.LastOrDefault()?.Tools.Select(t => t.Result[0]) ?? [])}";
        if (trace != _shownTrace)
        {
            _shownTrace = trace;
            ShowTrace(run);
        }
        EmptyText.Visibility = Visibility.Collapsed;
        RunArea.Visibility = Visibility.Visible;
        SetLight(Light, run);
        // the answer's own headline says what the task found; the words it was started with go under it
        string headline = FactoryDigest.Headline(run.Result, run.Title);
        RunTitle.Text = headline;
        RunMeta.Text = Meta(run) + (headline != run.Title ? "   ·   asked: " + Short(run.Title.Replace("\n", " "), 90) : "");
        ShowPipeline(run);
        ShowCard(run);
        Dispatcher.BeginInvoke(FitCard, System.Windows.Threading.DispatcherPriority.Loaded);
        // the menu: what is there to open, and what may be cleaned up or deleted now
        SessionItem.IsEnabled = !run.Working && Directory.Exists(run.Worktree);
        FolderItem.IsEnabled = Directory.Exists(run.Worktree);
        string proof = Factory.ProofDir(run);
        ProofItem.IsEnabled = Directory.Exists(proof) && Directory.EnumerateFiles(proof, "*", SearchOption.AllDirectories).Any();
        CleanItem.IsEnabled = !run.Working && !run.CleanedUp;
        DeleteItem.IsEnabled = !run.Working;
        if (run.Log.Count != _shownLog)
        {
            _shownLog = run.Log.Count;
            ShowLog(run);
        }
        string lessons = string.Join(",", run.Lessons.Select(l => l.Name + l.Status));
        if (lessons != _shownLessons)
        {
            _shownLessons = lessons;
            ShowLessons(run);
        }
    }

    // one line: department, where it works, the branch, the revision, how long it took and the tokens it used
    private static string Meta(FactoryRun run)
    {
        var parts = new List<string> { run.Department.ToString() };
        if (run.Repo.Length == 0) parts.Add(run.Folder + " - finding the repo");
        else
        {
            parts.Add(System.IO.Path.GetFileName(run.Repo) + (run.InCheckout ? " (your checkout)" : run.UseWorktree ? " (separate worktree)" : ""));
            if (run.Branch.Length > 0) parts.Add($"{run.Branch} from {run.BaseBranch}");
        }
        if (run.Revision > 1) parts.Add("revision " + run.Revision);
        parts.Add("harness: " + Factory.LaneName(run.Lane) + (run.Harness == FactoryHarness.Auto ? " (auto)" : ""));
        DateTime end = run.Working ? DateTime.Now : run.Log.Count > 0 ? run.Log[^1].At : run.Created;
        if (FactoryLine.Time(end - run.Created) is { Length: > 0 } took) parts.Add(took);
        long tokens = run.Trace.Sum(s => s.InputTokens + s.OutputTokens);
        if (tokens > 0) parts.Add(Tokens(tokens) + " tokens");
        return string.Join("   ·   ", parts);
    }

    // the one card for the task's state: what it does or needs, and the actions that make sense now - no others
    private void ShowCard(FactoryRun run)
    {
        bool working = run.Working;
        bool waiting = !working && run.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped && !run.CleanedUp;
        bool done = !working && run.Stage == FactoryStage.Done;
        // the task has a session to talk to once its first Claude turn ran (a NEEDS_YOU in the plan leaves no plan yet)
        bool canTalk = !working && !run.CleanedUp && run.Trace.Any(s => s.Kind == "turn") && Directory.Exists(run.Worktree);
        bool reviewable = done && !run.Accepted && run.Trace.Any(s => s.Kind == "turn");
        if (!done) _changing = false;

        string title, text;
        string border;
        if (working)
        {
            FactoryLine.Station? station = FactoryLine.LineOf(run).FirstOrDefault(s => s.Stages.Contains(run.Stage));
            FactorySpan? step = run.Trace.LastOrDefault(s => s.Ended == null);
            FactoryToolCall? tool = step?.Tools.LastOrDefault();
            title = $"Working · {station?.Name ?? StageName(run.Stage)}" + (step != null && FactoryLine.Time(DateTime.Now - step.Started) is { Length: > 0 } t ? $" · {t}" : "");
            text = tool != null ? $"{step!.Title}: {tool.Name} {Short(tool.Detail, 160)}" : Short(run.Note, 300);
            border = "VioletBrush";
        }
        else if (waiting)
        {
            title = run.Stage == FactoryStage.Stopped ? "Stopped - what you can do" : "Needs you - what to do";
            text = run.ManagerCase.Length > 0 ? run.ManagerCase : run.Note.Trim();
            border = "CoralInkBrush";
        }
        else if (done)
        {
            string what = run.PrUrl.Length > 0 ? "the draft PR is ready" : run.ReportFile.Length > 0 ? "the report is ready" : run.Result.Length > 0 ? "the answer is ready" : "done";
            title = run.Accepted ? $"Accepted - revision {run.Revision}" : reviewable ? $"Review revision {run.Revision} - {what}" : char.ToUpper(what[0]) + what[1..];
            text = run.Note.Trim();
            border = reviewable ? "VioletBrush" : "LineBrush";
        }
        else
        {
            title = run.CleanedUp ? "Cleaned up" : StatusText(run);
            text = run.Note.Trim();
            border = "LineBrush";
        }
        if (run.CleanedUp && !working) title += " · cleaned up - the record stays";
        // what to do stays in the card; the technical reason it stopped folds away under it
        (string action, string why) = FactoryDigest.SplitNote(text);
        if (working) (action, why) = (text, "");
        NextTitle.Text = title;
        NextTitle.Foreground = (Brush)FindResource(waiting ? "CoralInkBrush" : "InkBrush");
        NextWhen.Text = !working && run.Log.Count > 0 ? (waiting ? "stopped " : "") + FactoryDigest.Ago(run.Log[^1].At, DateTime.Now) : "";
        action = FactoryDigest.Clean(action);
        if (NextText.Text != action) NextText.Text = action;
        NextText.Visibility = action.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WhyBox.Visibility = why.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (WhyText.Text != why) WhyText.Text = why;
        NextCard.BorderBrush = (Brush)FindResource(border);
        NextCard.Background = (Brush)FindResource(waiting ? "CoralTintBrush" : working || reviewable ? "VioletTintBrush" : "CardBrush");

        // what the manager proposes to change in the factory, folded, until the developer puts it away
        bool fixes = !working && run.FactoryFixes.Count > 0 && !run.FixesSeen;
        FixesBox.Visibility = fixes ? Visibility.Visible : Visibility.Collapsed;
        FixesHeader.Text = $"Factory improvements proposed ({run.FactoryFixes.Count}) - for you to approve";
        string fixesText = string.Join("\n\n", run.FactoryFixes.Select((f, i) => $"{i + 1}. {f}"));
        if (fixes && FixesText.Text != fixesText) FixesText.Text = fixesText;

        // the questions its answer asks, to answer with one click
        List<FactoryQuestion> questions = !working && !run.CleanedUp && canTalk ? FactoryDigest.Questions(run.Result) : [];
        ShowQuestions(run, questions);

        // what it hands back: the findings, formatted, and what it could not verify folded away
        ResultBox.Visibility = run.Result.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_shownResult != run.Id + "|" + run.Result)
        {
            _shownResult = run.Id + "|" + run.Result;
            (string body, string notVerified) = FactoryDigest.Body(run.Result);
            ResultHeadline.Text = FactoryDigest.Headline(run.Result, "");
            ResultHeadline.Visibility = ResultHeadline.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultText.Document = CodeRenderer.Render(body);
            NotVerifiedBox.Visibility = notVerified.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            NotVerifiedHeader.Text = $"⚠ Not verified ({notVerified.Split('\n').Count(l => l.TrimStart().StartsWith('-') || l.TrimStart().StartsWith('*'))})";
            NotVerifiedText.Document = CodeRenderer.Render(notVerified);
        }

        // the decisions it took on its own, while it waits for the review
        ShowDecisions(run, reviewable);

        // your words to the task: a reply while it waits for you, the changes for the next revision once it is done
        bool reply = waiting && canTalk, change = done && canTalk && _changing;
        ReplyPanel.Visibility = reply || change ? Visibility.Visible : Visibility.Collapsed;
        KindPanel.Visibility = change ? Visibility.Visible : Visibility.Collapsed;
        FeedbackButton.Content = change ? "Send to the next revision" : "Send reply";
        FeedbackBox.ToolTip = change ? "What should change - the factory takes it round the line again as the next revision"
            : "Answer its question, or tell it what to do - it goes on from there";

        // the actions: one primary for the state, and only the others that apply
        Visibility On(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = On(working);
        TakeAnywayButton.Visibility = On(working && run.WaitingFor.Length > 0);
        SteerPanel.Visibility = On(working);
        if (!working) SteerSent.Visibility = Visibility.Collapsed;
        ContinueButton.Visibility = On(waiting);
        AnywayButton.Visibility = On(waiting && run.Department == FactoryDepartment.Development && run.Plan.Length > 0 && run.PrUrl.Length == 0);
        OpenPrButton.Visibility = On(!working && run.PrUrl.Length > 0);
        ReportButton.Visibility = On(!working && run.ReportFile.Length > 0 && File.Exists(run.ReportFile));
        AcceptButton.Visibility = On(reviewable && !_changing);
        AcceptButton.Content = questions.Count > 0 ? $"Accept - {questions.Count} question{(questions.Count > 1 ? "s" : "")} unanswered" : "Accept";
        ChangesButton.Visibility = On(done && canTalk && !_changing);
        CancelChangeButton.Visibility = On(change);
        // a diagnosis goes to Development as its fix; research findings as the change they proved (a PR to approve)
        bool research = run.Department == FactoryDepartment.Research;
        FixItButton.Visibility = On(done && (research || run.Department == FactoryDepartment.Troubleshooting) && run.Result.Length > 0);
        FixItButton.Content = research ? "Build it in Development" : "Fix it in Development";
        FixItButton.ToolTip = research
            ? "Start a Development task from these findings: it makes the change they proved on its own branch, in the repo the research changed, proves it and opens a draft PR"
            : "Start a Development task from this diagnosis: the root cause and the fix it named";
        // the first action shown is the primary one; a PR or report next to Accept steps back to secondary
        OpenPrButton.Style = ReportButton.Style = (Style)FindResource(reviewable ? "SecondaryButton" : "PrimaryButton");
        NextButtons.Visibility = NextButtons.Children.OfType<Button>().Any(b => b.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyFixes_Click(object sender, RoutedEventArgs e)
    {
        if (FixesText.Text.Length > 0) Clipboard.SetText(FixesText.Text);
    }

    private void FixesSeen_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } run) return;
        run.FixesSeen = true;
        Factory.Save();
        ShowCard(run);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreMenu.IsOpen = true;
    }

    // the task's production line: each station passed (with its time), working, stopped, or to come
    private void ShowPipeline(FactoryRun run)
    {
        LineHost.Content = FactoryLine.Build(run, k => (Brush)FindResource(k));
    }

    // the log as one markdown document, so plans, answers and verify output render like the chats
    private void ShowLog(FactoryRun run)
    {
        var text = new StringBuilder();
        foreach (FactoryStep step in run.Log)
        {
            text.Append($"**{step.At:HH:mm} · {StageName(step.Stage)}**{(step.Ok ? "" : " ✗")}\n\n{step.Text.Trim()}\n\n");
        }
        LogBox.Document = CodeRenderer.Render(text.ToString());
        LogBox.ScrollToEnd();
    }

    private void RunTab_Checked(object sender, RoutedEventArgs e)
    {
        if (LogBox == null || TraceArea == null || LessonsArea == null || EvidenceArea == null) return;
        LogBox.Visibility = LogTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TraceArea.Visibility = TraceTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EvidenceArea.Visibility = EvidenceTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LessonsArea.Visibility = LessonsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // each step a line - time, stage, what, how long, tokens, tools - that opens to its tool calls, prompt and answer
    private void ShowTrace(FactoryRun run)
    {
        TraceList.Children.Clear();
        TraceTab.Content = run.Trace.Count > 0 ? $"Trace ({run.Trace.Count})" : "Trace";
        Brush soft = (Brush)FindResource("InkSoftBrush"), coral = (Brush)FindResource("CoralInkBrush"), accent = (Brush)FindResource("VioletBrush");
        var mono = (FontFamily)FindResource("MonoFont");
        for (int i = 0; i < run.Trace.Count; i++)
        {
            FactorySpan span = run.Trace[i];
            int index = i;
            var header = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            header.Inlines.Add(new Run($"{span.Started:HH:mm:ss}  ") { FontFamily = mono, FontSize = 12, Foreground = soft });
            header.Inlines.Add(new Run(span.Title) { FontWeight = FontWeights.Bold, Foreground = span.Error.Length > 0 ? coral : span.Slow ? Amber : (Brush)FindResource("InkBrush") });
            if (span.Slow || span.Tools.Any(t => t.Slow)) header.Inlines.Add(new Run("  ⏱ slow") { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Amber });
            var stats = new List<string> { span.Kind, Factory.Duration(span) };
            if (span.InputTokens + span.OutputTokens > 0) stats.Add($"{Tokens(span.InputTokens)} in / {Tokens(span.OutputTokens)} out");
            if (span.Tools.Count > 0) stats.Add($"{span.Tools.Count} tool{(span.Tools.Count == 1 ? "" : "s")}");
            int denied = span.Tools.Count(t => t.Result == "denied"), failed = span.Tools.Count(t => t.Result == "error");
            if (denied > 0) stats.Add($"{denied} denied");
            if (failed > 0) stats.Add($"{failed} failed");
            header.Inlines.Add(new Run("   " + string.Join(" · ", stats)) { FontSize = 12, Foreground = soft });
            if (span.Error.Length > 0) header.Inlines.Add(new Run("   ✗ " + Short(span.Error, 90)) { FontSize = 12, Foreground = coral });

            var expander = new Expander { Header = header, IsExpanded = _openSpans.Contains(index), Margin = new Thickness(0, 0, 0, 4) };
            expander.Expanded += (_, _) =>
            {
                _openSpans.Add(index);
                if (expander.Content == null) expander.Content = SpanDetail(span, soft, coral, accent, mono);
            };
            expander.Collapsed += (_, _) => _openSpans.Remove(index);
            if (expander.IsExpanded) expander.Content = SpanDetail(span, soft, coral, accent, mono);
            TraceList.Children.Add(expander);
        }
        if (run.Trace.Count == 0) TraceList.Children.Add(new TextBlock { Text = "No steps yet.", Style = (Style)FindResource("Caption"), Margin = new Thickness(4) });
    }

    private UIElement SpanDetail(FactorySpan span, Brush soft, Brush coral, Brush accent, FontFamily mono)
    {
        var panel = new StackPanel { Margin = new Thickness(22, 4, 0, 8) };
        foreach (FactoryToolCall tool in span.Tools)
        {
            var line = new TextBlock { FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            line.Inlines.Add(new Run($"{tool.At:HH:mm:ss} ") { Foreground = soft });
            line.Inlines.Add(new Run(tool.Name) { FontWeight = FontWeights.Bold, Foreground = accent });
            if (tool.Detail.Length > 0) line.Inlines.Add(new Run("  " + tool.Detail) { Foreground = soft });
            line.Inlines.Add(new Run("  " + tool.Result) { FontWeight = FontWeights.SemiBold, Foreground = tool.Result is "ok" or "running" ? soft : coral });
            // how long it took, amber when it was slow
            if (tool.Ended is { } ended) line.Inlines.Add(new Run($"  {(ended - tool.At).TotalSeconds:0.#} s") { Foreground = tool.Slow ? Amber : soft, FontWeight = tool.Slow ? FontWeights.Bold : FontWeights.Normal });
            else if (tool.Slow) line.Inlines.Add(new Run("  running over a minute") { Foreground = Amber, FontWeight = FontWeights.Bold });
            panel.Children.Add(line);
        }
        panel.Children.Add(TextFold("Prompt", span.Prompt, mono));
        panel.Children.Add(TextFold(span.Kind == "verify" ? "Output" : "Answer", span.Answer, mono));
        return panel;
    }

    // a long text, folded: opens to a read-only box that can be selected and copied
    private Expander TextFold(string label, string text, FontFamily mono)
    {
        var fold = new Expander
        {
            Header = new TextBlock { Text = $"{label} · {text.Length:N0} characters", FontSize = 12, FontWeight = FontWeights.SemiBold },
            Margin = new Thickness(0, 4, 0, 0),
        };
        fold.Expanded += (_, _) => fold.Content ??= new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = mono, FontSize = 12, MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(1), Padding = new Thickness(8),
            Background = Brushes.Transparent,
        };
        return fold;
    }

    // the proof the plan chose and what was produced: a card per piece - kind, result, what it proves, its numbers -
    // with images shown, and Open for a video, a report or any other file
    private void ShowEvidence(FactoryRun run)
    {
        EvidenceList.Children.Clear();
        EvidenceTab.Content = run.Evidence.Count > 0 ? $"Evidence ({run.Evidence.Count})" : "Evidence";
        string dir = Factory.ProofDir(run);
        var planned = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(2, 0, 0, 8) };
        planned.Inlines.Add(new Run("Proof the plan chose: ") { FontWeight = FontWeights.SemiBold });
        planned.Inlines.Add(new Run(run.EvidencePlan.Count > 0 ? string.Join(", ", run.EvidencePlan) : "none yet"));
        List<string> missing = run.EvidencePlan.Where(k => !run.Evidence.Any(e => e.Kind == k)).ToList();
        if (run.Evidence.Count > 0 && missing.Count > 0)
        {
            planned.Inlines.Add(new Run("   ·   not produced: " + string.Join(", ", missing)) { Foreground = (Brush)FindResource("CoralInkBrush") });
        }
        EvidenceList.Children.Add(planned);
        if (run.Evidence.Count == 0)
        {
            EvidenceList.Children.Add(new TextBlock { Text = "No evidence yet - the Prove step writes it.", Style = (Style)FindResource("Caption"), Margin = new Thickness(4) });
            return;
        }
        foreach (FactoryEvidence e in run.Evidence)
        {
            string file = e.File.Length > 0 ? System.IO.Path.Combine(dir, e.File) : "";
            bool exists = file.Length > 0 && File.Exists(file);
            Brush result = (Brush)FindResource(e.Result == "pass" ? "InkBrush" : e.Result == "fail" ? "CoralInkBrush" : "InkSoftBrush");
            var head = new TextBlock { TextWrapping = TextWrapping.Wrap };
            head.Inlines.Add(new Run(e.Kind.ToUpperInvariant() + "  ") { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("VioletBrush") });
            head.Inlines.Add(new Run(e.Result switch { "pass" => "✓ pass", "fail" => "✗ fail", "skipped" => "– skipped", _ => e.Result }) { FontWeight = FontWeights.Bold, Foreground = result });
            head.Inlines.Add(new Run("   " + e.Proves) { FontSize = 13 });
            var body = new StackPanel();
            body.Children.Add(head);
            if (e.Detail.Length > 0) body.Children.Add(new TextBlock { Text = e.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("InkSoftBrush"), Margin = new Thickness(0, 2, 0, 0) });
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            if (exists && ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // the file stays free
                    bitmap.UriSource = new Uri(file);
                    bitmap.DecodePixelWidth = 640;
                    bitmap.EndInit();
                    var image = new Image { Source = bitmap, MaxHeight = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Open " + e.File };
                    image.MouseLeftButtonUp += (_, _) => OpenFile(file);
                    body.Children.Add(image);
                }
                catch (Exception)
                {
                    // not an image after all: the Open button below still works
                }
            }
            if (exists)
            {
                var open = new Button { Content = "Open " + e.File, Style = (Style)FindResource("SecondaryButton"), Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                open.Click += (_, _) => OpenFile(file);
                body.Children.Add(open);
            }
            else if (e.File.Length > 0)
            {
                body.Children.Add(new TextBlock { Text = $"{e.File} is not in the evidence folder", FontSize = 12, Foreground = (Brush)FindResource("CoralInkBrush"), Margin = new Thickness(0, 4, 0, 0) });
            }
            EvidenceList.Children.Add(new Border
            {
                BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = body,
            });
        }
    }

    private static string Tokens(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    private static string Short(string text, int max)
    {
        string line = text.Replace("\r", "").Split('\n')[0];
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_shown == null) return;
        try
        {
            string file = Factory.Export(_shown);
            Notice("Trace exported: ");
            ExportRun.Text = System.IO.Path.GetFileName(file);
            ExportLink.Tag = file;
            ExportLink.ToolTip = file + "\nClick to show it in its folder";
        }
        catch (Exception ex)
        {
            Notice("Could not export: " + ex.Message);
        }
    }

    private void ExportLink_Click(object sender, RoutedEventArgs e)
    {
        if (ExportLink.Tag is string file && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
    }

    // a reply while the task waits for you, or the changes for its next revision
    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        string text = _changing ? ChangeRequest(FeedbackBox.Text) : FeedbackBox.Text.Trim();
        if (_shown == null || text.Length == 0) return;
        bool decisionsChanged = _changing && _decisionRows.Any(r => r.Ok.IsChecked != true);
        string kind = !_changing ? "" : KindWrong.IsChecked == true ? "wrong behaviour" : KindMissing.IsChecked == true ? "missing"
            : KindDesign.IsChecked == true ? "change design" : KindBug.IsChecked == true ? "bug" : decisionsChanged ? "change design" : "";
        FeedbackBox.Clear();
        foreach (RadioButton chip in new[] { KindWrong, KindMissing, KindDesign, KindBug }) chip.IsChecked = false;
        _changing = false;
        await Factory.FeedbackAsync(_shown, text, kind);
    }

    // the room under the header and the line is shared: the card takes at most half and scrolls, the activity the rest
    // (a fixed 190 px for the activity left it ~100 px of a 1080p screen once the tabs took theirs)
    internal static double CardMaxHeight(double room) => Math.Max(180, Math.Round(room / 2));

    private void RunArea_SizeChanged(object sender, SizeChangedEventArgs e) => FitCard();

    private void FitCard()
    {
        if (!NextCard.IsVisible) return;
        double top = NextCard.TranslatePoint(new Point(0, 0), RunArea).Y;
        NextScroll.MaxHeight = CardMaxHeight(RunArea.ActualHeight - top);
    }

    // the result's text boxes sit inside the card's scroll: the wheel scrolls the card, not a box that has nothing to scroll
    private void CardWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        NextScroll.ScrollToVerticalOffset(NextScroll.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    // --- while it works: your words go into the step that runs now, or the next one; nothing stops ---

    private void Steer_Click(object sender, RoutedEventArgs e)
    {
        string text = SteerBox.Text.Trim();
        if (_shown == null || text.Length == 0) return;
        bool now = Factory.Steer(_shown, text);
        SteerBox.Clear();
        SteerSent.Text = (now ? "Sent - it takes it in at its next step: " : "Held - it goes in with its next step: ") + Short(text, 120);
        SteerSent.Visibility = Visibility.Visible;
    }

    private void SteerBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        e.Handled = true;
        Steer_Click(sender, e);
    }

    // --- the questions its answer asks: yes as suggested, no, or your own words - sent back as one message ---

    private string _shownResult = "", _questionsFor = "";
    private readonly List<(FactoryQuestion Question, Func<string?> Answer)> _questionRows = [];

    private void ShowQuestions(FactoryRun run, List<FactoryQuestion> questions)
    {
        QuestionsPanel.Visibility = questions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string key = questions.Count > 0 ? $"{run.Id}|{run.Revision}|{run.Result.GetHashCode()}" : "";
        if (key == _questionsFor) return;
        _questionsFor = key;
        QuestionList.Children.Clear();
        _questionRows.Clear();
        QuestionsLabel.Text = $"It asks you {questions.Count} question{(questions.Count > 1 ? "s" : "")}:";
        foreach (FactoryQuestion q in questions)
        {
            var row = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(new TextBlock { Text = $"{q.Number}.  {FactoryDigest.Clean(q.Ask)}", FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            if (q.Suggested.Length > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "Suggested: " + FactoryDigest.Clean(q.Suggested), Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 0, 0),
                });
            }
            var choices = new WrapPanel { Margin = new Thickness(20, 6, 0, 0) };
            string group = $"q{q.Number}-{key.GetHashCode()}";
            RadioButton Choice(string label) => new() { Content = label, GroupName = group, Style = (Style)FindResource("PillRadio"), Margin = new Thickness(0, 0, 6, 0) };
            RadioButton yes = Choice(q.Suggested.Length > 0 ? "Yes, as suggested" : "Yes"), no = Choice("No"), own = Choice("Answer…");
            AutomationProperties.SetName(yes, $"Question {q.Number}: yes");
            AutomationProperties.SetName(no, $"Question {q.Number}: no");
            AutomationProperties.SetName(own, $"Question {q.Number}: answer");
            var words = new TextBox
            {
                Style = (Style)FindResource("NotebookBox"), FontSize = 12.5, Margin = new Thickness(20, 6, 0, 0), Padding = new Thickness(8, 4, 8, 4),
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Visibility = Visibility.Collapsed, ToolTip = "Your answer, in your words",
            };
            AutomationProperties.SetName(words, $"Question {q.Number}: your answer");
            foreach (RadioButton choice in new[] { yes, no, own })
            {
                choices.Children.Add(choice);
                choice.Checked += (_, _) =>
                {
                    words.Visibility = own.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                    if (own.IsChecked == true) words.Focus();
                    CountAnswers();
                };
            }
            words.TextChanged += (_, _) => CountAnswers();
            row.Children.Add(choices);
            row.Children.Add(words);
            QuestionList.Children.Add(row);
            _questionRows.Add((q, () => yes.IsChecked == true ? FactoryDigest.Yes : no.IsChecked == true ? FactoryDigest.No
                : own.IsChecked == true && words.Text.Trim().Length > 0 ? words.Text.Trim() : null));
        }
        CountAnswers();
    }

    private void CountAnswers()
    {
        int answered = _questionRows.Count(r => r.Answer() != null);
        SendAnswersButton.IsEnabled = answered > 0;
        SendAnswersButton.Content = answered > 0 ? $"Send answers ({answered} of {_questionRows.Count})" : "Send answers";
    }

    private async void SendAnswers_Click(object sender, RoutedEventArgs e)
    {
        if (_shown == null) return;
        var answers = _questionRows.Where(r => r.Answer() != null).ToDictionary(r => r.Question.Number, r => r.Answer()!);
        if (answers.Count == 0) return;
        string text = FactoryDigest.Answers(_questionRows.Select(r => r.Question).ToList(), answers);
        _questionsFor = "";
        await Factory.FeedbackAsync(_shown, text);
    }

    // --- the review of finished work: its own decisions, then accept it or ask for changes ---

    private string _reviewFor = "";
    private readonly List<(CheckBox Ok, TextBox How, string Decision)> _decisionRows = [];

    private void ShowDecisions(FactoryRun run, bool show)
    {
        List<string> decisions = show ? DecisionItems(run.Decisions) : [];
        ReviewDecisionsLabel.Visibility = decisions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewDecisions.Visibility = decisions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string key = show ? $"{run.Id}|{run.Revision}|{run.Decisions.GetHashCode()}" : "";
        if (key == _reviewFor) return;
        _reviewFor = key;
        ReviewDecisions.Children.Clear();
        _decisionRows.Clear();
        foreach (string decision in decisions)
        {
            var ok = new CheckBox
            {
                IsChecked = true, Margin = new Thickness(0, 4, 0, 0),
                Content = new TextBlock { Text = Short(decision.Replace("\n", " "), 260), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
            };
            AutomationProperties.SetName(ok, "Decision ok: " + Short(decision, 60));
            var how = new TextBox
            {
                Style = (Style)FindResource("NotebookBox"), FontSize = 12.5, Margin = new Thickness(22, 2, 0, 2), Padding = new Thickness(8, 4, 8, 4),
                Visibility = Visibility.Collapsed, ToolTip = "How should it be instead?",
            };
            AutomationProperties.SetName(how, "How instead: " + Short(decision, 60));
            // unticking a decision is asking for a change: the card opens the box for the next revision
            ok.Unchecked += (_, _) =>
            {
                how.Visibility = Visibility.Visible;
                if (!_changing) Changes_Click(this, new RoutedEventArgs());
                how.Focus();
            };
            ok.Checked += (_, _) => how.Visibility = Visibility.Collapsed;
            ReviewDecisions.Children.Add(ok);
            ReviewDecisions.Children.Add(how);
            _decisionRows.Add((ok, how, decision));
        }
    }

    // the plan's numbered decisions ("1. Should...? Suggested: ...") as separate items
    private static List<string> DecisionItems(string decisions) =>
        System.Text.RegularExpressions.Regex.Split(decisions.Replace("\r", "").Trim(), @"(?m)^\s*(?=\d+\.\s)")
            .Select(d => d.Trim()).Where(d => d.Length > 0).ToList();

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } run || run.Working) return;
        await Factory.AcceptAsync(run);
    }

    // Request changes: the card shows the kinds of change and the box; the unticked decisions, with how they should
    // be, go in first
    private void Changes_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } run) return;
        _changing = true;
        ShowCard(run);
        FeedbackBox.Focus();
    }

    // Cancel on a change request: the decisions ticked again, the box empty, the card back to Accept / Request changes
    private void CancelChange_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } run) return;
        _changing = false;
        FeedbackBox.Clear();
        foreach (RadioButton chip in new[] { KindWrong, KindMissing, KindDesign, KindBug }) chip.IsChecked = false;
        foreach ((CheckBox ok, TextBox how, _) in _decisionRows)
        {
            ok.IsChecked = true;
            how.Clear();
        }
        ShowCard(run);
    }

    // what goes to the next revision: the unticked decisions (with how they should be), then the developer's words
    private string ChangeRequest(string words)
    {
        List<string> changed = _decisionRows.Where(r => r.Ok.IsChecked != true).Select(r =>
            $"- Your decision \"{Short(r.Decision.Replace("\n", " "), 140)}\" is not what I want" +
            (r.How.Text.Trim().Length > 0 ? ": " + r.How.Text.Trim() : ".")).ToList();
        return string.Join("\n", changed) + (changed.Count > 0 && words.Trim().Length > 0 ? "\n" : "") + words.Trim();
    }

    private void ShowLessons(FactoryRun run)
    {
        LessonList.Children.Clear();
        LessonsTab.Content = run.Lessons.Count > 0 ? $"Learned ({run.Lessons.Count})" : "Learned";
        if (run.Lessons.Count == 0)
        {
            LessonList.Children.Add(new TextBlock { Text = "Nothing learned yet - the task learns when it ends.", Style = (Style)FindResource("Caption"), Margin = new Thickness(4) });
        }
        foreach (FactoryLesson lesson in run.Lessons) LessonList.Children.Add(LessonCard(run, lesson));
    }

    private Border LessonCard(FactoryRun run, FactoryLesson lesson)
    {
        string kind = lesson.Kind switch { "memory" => "memory", "claude-md" => "rule", "hook" => "hook", _ => "skill" };
        string state = lesson.Status switch
        {
            "saved" => "saved to the repo's memory", "pending" => "waits for your OK", "applied" => "applied to " + lesson.File,
            "dismissed" => "dismissed", _ => "undone",
        };
        var head = new TextBlock { TextWrapping = TextWrapping.Wrap };
        head.Inlines.Add(new Run(kind.ToUpperInvariant() + "  ") { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("VioletBrush") });
        head.Inlines.Add(new Run(lesson.Name) { FontWeight = FontWeights.Bold, FontSize = 13.5 });
        head.Inlines.Add(new Run("  ·  " + state) { FontSize = 11.5, Foreground = (Brush)FindResource("InkSoftBrush") });
        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = lesson.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 4) });
        body.Children.Add(new TextBlock
        {
            Text = lesson.Content, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("InkSoftBrush"),
            FontFamily = lesson.Kind is "hook" ? (FontFamily)FindResource("MonoFont") : FontFamily, MaxHeight = 120,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        Button Act(string label, bool primary, Action act)
        {
            var button = new Button { Content = label, Style = (Style)FindResource(primary ? "PrimaryButton" : "SecondaryButton"), Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) =>
            {
                try
                {
                    act();
                }
                catch (Exception ex)
                {
                    MessageText(body, "Could not do it: " + ex.Message);
                }
            };
            return button;
        }
        // Undo and Dismiss first ask why (optional): later lessons for the repo avoid the like
        void AskWhy(string label, Action<string> act)
        {
            buttons.Children.Clear();
            var why = new TextBox
            {
                Style = (Style)FindResource("NotebookBox"), FontSize = 12.5, MinHeight = 34, Padding = new Thickness(10, 5, 10, 5), Width = 330,
                Margin = new Thickness(0, 0, 8, 0), ToolTip = "Why? (optional) - the next lessons avoid this",
            };
            System.Windows.Automation.AutomationProperties.SetName(why, "Why");
            buttons.Children.Add(why);
            buttons.Children.Add(Act(label, true, () => act(why.Text)));
            why.Focus();
        }
        if (lesson.Status == "saved")
        {
            buttons.Children.Add(Act("Undo", false, () => AskWhy("Undo it", why => Factory.Undo(run, lesson, why))));
            buttons.Children.Add(Act("Open note", false, () => OpenFile(lesson.File)));
        }
        else if (lesson.Status == "pending")
        {
            buttons.Children.Add(Act("Apply", true, () => Factory.Apply(run, lesson)));
            buttons.Children.Add(Act("Dismiss", false, () => AskWhy("Dismiss it", why => Factory.Dismiss(run, lesson, why))));
        }
        if (lesson.Feedback.Length > 0)
        {
            body.Children.Add(new TextBlock { Text = "Why: " + lesson.Feedback, FontSize = 12, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }
        else if (lesson.Status == "applied")
        {
            buttons.Children.Add(Act("Undo", false, () => AskWhy("Undo it", why => Factory.Undo(run, lesson, why))));
            buttons.Children.Add(Act("Open file", false, () => OpenFile(lesson.File)));
        }
        if (buttons.Children.Count > 0) body.Children.Add(buttons);
        return new Border
        {
            BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = body,
            Opacity = lesson.Status is "dismissed" or "undone" ? 0.5 : 1,
        };
    }

    private void MessageText(StackPanel body, string text) =>
        body.Children.Add(new TextBlock { Text = text, Foreground = (Brush)FindResource("CoralInkBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

    private void OpenFile(string? file)
    {
        if (file != null && File.Exists(file)) Open(file);
    }

    // a page, file or folder outside Dev break, in front: the window stays on top for the hourly question, so the
    // browser or Explorer would open behind it, unseen - it lets go until the next question puts it back on top
    private void Open(string target)
    {
        if (Window.GetWindow(this) is { } window) window.Topmost = false;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) Factory.Stop(_shown);
    }

    private void TakeAnyway_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) _shown.TakeAnyway = true;
        TakeAnywayButton.Visibility = Visibility.Collapsed;
    }

    private async void Anyway_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) await Factory.PullRequestAnywayAsync(_shown);
    }

    private void OpenPr_Click(object sender, RoutedEventArgs e)
    {
        if (_shown?.PrUrl is { Length: > 0 } url) Open(url);
    }

    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) OpenSessionRequested?.Invoke(new SessionInfo(_shown.SessionId, _shown.Worktree, _shown.Title, DateTime.Now, HasAiTitle: true));
    }

    private void Proof_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null && Directory.Exists(Factory.ProofDir(_shown))) Open(Factory.ProofDir(_shown));
    }

    // the diagnosis goes to Development as a new task, shown at once
    private async void FixIt_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } diagnosed) return;
        FactoryRun run = Factory.HandOff(diagnosed);
        Select(run);
        await Factory.RunAsync(run);
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null && File.Exists(_shown.ReportFile)) Open(_shown.ReportFile);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null && Directory.Exists(_shown.Worktree)) Open(_shown.Worktree);
    }

    // --- shared bits ---

    // where a task is, in the inbox's words: the station it works at, or what it waits for, or what it handed over
    private static string StatusText(FactoryRun run)
    {
        if (run.Working) return (FactoryLine.LineOf(run).FirstOrDefault(s => s.Stages.Contains(run.Stage))?.Name ?? StageName(run.Stage)) + "…";
        string status = run.Stage switch
        {
            FactoryStage.NeedsYou => "needs you",
            FactoryStage.Stopped => "stopped",
            FactoryStage.Done when run.Accepted => "accepted",
            FactoryStage.Done when run.PrUrl.Length > 0 => "draft PR ready",
            FactoryStage.Done when run.ReportFile.Length > 0 => "report ready",
            FactoryStage.Done when run.Result.Length > 0 => "answer ready",
            FactoryStage.Done => "done",
            _ => StageName(run.Stage),
        };
        return run.CleanedUp ? status + " · cleaned up" : status;
    }

    private static string StageName(FactoryStage stage) => stage switch
    {
        FactoryStage.PullRequest => "Ship",
        // where the task works: the developer's checkout, or a separate worktree when they chose one
        FactoryStage.Worktree => "Workspace",
        FactoryStage.Locate => "Gate",
        FactoryStage.NeedsYou => "Needs you",
        _ => stage.ToString(),
    };

    // green and pulsing while the task works, red when it needs you or was stopped, none when it is done
    private static void SetLight(Ellipse light, FactoryRun run)
    {
        light.BeginAnimation(OpacityProperty, null);
        light.Opacity = 1;
        bool red = !run.Working && run.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped;
        light.Visibility = run.Working || red ? Visibility.Visible : Visibility.Collapsed;
        light.Fill = new SolidColorBrush(run.Working ? Color.FromRgb(0x2E, 0xB8, 0x5C) : Color.FromRgb(0xE5, 0x48, 0x2E));
        light.Stroke = Brushes.Black;
        light.StrokeThickness = 1.2;
        if (run.Working)
        {
            light.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
    }
}
