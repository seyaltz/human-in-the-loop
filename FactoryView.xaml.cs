using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
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
        [FactoryStage.Worktree, FactoryStage.Plan, FactoryStage.Build, FactoryStage.Verify, FactoryStage.Review, FactoryStage.PullRequest, FactoryStage.Learn];

    private readonly Dictionary<string, (Ellipse Light, TextBlock Status)> _rows = [];
    private FactoryRun? _shown;
    private int _shownLog = -1;
    private string? _shownLessons;
    private bool _started;

    /// <summary>"Open in Sessions": the run's Claude Code session, to continue it there.</summary>
    public event Action<SessionInfo>? OpenSessionRequested;

    public FactoryView()
    {
        InitializeComponent();
        Factory.Changed += run => Dispatcher.BeginInvoke(() => Update(run));
    }

    /// <summary>Lists the runs and the repositories the first time the view is shown.</summary>
    public async void Start()
    {
        if (_started) return;
        _started = true;
        foreach (FactoryRun run in Factory.Runs) RunList.Items.Add(Row(run));
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

    // a repository picked: its current branch as the base, and the verify command used there last time
    private async Task RepoChangedAsync()
    {
        string repo = RepoBox.Text.Trim().TrimEnd('\\');
        if (repo == _formRepo) return;
        _formRepo = repo;
        if (!IsRepo(repo)) return;
        BaseBox.Text = await Factory.CurrentBranchAsync(repo);
        VerifyBox.Text = AppSettings.Get("factoryVerify:" + repo.ToLowerInvariant()) ?? "";
    }

    private static bool IsRepo(string folder) =>
        folder.Length > 0 && (Directory.Exists(System.IO.Path.Combine(folder, ".git")) || File.Exists(System.IO.Path.Combine(folder, ".git")));

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await RepoChangedAsync();
        string task = TaskBox.Text.Trim(), repo = RepoBox.Text.Trim().TrimEnd('\\'), baseBranch = BaseBox.Text.Trim();
        string? error = task.Length == 0 ? "Write the task first." : !IsRepo(repo) ? "Pick a git repository." : baseBranch.Length == 0 ? "Name the base branch." : null;
        FormError.Text = error ?? "";
        FormError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        if (error != null) return;
        FactorySource source = JiraSource.IsChecked == true ? FactorySource.Jira : SpecSource.IsChecked == true ? FactorySource.Spec : FactorySource.Typed;
        AppSettings.Set("factoryRepo", repo);
        if (VerifyBox.Text.Trim().Length > 0) AppSettings.Set("factoryVerify:" + repo.ToLowerInvariant(), VerifyBox.Text.Trim());
        FactoryRun run = Factory.Create(source, task, repo, baseBranch, VerifyBox.Text);
        RunList.Items.Insert(0, Row(run));
        RunList.SelectedIndex = 0;
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
        row.Status.Text = $"{System.IO.Path.GetFileName(run.Repo)} · {StageName(run)}{(run.Cost > 0 ? $" · ${run.Cost:0.00}" : "")}";
        if (((row.Status.Parent as StackPanel)?.Children[0]) is TextBlock title) title.Text = run.Title;
    }

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunList.SelectedItem is ListBoxItem { Tag: FactoryRun run }) Show(run);
    }

    private void Update(FactoryRun run)
    {
        UpdateRow(run);
        if (run == _shown) Show(run);
    }

    // --- the selected run ---

    private void Show(FactoryRun run)
    {
        if (_shown != run)
        {
            _shown = run;
            _shownLog = -1;
            _shownLessons = null;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        RunArea.Visibility = Visibility.Visible;
        SetLight(Light, run);
        RunTitle.Text = run.Title;
        RunMeta.Text = $"{run.Repo}   ·   {run.Branch} from {run.BaseBranch}   ·   {run.Steps} steps   ·   ${run.Cost:0.00}";
        RunNote.Text = run.Working ? $"{StageName(run)}..." : run.Note.Replace("\r", "").Split('\n')[0].Trim();
        RunNote.Foreground = (Brush)FindResource(!run.Working && run.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped ? "CoralInkBrush" : "VioletBrush");
        StopButton.Visibility = run.Working ? Visibility.Visible : Visibility.Collapsed;
        AnywayButton.Visibility = !run.Working && run.Stage == FactoryStage.NeedsYou && run.Plan.Length > 0 && run.PrUrl.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        OpenPrButton.Visibility = run.PrUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionButton.Visibility = FolderButton.Visibility = Directory.Exists(run.Worktree) ? Visibility.Visible : Visibility.Collapsed;
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
            var chip = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4),
                BorderThickness = new Thickness(1.5), BorderBrush = (Brush)FindResource("InkBrush"),
                Background = now ? (Brush)FindResource("VioletBrush") : failed ? (Brush)FindResource("CoralBrush") : Brushes.Transparent,
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

    private void ShowLessons(FactoryRun run)
    {
        LessonList.Children.Clear();
        LessonsArea.Visibility = run.Lessons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (FactoryLesson lesson in run.Lessons) LessonList.Children.Add(LessonCard(run, lesson));
    }

    private Border LessonCard(FactoryRun run, FactoryLesson lesson)
    {
        string kind = lesson.Kind switch { "memory" => "memory", "claude-md" => "CLAUDE.md rule", "hook" => "hook", _ => "skill" };
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
        if (lesson.Status == "saved")
        {
            buttons.Children.Add(Act("Undo", false, () => Factory.Undo(run, lesson)));
            buttons.Children.Add(Act("Open note", false, () => OpenFile(lesson.File)));
        }
        else if (lesson.Status == "pending")
        {
            buttons.Children.Add(Act("Apply", true, () => Factory.Apply(run, lesson)));
            buttons.Children.Add(Act("Dismiss", false, () => Factory.Dismiss(run, lesson)));
        }
        else if (lesson.Status == "applied")
        {
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
