using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DevBreak;

public partial class QuestionWindow : Window
{
    private const double CollapsedHeight = 680;
    private const double WideWidth = 1180;

    // the window's starting width (QuestionWindow.xaml), which the quiz is laid out for
    private const double QuizWidth = 900;
    private const double WideHeight = 820;

    /// <summary>What one topic holds: its question (null while generating), error, typed answer and whether the answer is shown.</summary>
    private sealed class TabState(Level level, PendingQuestion pending)
    {
        public Level Level { get; } = level;
        public PendingQuestion Pending { get; } = pending;
        public string Topic => Pending.Topic;
        public Question? Question { get; set; }
        public string? Error { get; set; }
        public string Mine { get; set; } = "";
        public bool IsAnswerShown { get; set; }
        public bool IsTheoryOpen { get; set; }
        public bool IsScoring { get; set; }
        public Score? Score { get; set; }
        public string? ScoreError { get; set; }
        public ChatPanel? Chat { get; set; }
    }

    private readonly Dictionary<Category, TabState> _tabs = [];
    private readonly QuestionPool _pool;

    // null = Auto (by the time of day)
    private Level? _levelChoice;

    public QuestionWindow(QuestionPool pool)
    {
        _pool = pool;
        InitializeComponent();
        AddTopicChips();
        // a factory run's session continues in Sessions
        FactoryView.OpenSessionRequested += info =>
        {
            SessionsMode.IsChecked = true;
            SessionsView.Open(info);
        };

        Game.Awarded += OnAwarded;
        DevBreak.Topics.Changed += OnTopicsChanged;
        Closed += (_, _) =>
        {
            Game.Awarded -= OnAwarded;
            DevBreak.Topics.Changed -= OnTopicsChanged;
        };
        UpdateHud();
        TldrView.ChatChanged += panel =>
        {
            if (TldrMode.IsChecked == true) ShowChat(panel);
        };
        LearnView.ChatChanged += panel =>
        {
            if (LearnMode.IsChecked == true) ShowChat(panel);
        };

        Loaded += (_, _) =>
        {
            PlaceBottomRight(CollapsedHeight);
            Topics.SelectedIndex = 0;
        };
    }

    private Category Current => (Category)((ListBoxItem)Topics.SelectedItem).Tag;

    private void AddTopicChips()
    {
        foreach (Category category in Category.All)
        {
            Topics.Items.Add(new ListBoxItem { Content = category.Name, Tag = category });
        }
    }

    /// <summary>Rebuilds the chips after the topics change, staying on the same tab when it is still there.</summary>
    private void OnTopicsChanged()
    {
        Category? selected = Topics.SelectedItem != null ? Current : null;
        Topics.SelectionChanged -= Topics_SelectionChanged;
        Topics.Items.Clear();
        AddTopicChips();
        Topics.SelectionChanged += Topics_SelectionChanged;
        int index = Category.All.ToList().FindIndex(c => c == selected);
        Topics.SelectedIndex = Math.Max(0, index);
        LearnView.Reload();
        TldrView.Reload();
    }

    private void Topics_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Topics.SelectedItem == null) return;
        if (!_tabs.ContainsKey(Current)) NewQuestion();
        ShowTab();
    }

    private void NewQuestion()
    {
        Category category = Current;
        Level level = _levelChoice ?? Level.At(DateTime.Now.TimeOfDay);
        var state = new TabState(level, _pool.Take(category, level));
        _tabs[category] = state;
        Generate(category, state);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        NewQuestion();
        ShowTab();
    }

    private async void Generate(Category category, TabState state)
    {
        try
        {
            state.Question = await state.Pending.Question;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
        }
        if (Topics.SelectedItem != null && Current == category && _tabs[category] == state) ShowTab();
    }

    private void ShowTab()
    {
        TabState state = _tabs[Current];
        Title = $"Dev break - {Current.Name} - {state.Level.Name} - {state.Topic}";
        LevelText.Text = state.Level.Name.ToUpperInvariant();
        LevelBadge.Background = Res(state.Level == Level.Heavy ? "CoralTintBrush"
            : state.Level == Level.Medium ? "MarkerBrush"
            : "VioletTintBrush");
        LevelNote.Text = state.Level == Level.Heavy ? "a meaty one"
            : state.Level == Level.Medium ? "a medium one"
            : "a light one";
        TopicText.Text = state.Topic;
        MineBox.Text = state.Mine;
        UpdatePlaceholder();

        bool isLoading = state.Question == null && state.Error == null;
        Loading.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = $"Generating a {state.Level.Name.ToLowerInvariant()} question about {state.Topic}...";
        QuestionBox.Document = CodeRenderer.Render(state.Error != null
            ? "Could not get a question from the claude CLI:\n\n" + state.Error
            : state.Question?.Text ?? "");
        AnswerBox.Document = CodeRenderer.Render(state.Question?.Answer ?? "");
        TheoryBox.Document = CodeRenderer.Render(state.Question?.Theory ?? "");
        TheoryExpander.IsEnabled = state.Question != null;
        TheoryExpander.IsExpanded = state.IsTheoryOpen;
        ShowButton.IsEnabled = state.Question != null && !state.IsAnswerShown;
        UpdateCheckButton(state);
        MineBox.IsReadOnly = state.IsAnswerShown;
        MineHint.Visibility = state.IsAnswerShown ? Visibility.Collapsed : Visibility.Visible;
        UpdatePlaceholder();
        ShowScore(state);
        SetAnswerVisible(state.IsAnswerShown);
        if (QuizMode.IsChecked == true) ShowChat(state.Chat);
    }

    /// <summary>Shows a side panel (a discussion, or saving to Claude) on the right of the window, growing it to fit; null hides it.</summary>
    private void ShowChat(UserControl? panel)
    {
        ChatHost.Content = panel;
        ChatHost.Visibility = panel != null ? Visibility.Visible : Visibility.Collapsed;
        if (panel == null) return;
        Rect area = SystemParameters.WorkArea;
        double content = QuizMode.IsChecked == true ? QuizWidth : WideWidth;
        Width = Math.Max(Width, Math.Min(content + ChatHost.Width + ChatHost.Margin.Left, area.Width - 32));
        PlaceBottomRight(Height);
    }

    private void ShowScore(TabState state)
    {
        ScoringBar.Visibility = ScoringText.Visibility = state.IsScoring ? Visibility.Visible : Visibility.Collapsed;
        ScoreBadge.Visibility = state.Score != null ? Visibility.Visible : Visibility.Collapsed;
        FeedbackCard.Visibility = state.Score != null || state.ScoreError != null ? Visibility.Visible : Visibility.Collapsed;
        if (state.Score is { } score)
        {
            // the teacher's stamp
            ScoreText.Text = $"{score.Points}/10";
            ScoreWord.Text = score.Points == 10 ? "perfect!"
                : score.Points >= 8 ? "nice!"
                : score.Points >= 5 ? "close!"
                : "review it";
            ScoreBadge.ToolTip = $"Your score: {score.Points} out of 10";
            FeedbackText.Text = score.Feedback.Replace("`", "");
        }
        else if (state.ScoreError != null)
        {
            FeedbackText.Text = "Could not score your answer: " + state.ScoreError;
        }
    }

    private async void ScoreMine(Category category, TabState state)
    {
        state.IsScoring = true;
        try
        {
            state.Score = await QuestionGenerator.ScoreAsync(state.Question!, state.Mine);
            Game.QuestionAnswered(category, state.Topic, state.Score.Points);
        }
        catch (Exception ex)
        {
            state.ScoreError = ex.Message;
        }
        state.IsScoring = false;
        if (Topics.SelectedItem != null && Current == category && _tabs[category] == state) ShowScore(state);
    }

    private void SetAnswerVisible(bool visible)
    {
        AnswerPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AnswerRow.Height = visible ? new GridLength(1.3, GridUnitType.Star) : GridLength.Auto;
        if (visible)
        {
            // the whole work-area height, so question and answer both fit without scrolling
            Rect area = SystemParameters.WorkArea;
            PlaceBottomRight(area.Height - 32);
        }
    }

    private void PlaceBottomRight(double height)
    {
        Rect area = SystemParameters.WorkArea;
        Height = Math.Max(Height, height);
        Left = area.Right - Width - 16;
        Top = Math.Max(area.Top + 16, area.Bottom - Height - 16);
    }

    private void MineBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Topics.SelectedItem != null && _tabs.TryGetValue(Current, out TabState? state))
        {
            state.Mine = MineBox.Text;
            UpdateCheckButton(state);
        }
        UpdatePlaceholder();
    }

    private void UpdatePlaceholder() =>
        MinePlaceholder.Visibility = MineBox.Text.Length == 0 && !MineBox.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private void UpdateCheckButton(TabState state) =>
        CheckButton.IsEnabled = state.Question != null && !state.IsAnswerShown && state.Mine.Trim().Length > 0;

    private void Theory_Toggled(object sender, RoutedEventArgs e)
    {
        if (Topics.SelectedItem != null && _tabs.TryGetValue(Current, out TabState? state))
        {
            state.IsTheoryOpen = TheoryExpander.IsExpanded;
        }
    }

    private void Discuss_Click(object sender, RoutedEventArgs e)
    {
        TabState state = _tabs[Current];
        if (state.Chat == null)
        {
            ChatPanel chat = new(state.Topic, state.Question!, () => state.Mine, () => state.Score);
            chat.CloseRequested += () =>
            {
                state.Chat = null;
                if (ChatHost.Content == chat) ShowChat(null);
            };
            state.Chat = chat;
        }
        ShowChat(state.Chat);
        state.Chat.FocusInput();
    }

    /// <summary>Switches to the learn path.</summary>
    public void ShowLearn() => LearnMode.IsChecked = true;

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (LearnView == null || StatsView == null || TldrView == null || SessionsView == null || FactoryView == null || QuizView == null) return;
        bool learn = LearnMode.IsChecked == true;
        bool stats = StatsMode.IsChecked == true;
        bool tldr = TldrMode.IsChecked == true;
        bool sessions = SessionsMode.IsChecked == true;
        bool factory = FactoryMode.IsChecked == true;
        QuizView.Visibility = learn || stats || tldr || sessions || factory ? Visibility.Collapsed : Visibility.Visible;
        LearnView.Visibility = learn ? Visibility.Visible : Visibility.Collapsed;
        StatsView.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
        TldrView.Visibility = tldr ? Visibility.Visible : Visibility.Collapsed;
        SessionsView.Visibility = sessions ? Visibility.Visible : Visibility.Collapsed;
        FactoryView.Visibility = factory ? Visibility.Visible : Visibility.Collapsed;
        if (learn || stats || tldr || sessions || factory)
        {
            Title = learn ? "Dev break - learn path" : stats ? "Dev break - stats" : tldr ? "Dev break - TL;DR"
                : factory ? "Dev break - factory" : "Dev break - sessions";
            // courses, stats, the TL;DR and sessions need room; grow once, never shrink
            Rect area = SystemParameters.WorkArea;
            Width = Math.Max(Width, Math.Min(WideWidth, area.Width - 32));
            PlaceBottomRight(Math.Min(WideHeight, area.Height - 32));
            if (learn) LearnView.Start();
            else if (stats) StatsView.Refresh();
            else if (tldr) TldrView.Start();
            else if (factory) FactoryView.Start();
            else SessionsView.Start();
            // each mode keeps its own discussion: the lesson's and the TL;DR's come back; stats and sessions have none
            ShowChat(tldr ? TldrView.CurrentChat : learn ? LearnView.CurrentChat : null);
        }
        else if (Topics.SelectedItem != null && _tabs.ContainsKey(Current))
        {
            ShowTab();
        }
        else
        {
            ShowChat(null);
        }
    }

    private void Show_Click(object sender, RoutedEventArgs e) => Reveal(score: false);

    private void Check_Click(object sender, RoutedEventArgs e) => Reveal(score: true);

    private void Reveal(bool score)
    {
        TabState state = _tabs[Current];
        state.IsAnswerShown = true;
        ShowButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        MineBox.IsReadOnly = true;
        MineHint.Visibility = Visibility.Collapsed;
        UpdatePlaceholder();
        if (score) ScoreMine(Current, state);
        else Game.QuestionSeen(Current, state.Topic);
        ShowScore(state);
        SetAnswerVisible(true);
    }

    /// <summary>The gear menu: what the questions know about me, the level, and snooze.</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SettingsButton };

        var knowledge = new MenuItem { Header = "Knowledge about me" };
        foreach ((KnowledgeSource source, string label) in new[]
                 {
                     (KnowledgeSource.Claude, "From my Claude history"),
                     (KnowledgeSource.File, "From profile.txt"),
                     (KnowledgeSource.None, "None (generic)"),
                 })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Knowledge.Source == source };
            item.Click += (_, _) => Knowledge.SetSource(source);
            knowledge.Items.Add(item);
        }
        menu.Items.Add(knowledge);

        // discussions run with full access to this machine; this picks whose Claude setup they load
        var setup = new MenuItem { Header = "Claude in discussions" };
        foreach ((ClaudeSetup choice, string label) in new[]
                 {
                     (ClaudeSetup.Clean, "Clean - my profile only"),
                     (ClaudeSetup.Mine, "My Claude setup - CLAUDE.md, memory, skills, hooks (slower)"),
                 })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = QuestionGenerator.DiscussSetup == choice };
            item.Click += (_, _) => QuestionGenerator.SetDiscussSetup(choice);
            setup.Items.Add(item);
        }
        menu.Items.Add(setup);

        var topics = new MenuItem { Header = "Topics..." };
        topics.Click += (_, _) => new TopicsWindow(isFirstRun: false) { Owner = this }.ShowDialog();
        menu.Items.Add(topics);

        var levels = new MenuItem { Header = "Level for new questions" };
        foreach (Level? level in new[] { null, Level.Heavy, Level.Medium, Level.Light })
        {
            string label = level?.Name ?? $"Auto (now: {Level.At(DateTime.Now.TimeOfDay).Name})";
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _levelChoice == level };
            item.Click += (_, _) => _levelChoice = level;
            levels.Items.Add(item);
        }
        menu.Items.Add(levels);

        menu.Items.Add(new Separator());
        var snooze = new MenuItem { Header = $"Snooze {App.SnoozeMinutes} min" };
        snooze.Click += (_, _) =>
        {
            (Application.Current as App)?.Snooze();
            Close();
        };
        menu.Items.Add(snooze);
        menu.IsOpen = true;
    }

    private void UpdateHud()
    {
        int xp = Game.Xp;
        Rank rank = Game.RankOf(xp);
        Rank? next = Game.NextRank(xp);
        XpText.Text = $"{rank.Name} · {xp:N0} XP";
        XpChip.ToolTip = next == null ? "Top rank reached" : $"{next.From - xp:N0} XP to {next.Name}";
        int streak = Game.Streak();
        StreakText.Text = streak == 1 ? "1 day" : $"{streak} days";
        StreakChip.ToolTip = $"Daily streak: {streak} day(s) in a row (best {Game.BestStreak()})";
        int questions = Game.QuestionsOn(DateTime.Today);
        int lessons = Game.LessonsOn(DateTime.Today);
        bool met = questions >= Game.GoalQuestions && lessons >= Game.GoalLessons;
        GoalText.Text = met
            ? "Goal done"
            : $"{Math.Min(questions, Game.GoalQuestions)}/{Game.GoalQuestions} · {Math.Min(lessons, Game.GoalLessons)}/{Game.GoalLessons}";
        GoalChip.Background = Res(met ? "MarkerBrush" : "CardBrush");
        GoalChip.ToolTip = $"Today's goal: {Game.GoalQuestions} questions and {Game.GoalLessons} lesson (+{Game.GoalBonusXp} XP)";
    }

    private void OnAwarded(IReadOnlyList<string> lines)
    {
        UpdateHud();
        if (StatsView.Visibility == Visibility.Visible) StatsView.Refresh();
        if (lines.Count > 0) ShowToast(string.Join("\n", lines));
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250 + 2500 + 300 * text.Split('\n').Length))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600 + 2500 + 300 * text.Split('\n').Length))));
        fade.Completed += (_, _) =>
        {
            if (ToastText.Text == text) Toast.Visibility = Visibility.Collapsed;
        };
        Toast.BeginAnimation(OpacityProperty, fade);
    }
}
