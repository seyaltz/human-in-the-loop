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
/// The Factory view: a new task (typed, a Jira issue or a spec) for a repository, the runs, and the selected run -
/// its pipeline, its log, its draft PR and what it learned for the repo's Claude (memory saved, rules to approve).
/// </summary>
public partial class FactoryView : UserControl
{
    private static readonly FactoryStage[] Stages =
        [FactoryStage.Locate, FactoryStage.Worktree, FactoryStage.Plan, FactoryStage.Build, FactoryStage.Verify, FactoryStage.Prove, FactoryStage.Review, FactoryStage.PullRequest, FactoryStage.Learn];

    private readonly Dictionary<string, (Ellipse Light, TextBlock Status)> _rows = [];
    private FactoryRun? _shown;
    private int _shownLog = -1;
    private string? _shownLessons;
    private string? _shownTrace;
    private string? _shownEvidence;
    // the trace steps the developer opened stay open while the trace updates
    private readonly HashSet<int> _openSpans = [];
    private bool _started;

    /// <summary>"Open in Sessions": the run's Claude Code session, to continue it there.</summary>
    public event Action<SessionInfo>? OpenSessionRequested;

    // the slow watchdog: every few seconds, a step or tool call that just passed a minute turns amber and is told
    private readonly System.Windows.Threading.DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(5) };
    private static readonly Brush Amber = Frozen(Color.FromRgb(0xE8, 0xA3, 0x3D));

    public FactoryView()
    {
        InitializeComponent();
        Factory.Changed += run => Dispatcher.BeginInvoke(() => Update(run));
        _watchdog.Tick += (_, _) =>
        {
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

    /// <summary>Lists the runs and the repositories the first time the view is shown.</summary>
    public async void Start()
    {
        if (_started) return;
        _started = true;
        foreach (FactoryRun run in Factory.Runs) RunList.Items.Add(Row(run));
        UpdateRunsSummary();
        AutonomousBox.IsChecked = AppSettings.Get("factoryAutonomous") != "0";
        ReportBox.IsChecked = AppSettings.Get("factoryReport") == "1";
        IReadOnlyList<string> repos = await Task.Run(ClaudeProjects.GitRepos);
        foreach (string repo in repos.OrderBy(r => r, StringComparer.OrdinalIgnoreCase)) RepoBox.Items.Add(repo);
        if (AppSettings.Get("factoryRepo") is { Length: > 0 } last) RepoBox.Text = last;
        await RepoChangedAsync();
    }

    // --- the form ---

    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        if (TaskLabel == null) return;
        TaskLabel.Text = JiraSource.IsChecked == true ? "Jira issue key (e.g. ABC-123)"
            : SpecSource.IsChecked == true ? "The spec: a file path or a link" : "What to build or fix";
        BrowseSpecButton.Visibility = SpecSource.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TaskBox.Height = TypedSource.IsChecked == true ? 92 : 40;
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
    // with repos in it: both empty - the run finds the repo and fills them
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
        AppSettings.Set("factoryReport", report ? "1" : "0");
        FactoryRun run = Factory.Create(source, task, repo, baseBranch, VerifyBox.Text, autonomous, report);
        RunList.Items.Insert(0, Row(run));
        RunList.SelectedIndex = 0;
        RunsTab.IsChecked = true;
        UpdateRunsSummary();
        TaskBox.Clear();
        await Factory.RunAsync(run);
    }

    // --- the runs ---

    private ListBoxItem Row(FactoryRun run)
    {
        var light = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 5, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var status = new TextBlock { FontSize = 11.5, Foreground = (Brush)FindResource("InkSoftBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        _rows[run.Id] = (light, status);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = run.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 });
        text.Children.Add(status);
        var row = new DockPanel { Margin = new Thickness(2, 6, 4, 6) };
        DockPanel.SetDock(light, Dock.Left);
        row.Children.Add(light);
        row.Children.Add(text);
        var item = new ListBoxItem { Content = row, Tag = run, ToolTip = run.Repo };
        UpdateRow(run);
        return item;
    }

    private void UpdateRow(FactoryRun run)
    {
        if (!_rows.TryGetValue(run.Id, out var row)) return;
        SetLight(row.Light, run);
        row.Status.Text = $"{System.IO.Path.GetFileName(run.Repo.Length > 0 ? run.Repo : run.Folder)} · {StageName(run)}{(run.Cost > 0 ? $" · ${run.Cost:0.00}" : "")}";
        if (((row.Status.Parent as StackPanel)?.Children[0]) is TextBlock title) title.Text = run.Title;
    }

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunList.SelectedItem is ListBoxItem { Tag: FactoryRun run }) Show(run);
    }

    // the runs that were working at the last update: one that stops needing the developer is told once
    private readonly HashSet<string> _working = [];

    private void Update(FactoryRun run)
    {
        if (run.Working) _working.Add(run.Id);
        else if (_working.Remove(run.Id) && run.Stage == FactoryStage.NeedsYou)
        {
            App.Alert("Dev break factory - needs you", $"{Short(run.Title, 60)}\n{Short(run.Note, 180)}");
        }
        UpdateRow(run);
        UpdateRunsSummary();
        if (run == _shown) Show(run);
    }

    private void LeftTab_Checked(object sender, RoutedEventArgs e)
    {
        if (FormArea == null || RunsArea == null) return;
        FormArea.Visibility = NewTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RunsArea.Visibility = RunsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRunsSummary()
    {
        List<FactoryRun> runs = Factory.Runs;
        RunsTab.Content = runs.Count > 0 ? $"History ({runs.Count})" : "History";
        int working = runs.Count(r => r.Working), waiting = runs.Count(r => !r.Working && r.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped);
        int prs = runs.Count(r => r.PrUrl.Length > 0);
        RunsSummary.Text = runs.Count == 0 ? "No runs yet." : $"{working} working · {waiting} to continue · {prs} with a PR";
        CleanAllButton.IsEnabled = runs.Any(r => !r.Working && !r.CleanedUp);
    }

    // --- continue and clean up ---

    private Func<Task>? _confirmed;

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) await Factory.ContinueAsync(_shown);
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultText.Text.Length > 0) Clipboard.SetText(ResultText.Text);
    }

    private async void Worktree_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) await Factory.UseWorktreeAsync(_shown);
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
            ? $"Delete this run's evidence and exported trace{(info.Pushed ? ", and its local branch (it is on the remote)" : "")} - {Factory.Size(info.Bytes)}. "
            : $"Delete this run's worktree, evidence, exported trace and session files - {Factory.Size(info.Bytes)}. ";
        AskConfirm(what + "The record and its trace stay; your checkout, its memory and the remote branch and PR are not touched." +
            (lose.Count > 0 ? "\n⚠ This loses: " + string.Join("; ", lose) + "." : ""), lose.Count > 0, async () =>
            {
                string freed = await Factory.CleanUpAsync(run);
                Show(run);
                RunNote.Text = freed;
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
        if (_shown == null && RunList.Items.Count > 0) RunList.SelectedIndex = 0;
        AskConfirm($"Clean up {runs.Count} run{(runs.Count == 1 ? "" : "s")}: worktrees, evidence, exported traces and session files - {Factory.Size(bytes)}. " +
            "The records stay; your checkouts, memory and remote branches and PRs are not touched." +
            (lose.Count > 0 ? $"\n⚠ Uncommitted or unpushed work is lost in: {string.Join("; ", lose)}." : ""), lose.Count > 0, async () =>
            {
                foreach (FactoryRun run in runs) await Factory.CleanUpAsync(run);
                if (_shown != null) Show(_shown);
                RunNote.Text = $"Cleaned up {runs.Count} run{(runs.Count == 1 ? "" : "s")} - {Factory.Size(bytes)} freed.";
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
        DeleteText.Text = "Claude checks what is worth keeping from this run before it is deleted...";
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
            DeleteText.Text = "Could not check the run: " + ex.Message;
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
        DeleteConfirmButton.Content = lose.Count > 0 ? "Keep the ticked and delete anyway" : "Keep the ticked and delete the run";
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
            RunNote.Text = result;
            return;
        }
        // the run is gone: off the list, onto the next one
        ListBoxItem? item = RunList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == run);
        if (item != null) RunList.Items.Remove(item);
        _rows.Remove(run.Id);
        _shown = null;
        if (RunList.Items.Count > 0) RunList.SelectedIndex = 0;
        else RunArea.Visibility = Visibility.Collapsed;
        RunNote.Text = result;
        UpdateRunsSummary();
    }

    private void DeleteCancel_Click(object sender, RoutedEventArgs e)
    {
        _deleting = null;
        DeletePanel.Visibility = Visibility.Collapsed;
    }

    private void AskConfirm(string text, bool loses, Func<Task> act)
    {
        if (RunArea.Visibility != Visibility.Visible && RunList.Items.Count > 0) RunList.SelectedIndex = 0;
        ConfirmText.Text = text;
        ConfirmText.Foreground = (Brush)FindResource(loses ? "CoralInkBrush" : "InkBrush");
        ConfirmButton.Content = loses ? "Delete it anyway" : "Delete it";
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

    // --- the selected run ---

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
            GoodRating.IsChecked = run.Rating == "good";
            BadRating.IsChecked = run.Rating == "bad";
            RatingNote.Text = run.RatingNote;
            FeedbackBox.Clear();
            ExportText.Visibility = Visibility.Collapsed;
        }
        // feedback needs a run that is not working and still has its worktree and session
        // the run has a session to talk to once its first Claude turn ran (a NEEDS_YOU in the plan leaves no plan yet)
        bool canFeedback = !run.Working && !run.CleanedUp && run.Trace.Any(s => s.Kind == "turn") && Directory.Exists(run.Worktree);
        FeedbackButton.IsEnabled = canFeedback;
        FeedbackBox.IsEnabled = canFeedback;
        RateButton.IsEnabled = !run.Working && run.Repo.Length > 0;
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
        RunTitle.Text = run.Title;
        RunMeta.Text = run.Repo.Length == 0 ? $"{run.Folder}   ·   finding the repo"
            : $"{run.Repo}{(run.InCheckout ? " (your checkout)" : run.UseWorktree ? " (separate worktree)" : "")}   ·   {run.Branch} from {run.BaseBranch}   ·   {run.Steps} steps   ·   ${run.Cost:0.00}";
        RunNote.Text = run.Working ? $"{StageName(run)}..." : run.Note.Replace("\r", "").Split('\n')[0].Trim();
        RunNote.Foreground = (Brush)FindResource(!run.Working && run.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped ? "CoralInkBrush" : "VioletBrush");
        StopButton.Visibility = run.Working ? Visibility.Visible : Visibility.Collapsed;
        string result = run.Result + (run.PrUrl.Length > 0 ? (run.Result.Length > 0 ? "\n\n" : "") + "Draft PR: " + run.PrUrl : "");
        ResultBox.Visibility = result.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ResultText.Text != result) ResultText.Text = result;
        ContinueButton.Visibility = !run.Working && !run.CleanedUp && run.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped
            ? Visibility.Visible : Visibility.Collapsed;
        CleanButton.Visibility = !run.Working && !run.CleanedUp ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = run.Working ? Visibility.Collapsed : Visibility.Visible;
        if (_deleting != null && _deleting != run) DeleteCancel_Click(this, new RoutedEventArgs());
        // the run asked because the developer's checkout could not be used
        WorktreeButton.Visibility = !run.Working && !run.CleanedUp && !run.InCheckout && !run.UseWorktree && run.Stage == FactoryStage.NeedsYou
            && run.Note.Contains("Use a separate worktree", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        AnywayButton.Visibility = !run.Working && run.Stage == FactoryStage.NeedsYou && run.Plan.Length > 0 && run.PrUrl.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        OpenPrButton.Visibility = run.PrUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionButton.Visibility = FolderButton.Visibility = Directory.Exists(run.Worktree) ? Visibility.Visible : Visibility.Collapsed;
        ReportButton.Visibility = run.ReportFile.Length > 0 && File.Exists(run.ReportFile) ? Visibility.Visible : Visibility.Collapsed;
        string proof = Factory.ProofDir(run);
        ProofButton.Visibility = Directory.Exists(proof) && Directory.EnumerateFiles(proof, "*", SearchOption.AllDirectories).Any()
            ? Visibility.Visible : Visibility.Collapsed;
        SessionButton.IsEnabled = !run.Working;
        ShowPipeline(run);
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

    // a chip per stage: done (✓), now (violet), where it stopped (coral), to come (faded)
    private void ShowPipeline(FactoryRun run)
    {
        Pipeline.Children.Clear();
        HashSet<FactoryStage> seen = run.Log.Select(s => s.Stage).ToHashSet();
        FactoryStage? failedAt = run.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped
            ? run.Log.LastOrDefault(s => s.Stage is not (FactoryStage.NeedsYou or FactoryStage.Stopped or FactoryStage.Learn))?.Stage : null;
        foreach (FactoryStage stage in Stages)
        {
            bool now = run.Working && run.Stage == stage;
            bool failed = !run.Working && failedAt == stage;
            bool done = seen.Contains(stage) && !now && !failed;
            // the step working now turns amber once it is slow
            bool slow = now && run.Trace.LastOrDefault(s => s.Ended == null) is { Slow: true };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4),
                BorderThickness = new Thickness(1.5), BorderBrush = (Brush)FindResource("InkBrush"),
                ToolTip = slow ? "Slow: over a minute" : null,
                Background = slow ? Amber : now ? (Brush)FindResource("VioletBrush") : failed ? (Brush)FindResource("CoralBrush") : Brushes.Transparent,
                Opacity = done || now || failed ? 1 : 0.4,
                Child = new TextBlock
                {
                    Text = (done ? "✓ " : "") + StageName(stage), FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = now ? Brushes.White : (Brush)FindResource("InkBrush"),
                },
            };
            Pipeline.Children.Add(chip);
        }
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

    // each step a line - time, stage, what, how long, cost, tokens, tools - that opens to its tool calls, prompt and answer
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
            if (span.Cost > 0) stats.Add($"${span.Cost:0.00}");
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

    private static string Tokens(long n) => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

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
            ExportRun.Text = System.IO.Path.GetFileName(file);
            ExportLink.Tag = file;
            ExportLink.ToolTip = file + "\nClick to show it in its folder";
        }
        catch (Exception ex)
        {
            ExportRun.Text = "Could not export: " + ex.Message;
            ExportLink.Tag = null;
        }
        ExportText.Visibility = Visibility.Visible;
    }

    private void ExportLink_Click(object sender, RoutedEventArgs e)
    {
        if (ExportLink.Tag is string file && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
    }

    private async void Rate_Click(object sender, RoutedEventArgs e)
    {
        if (_shown == null) return;
        string rating = GoodRating.IsChecked == true ? "good" : BadRating.IsChecked == true ? "bad" : "";
        if (rating.Length == 0) return;
        await Factory.RateAsync(_shown, rating, RatingNote.Text);
    }

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        if (_shown == null || FeedbackBox.Text.Trim().Length == 0) return;
        string text = FeedbackBox.Text;
        FeedbackBox.Clear();
        await Factory.FeedbackAsync(_shown, text);
    }

    private void ShowLessons(FactoryRun run)
    {
        LessonList.Children.Clear();
        LessonsTab.Content = run.Lessons.Count > 0 ? $"Learned ({run.Lessons.Count})" : "Learned";
        if (run.Lessons.Count == 0)
        {
            LessonList.Children.Add(new TextBlock { Text = "Nothing learned yet - the run learns when it ends.", Style = (Style)FindResource("Caption"), Margin = new Thickness(4) });
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

    private static void OpenFile(string? file)
    {
        if (file != null && File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) Factory.Stop(_shown);
    }

    private async void Anyway_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) await Factory.PullRequestAnywayAsync(_shown);
    }

    private void OpenPr_Click(object sender, RoutedEventArgs e)
    {
        if (_shown?.PrUrl is { Length: > 0 } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null) OpenSessionRequested?.Invoke(new SessionInfo(_shown.SessionId, _shown.Worktree, _shown.Title, DateTime.Now, HasAiTitle: true));
    }

    private void Proof_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null && Directory.Exists(Factory.ProofDir(_shown))) Process.Start(new ProcessStartInfo(Factory.ProofDir(_shown)) { UseShellExecute = true });
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null && File.Exists(_shown.ReportFile)) Process.Start(new ProcessStartInfo(_shown.ReportFile) { UseShellExecute = true });
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_shown != null && Directory.Exists(_shown.Worktree)) Process.Start(new ProcessStartInfo(_shown.Worktree) { UseShellExecute = true });
    }

    // --- shared bits ---

    private static string StageName(FactoryRun run) => run.Working ? StageName(run.Stage) : run.Stage switch
    {
        FactoryStage.Done => "draft PR",
        FactoryStage.NeedsYou => "needs you",
        FactoryStage.Stopped => "stopped",
        _ => StageName(run.Stage),
    };

    private static string StageName(FactoryStage stage) => stage switch
    {
        FactoryStage.PullRequest => "Draft PR",
        // where the run works: the developer's checkout, or a separate worktree when they chose one
        FactoryStage.Worktree => "Workspace",
        FactoryStage.NeedsYou => "Needs you",
        _ => stage.ToString(),
    };

    // green and pulsing while the run works, red when it needs you or was stopped, none when it is done
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
