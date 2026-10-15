using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DevBreak;

/// <summary>The learn path view: a course per tab, its lessons with progress on the left, the open lesson on the right.</summary>
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
        ShowLesson(null, null);
        ShowLoading($"Writing the {category.Name} course...");
        try
        {
            IReadOnlyList<Lesson> syllabus = await LearnPath.SyllabusAsync(category);
            if (version != _version) return;
            _syllabus = syllabus;
            for (int i = 0; i < syllabus.Count; i++)
            {
                LessonList.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock { Text = ItemText(category, i), TextWrapping = TextWrapping.Wrap },
                    ToolTip = syllabus[i].Summary,
                });
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
        LessonTitle.Text = $"{index + 1}. {lesson.Title}";
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
            LessonTitle.Text = LessonSummary.Text = "";
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

    private void UpdateProgress()
    {
        int done = Enumerable.Range(0, _syllabus!.Count).Count(i => LearnPath.IsDone(Current, i));
        ProgressText.Text = $"{done} of {_syllabus.Count} lessons done";
    }

    private string ItemText(Category category, int index) =>
        $"{(LearnPath.IsDone(category, index) ? "✓" : " ")}  {index + 1}. {_syllabus![index].Title}";

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
        ((TextBlock)((ListBoxItem)LessonList.Items[Index]).Content).Text = ItemText(Current, Index);
        UpdateProgress();
        UpdateButtons();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => LessonList.SelectedIndex = Index - 1;

    private void Next_Click(object sender, RoutedEventArgs e) => LessonList.SelectedIndex = Index + 1;
}
