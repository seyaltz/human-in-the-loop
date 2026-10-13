using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DevBreak;

public partial class QuestionWindow : Window
{
    private const double CollapsedHeight = 680;
    private const double LearnWidth = 1180;
    private const double LearnHeight = 820;

    /// <summary>What one tab holds: its question (null while generating), error, typed answer and whether the answer is shown.</summary>
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
        public ChatWindow? Chat { get; set; }
    }

    private readonly Dictionary<Category, TabState> _tabs = [];
    private readonly QuestionPool _pool;

    public QuestionWindow(QuestionPool pool)
    {
        _pool = pool;
        InitializeComponent();
        SnoozeButton.Content = $"Snooze {App.SnoozeMinutes} min";
        LevelChoice.Items.Add(new ComboBoxItem { Content = $"Auto (now: {Level.At(DateTime.Now.TimeOfDay).Name})" });
        foreach (Level level in new[] { Level.Heavy, Level.Medium, Level.Light })
        {
            LevelChoice.Items.Add(new ComboBoxItem { Content = level.Name, Tag = level });
        }
        LevelChoice.SelectedIndex = 0;
        foreach ((KnowledgeSource source, string label) in new[]
                 {
                     (KnowledgeSource.Claude, "From my Claude history"),
                     (KnowledgeSource.File, "From profile.txt"),
                     (KnowledgeSource.None, "None (generic)"),
                 })
        {
            KnowledgeChoice.Items.Add(new ComboBoxItem { Content = label, Tag = source });
            if (Knowledge.Source == source) KnowledgeChoice.SelectedIndex = KnowledgeChoice.Items.Count - 1;
        }
        KnowledgeChoice.SelectionChanged += (_, _) =>
            Knowledge.SetSource((KnowledgeSource)((ComboBoxItem)KnowledgeChoice.SelectedItem).Tag);
        foreach (Category category in Category.All)
        {
            Tabs.Items.Add(new TabItem { Header = category.Name, Tag = category });
        }

        Loaded += (_, _) =>
        {
            PlaceBottomRight(CollapsedHeight);
            Tabs.SelectedIndex = 0;
        };
    }

    private Category Current => (Category)((TabItem)Tabs.SelectedItem).Tag;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || Tabs.SelectedItem == null) return;
        if (!_tabs.ContainsKey(Current)) NewQuestion();
        ShowTab();
    }

    private void NewQuestion()
    {
        Category category = Current;
        Level level = (LevelChoice.SelectedItem as ComboBoxItem)?.Tag as Level ?? Level.At(DateTime.Now.TimeOfDay);
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
        if (Tabs.SelectedItem != null && Current == category && _tabs[category] == state) ShowTab();
    }

    private void ShowTab()
    {
        TabState state = _tabs[Current];
        Title = $"Dev break - {Current.Name} - {state.Level.Name} - {state.Topic}";
        LevelText.Text = state.Level.Name.ToUpperInvariant();
        LevelBadge.Background = new SolidColorBrush(state.Level == Level.Heavy ? Color.FromRgb(0xC6, 0x28, 0x28)
            : state.Level == Level.Medium ? Color.FromRgb(0xEF, 0x6C, 0x00)
            : Color.FromRgb(0x2E, 0x7D, 0x32));
        TopicText.Text = state.Topic;
        MineBox.Text = state.Mine;

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
        DiscussButton.IsEnabled = state.IsAnswerShown;
        MineBox.IsReadOnly = state.IsAnswerShown;
        ShowScore(state);
        SetAnswerVisible(state.IsAnswerShown);
    }

    private void ShowScore(TabState state)
    {
        ScoringBar.Visibility = ScoringText.Visibility = state.IsScoring ? Visibility.Visible : Visibility.Collapsed;
        ScoreBadge.Visibility = state.Score != null ? Visibility.Visible : Visibility.Collapsed;
        FeedbackText.Visibility = state.Score != null || state.ScoreError != null ? Visibility.Visible : Visibility.Collapsed;
        if (state.Score is { } score)
        {
            ScoreText.Text = $"Your score {score.Points}/10";
            ScoreBadge.Background = new SolidColorBrush(score.Points >= 8 ? Color.FromRgb(0x2E, 0x7D, 0x32)
                : score.Points >= 5 ? Color.FromRgb(0xEF, 0x6C, 0x00)
                : Color.FromRgb(0xC6, 0x28, 0x28));
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
        }
        catch (Exception ex)
        {
            state.ScoreError = ex.Message;
        }
        state.IsScoring = false;
        if (Tabs.SelectedItem != null && Current == category && _tabs[category] == state) ShowScore(state);
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
        if (Tabs.SelectedItem != null && _tabs.TryGetValue(Current, out TabState? state))
        {
            state.Mine = MineBox.Text;
            UpdateCheckButton(state);
        }
    }

    private void UpdateCheckButton(TabState state) =>
        CheckButton.IsEnabled = state.Question != null && !state.IsAnswerShown && state.Mine.Trim().Length > 0;

    private void Theory_Toggled(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem != null && _tabs.TryGetValue(Current, out TabState? state))
        {
            state.IsTheoryOpen = TheoryExpander.IsExpanded;
        }
    }

    private void Discuss_Click(object sender, RoutedEventArgs e)
    {
        TabState state = _tabs[Current];
        if (state.Chat == null)
        {
            state.Chat = new ChatWindow(state.Topic, state.Question!, state.Mine);
            state.Chat.Closed += (_, _) => state.Chat = null;
            state.Chat.Left = Math.Max(SystemParameters.WorkArea.Left + 16, Left - state.Chat.Width - 12);
            state.Chat.Top = Top;
            state.Chat.Show();
        }
        state.Chat.Activate();
    }

    /// <summary>Switches to the learn path.</summary>
    public void ShowLearn() => LearnMode.IsChecked = true;

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (LearnView == null || QuizView == null) return;
        bool learn = LearnMode.IsChecked == true;
        QuizView.Visibility = learn ? Visibility.Collapsed : Visibility.Visible;
        LearnView.Visibility = learn ? Visibility.Visible : Visibility.Collapsed;
        if (learn)
        {
            Title = "Dev break - learn path";
            // a course needs room for the lesson list and the lesson; grow once, never shrink
            Rect area = SystemParameters.WorkArea;
            Width = Math.Max(Width, Math.Min(LearnWidth, area.Width - 32));
            PlaceBottomRight(Math.Min(LearnHeight, area.Height - 32));
            LearnView.Start();
        }
        else if (Tabs.SelectedItem != null && _tabs.ContainsKey(Current))
        {
            ShowTab();
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
        DiscussButton.IsEnabled = true;
        MineBox.IsReadOnly = true;
        if (score) ScoreMine(Current, state);
        ShowScore(state);
        SetAnswerVisible(true);
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.Snooze();
        Close();
    }
}
