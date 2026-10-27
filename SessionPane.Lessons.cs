using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// Lessons under a turn: when a turn needed retries, was corrected, or was stopped and asked again, a model drafts what
/// to keep; the user edits it and saves it (a memory, a skill or a rule, where the CLI loads it natively) or dismisses
/// it with a reason the next drafts read. Nothing is written without the click, and every save can be undone.
/// </summary>
public partial class SessionPane
{
    private readonly HashSet<TurnLog> _askedLessons = [];

    // the turn ended: did it need retries
    private void LessonsAfterTurn(SessionState state)
    {
        if (state.Log is { } log && SessionLessons.ShouldPropose(log, null, false)) Logged(ProposeLessons(state, log, null), "lessons");
    }

    // a new message: does it correct the turn before it, or ask again what was stopped
    private void LessonsOnMessage(SessionState state, TurnLog? previous, string message, bool stopped)
    {
        bool fromCard = state.FromCard;
        state.FromCard = false;
        if (previous == null || fromCard) return;
        string text = AttachedFiles.Split(SessionAnswers.WithoutShape(message)).Text;
        bool reasked = stopped && text.Trim().Length > 0;
        if (SessionLessons.ShouldPropose(previous, text, reasked) && (reasked || SessionStats.Correction.IsMatch(text)))
        {
            Logged(ProposeLessons(state, previous, text), "lessons");
        }
    }

    private async Task ProposeLessons(SessionState state, TurnLog log, string? correction)
    {
        if (!_askedLessons.Add(log)) return;
        List<FactoryLesson> lessons = await SessionLessons.ProposeAsync(state.Info.Folder, log, correction);
        Log($"lessons ({state.Info.Id[..8]}): {lessons.Count} proposed");
        foreach (FactoryLesson lesson in lessons)
        {
            SessionLessons.Record(state.Info.Folder, state.Info.Id, lesson);
            LessonCard(state, log, lesson);
        }
    }

    // the grader found the same criterion broken in 3 turns of this folder: keep it as a rule (once, at the third)
    private void RepeatedViolation(SessionState state, string criterion, string evidence, int times)
    {
        Log($"grade: repeated '{criterion}' x{times} - {evidence}");
        if (times != 3 || state.Log is not { } log) return;
        var lesson = new FactoryLesson
        {
            Kind = "rule", Name = criterion, Status = "pending",
            Description = $"The grader found this broken in {times} turns in this folder",
            Content = $"{criterion} - check this before saying a task is done. (The grader found it broken {times} times; the latest: {evidence})",
        };
        SessionLessons.Record(state.Info.Folder, state.Info.Id, lesson);
        LessonCard(state, log, lesson);
    }

    // the card: kind, name, description and text; Save, or Edit... / Dismiss... in the panel above the box - a text box
    // inside the transcript takes no typing (the transcript is a read-only RichTextBox; measured: keys did not reach it)
    private void LessonCard(SessionState state, TurnLog log, FactoryLesson lesson)
    {
        StackPanel body = Card(state, log, "Lesson - " + KindLabel(lesson.Kind));
        void Fill()
        {
            while (body.Children.Count > 1) body.Children.RemoveAt(1);
            body.Children.Add(Say(KindHelp(lesson.Kind)));
            body.Children.Add(new TextBlock { Text = lesson.Name, FontWeight = FontWeights.Bold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(Say(lesson.Description));
            body.Children.Add(new TextBlock { Text = lesson.Content, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = (Brush)FindResource("InkBrush") });
            body.Children.Add(Buttons(
                CardButton("Save", true, () => Logged(SaveLesson(state, lesson, body, lesson.Name, lesson.Description, lesson.Content), "save lesson")),
                CardButton("Edit...", false, () => EditLesson(state, lesson, body, Fill)),
                CardButton("Dismiss...", false, () => DismissLesson(state, lesson, body))));
        }
        Fill();
    }

    // the lesson's name, description and text, editable in the panel above the box; Save writes the edited one
    private void EditLesson(SessionState state, FactoryLesson lesson, StackPanel body, Action refill)
    {
        TextBox name = Field(lesson.Name, "Lesson name", false);
        TextBox description = Field(lesson.Description, "Lesson description", false);
        TextBox content = Field(lesson.Content, "Lesson text", true);
        ShowAction("Edit the lesson - " + KindLabel(lesson.Kind), [name, description, content, Buttons(
            CardButton("Save", true, () =>
            {
                CloseAction();
                Logged(SaveLesson(state, lesson, body, name.Text, description.Text, content.Text), "save lesson");
            }),
            CardButton("Keep editing later", false, () =>
            {
                (lesson.Name, lesson.Description, lesson.Content) = (name.Text.Trim(), description.Text.Trim(), content.Text.Trim());
                CloseAction();
                refill();
            }))]);
        content.Focus();
    }

    // dismissing asks why (optional): the next drafts for this folder read it
    private void DismissLesson(SessionState state, FactoryLesson lesson, StackPanel body)
    {
        TextBox reason = Field("", "Why dismiss (optional)", false);
        ShowAction("Dismiss this lesson? It will not be proposed again here.", [reason, Buttons(
            CardButton("Dismiss", true, () =>
            {
                CloseAction();
                lesson.Status = "dismissed";
                lesson.Feedback = reason.Text.Trim();
                SessionLessons.Record(state.Info.Folder, state.Info.Id, lesson);
                Done(body, "Dismissed" + (lesson.Feedback.Length > 0 ? ": " + lesson.Feedback : "") + " - not proposed again here.", null);
            }),
            CardButton("Cancel", false, CloseAction))]);
        reason.Focus();
    }

    private static TextBox Field(string text, string name, bool multi)
    {
        var box = new TextBox
        {
            Text = text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = multi, FontSize = 13, Margin = new Thickness(0, 0, 0, 6),
            MaxHeight = multi ? 160 : 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private async Task SaveLesson(SessionState state, FactoryLesson lesson, StackPanel body, string name, string description, string content)
    {
        if (!SessionLessons.TryBeginSave(lesson)) return;
        lesson.Name = name.Trim().Length > 0 ? name.Trim() : lesson.Name;
        lesson.Description = description.Trim();
        lesson.Content = content.Trim();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            string? memoryDir = lesson.Kind == "memory" ? await CliSetup.MemoryDirAsync(state.Info.Folder) : null;
            string file = SessionLessons.Save(lesson, memoryDir, home);
            SessionLessons.Record(state.Info.Folder, state.Info.Id, lesson);
            Log($"lesson saved: {lesson.Kind} {lesson.Name} -> {file}");
            Done(body, "Saved to " + file, () =>
            {
                SessionLessons.Undo(lesson);
                SessionLessons.Record(state.Info.Folder, state.Info.Id, lesson);
                Log($"lesson undone: {lesson.Kind} {lesson.Name}");
                Done(body, "Undone - " + file + " is as it was.", null);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or TimeoutException)
        {
            lesson.Status = "pending";
            body.Children.Add(Say("Could not save: " + ex.Message, false));
        }
    }

    // the card after the decision: one line, and Undo for a saved lesson
    private void Done(StackPanel body, string line, Action? undo)
    {
        while (body.Children.Count > 1) body.Children.RemoveAt(1);
        body.Children.Add(Say(line, false));
        if (undo != null) body.Children.Add(Buttons(CardButton("Undo", false, undo)));
    }

    private static string KindLabel(string kind) => kind switch
    {
        "memory" => "a fact about this project",
        "skill" => "a procedure that worked",
        _ => "a rule from now on",
    };

    private static string KindHelp(string kind) => kind switch
    {
        "memory" => "Saved in this folder's project memory - Claude reads it in every session here.",
        "skill" => "Saved as a skill in ~/.claude/skills - Claude uses it whenever the task fits.",
        _ => "Added to the end of ~/.claude/CLAUDE.md - Claude follows it in every session.",
    };
}
