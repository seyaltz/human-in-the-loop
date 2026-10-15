using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DevBreak;

/// <summary>The learn path view: a course per tab, its lessons as a path with progress on the left, the open lesson on the right.</summary>
public partial class LearnView : UserControl
{
    private IReadOnlyList<Lesson>? _syllabus;

    // bumped on every tab or lesson change, so a slow reply for an old selection is dropped
    private int _version;

    public LearnView()
    {
        InitializeComponent();
        AddCourseChips();
    }

    private void AddCourseChips()
    {
        foreach (Category category in LearnPath.Courses)
        {
            Tabs.Items.Add(new ListBoxItem { Content = category.Name, Tag = category });
        }
    }

    /// <summary>Rebuilds the course chips after the topics change; the first course opens again when the view is shown.</summary>
    public void Reload()
    {
        bool wasStarted = Tabs.SelectedIndex >= 0;
        ++_version;
        Tabs.SelectionChanged -= Tabs_SelectionChanged;
        Tabs.Items.Clear();
        AddCourseChips();
        Tabs.SelectionChanged += Tabs_SelectionChanged;
        _syllabus = null;
        LessonList.Items.Clear();
        CourseTitle.Text = ProgressText.Text = "";
        ProgressBar.Value = 0;
        ShowLesson(null, null);
        if (wasStarted && IsVisible) Start();
    }

    /// <summary>Opens the first course the first time the view is shown, so a quiz popup never writes a course.</summary>
    public void Start()
    {
        if (Tabs.SelectedIndex < 0) Tabs.SelectedIndex = 0;
    }

    private Category Current => (Category)((ListBoxItem)Tabs.SelectedItem).Tag;

    private int Index => LessonList.SelectedIndex;

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || Tabs.SelectedItem == null) return;
        int version = ++_version;
        Category category = Current;
        _syllabus = null;
        LessonList.Items.Clear();
        CourseTitle.Text = category.Name;
        ProgressText.Text = "";
        ProgressBar.Value = 0;
        ShowLesson(null, null);
        ShowLoading($"Writing the {category.Name} course...");
        try
        {
            IReadOnlyList<Lesson> syllabus = await LearnPath.SyllabusAsync(category);
            if (version != _version) return;
            _syllabus = syllabus;
            for (int i = 0; i < syllabus.Count; i++)
            {
                LessonList.Items.Add(new ListBoxItem { ToolTip = syllabus[i].Summary });
            }
            UpdateProgress();
            // continue where the course was left: the first lesson not done yet
            int next = Enumerable.Range(0, syllabus.Count).FirstOrDefault(i => !LearnPath.IsDone(category, i));
            LessonList.SelectedIndex = next;
        }
        catch (Exception ex)
        {
            if (version != _version) return;
            ShowError("Could not get the course from the claude CLI:\n\n" + ex.Message);
        }
    }

    private async void LessonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syllabus == null || Index < 0) return;
        int version = ++_version;
        Category category = Current;
        int index = Index;
        Lesson lesson = _syllabus[index];
        LessonList.ScrollIntoView(LessonList.SelectedItem);
        LessonNumber.Text = $"LESSON {index + 1} OF {_syllabus.Count}";
        LessonTitle.Text = lesson.Title;
        LessonSummary.Text = lesson.Summary;
        UpdateButtons();
        ShowLesson(null, null);
        ShowLoading($"Writing lesson {index + 1}: {lesson.Title}...");
        try
        {
            LessonContent content = await LearnPath.LessonAsync(category, index, _syllabus);
            if (version != _version) return;
            ShowLesson(content, null);
        }
        catch (Exception ex)
        {
            if (version != _version) return;
            ShowError("Could not get the lesson from the claude CLI:\n\n" + ex.Message);
        }
    }

    private void ShowLoading(string text)
    {
        LoadingText.Text = text;
        Loading.Visibility = Visibility.Visible;
    }

    private void ShowError(string text) => ShowLesson(null, text);

    private void ShowLesson(LessonContent? content, string? error)
    {
        Loading.Visibility = Visibility.Collapsed;
        LessonBox.Document = CodeRenderer.Render(error ?? content?.Text ?? "");
        CheckQuestionBox.Document = CodeRenderer.Render(content?.Check ?? "");
        AnswerBox.Document = CodeRenderer.Render(content?.Answer ?? "");
        AnswerExpander.IsExpanded = false;
        CheckPanel.Visibility = content != null ? Visibility.Visible : Visibility.Collapsed;
        if (_syllabus == null)
        {
            LessonNumber.Text = LessonTitle.Text = LessonSummary.Text = "";
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        bool hasLesson = _syllabus != null && Index >= 0;
        PreviousButton.IsEnabled = hasLesson && Index > 0;
        NextButton.IsEnabled = hasLesson && Index < _syllabus!.Count - 1;
        DoneButton.IsEnabled = hasLesson;
        DoneButton.Content = hasLesson && LearnPath.IsDone(Current, Index) ? "Done ✓ (undo)" : $"Mark as done (+{Game.LessonXp} XP)";
    }

    /// <summary>The progress bar, and every stop on the path (the next lesson moves as lessons get done).</summary>
    private void UpdateProgress()
    {
        Category category = Current;
        int done = Enumerable.Range(0, _syllabus!.Count).Count(i => LearnPath.IsDone(category, i));
        ProgressText.Text = $"{done} of {_syllabus.Count} done";
        ProgressBar.Value = (double)done / _syllabus.Count;
        int next = Enumerable.Range(0, _syllabus.Count).FirstOrDefault(i => !LearnPath.IsDone(category, i), -1);
        for (int i = 0; i < _syllabus.Count; i++)
        {
            ((ListBoxItem)LessonList.Items[i]).Content = Stop(category, i, i == next);
        }
    }

    /// <summary>One stop on the path: a line through a node (ticked when done, violet for up next) and the lesson's title.</summary>
    private UIElement Stop(Category category, int index, bool isNext)
    {
        bool done = LearnPath.IsDone(category, index);
        bool previousDone = index > 0 && LearnPath.IsDone(category, index - 1);
        bool isLast = index == _syllabus!.Count - 1;

        var rail = new Grid { Width = 32, Margin = new Thickness(0, 0, 12, 0) };
        rail.RowDefinitions.Add(new RowDefinition());
        rail.RowDefinitions.Add(new RowDefinition());
        if (index > 0) rail.Children.Add(Line(previousDone, row: 0));
        if (!isLast) rail.Children.Add(Line(done, row: 1));

        var node = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Res(done ? "MarkerBrush" : isNext ? "VioletBrush" : "CardBrush"),
            BorderBrush = Res(done || isNext ? "InkBrush" : "MutedBrush"),
        };
        if (done)
        {
            node.Child = new Path
            {
                Data = (Geometry)FindResource("CheckIcon"), Style = (Style)FindResource("IconPath"), Width = 14, Height = 14,
                Stroke = Res("InkBrush"), StrokeThickness = 3,
            };
        }
        else
        {
            node.Child = new TextBlock
            {
                Text = (index + 1).ToString(), FontWeight = FontWeights.Bold, FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = isNext ? Brushes.White : Res("InkSoftBrush"),
            };
        }
        Grid.SetRowSpan(node, 2);
        rail.Children.Add(node);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 4, 8) };
        text.Children.Add(new TextBlock
        {
            Text = _syllabus[index].Title, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            FontWeight = isNext ? FontWeights.Bold : done ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Res(done || isNext ? "InkBrush" : "InkSoftBrush"),
        });
        if (done || isNext)
        {
            text.Children.Add(new TextBlock
            {
                Text = done ? "Done" : "Up next", FontSize = 11.5, Foreground = Res("InkSoftBrush"),
            });
        }

        var stop = new DockPanel { MinHeight = 48 };
        DockPanel.SetDock(rail, Dock.Left);
        stop.Children.Add(rail);
        stop.Children.Add(text);
        return stop;
    }

    private Rectangle Line(bool isWalked, int row)
    {
        var line = new Rectangle { Width = 2, Fill = Res(isWalked ? "InkBrush" : "LineBrush"), HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetRow(line, row);
        return line;
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        bool done = !LearnPath.IsDone(Current, Index);
        LearnPath.SetDone(Current, Index, done);
        if (done)
        {
            bool finished = Enumerable.Range(0, _syllabus!.Count).All(i => LearnPath.IsDone(Current, i));
            Game.LessonDone(Current, Index, _syllabus[Index].Title, finished);
        }
        else
        {
            Game.LessonUndone(Current, Index);
        }
        // get the next lesson ready while this one is read
        if (done) _ = LearnPath.PrepareAsync(Current);
        UpdateProgress();
        UpdateButtons();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => LessonList.SelectedIndex = Index - 1;

    private void Next_Click(object sender, RoutedEventArgs e) => LessonList.SelectedIndex = Index + 1;
}
