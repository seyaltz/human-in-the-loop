using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>What Sessions learned: the lessons its panes proposed, kept or waiting, per folder, with Undo for a kept one.</summary>
public partial class InsightsView
{
    private void AddSessionLessons()
    {
        var lessons = SessionLessons.All().Where(l => l.Lesson.Status is "saved" or "pending").ToList();
        if (lessons.Count == 0) return;
        Body.Children.Add(new TextBlock
        {
            Text = "From Sessions", FontWeight = FontWeights.Bold, FontSize = 17, Margin = new Thickness(2, 10, 0, 4),
        });
        Body.Children.Add(new TextBlock
        {
            Text = $"{Count(lessons.Count, "lesson")} your Sessions panes proposed after turns that needed retries or a correction. Kept ones are in your Claude Code setup; waiting ones are under their turn.",
            Style = (Style)Res("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 12),
        });
        foreach (var folder in lessons.GroupBy(l => l.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var list = new StackPanel();
            foreach (SessionLessons.SavedLesson saved in folder) list.Children.Add(SessionLesson(saved));
            Body.Children.Add(Card(Path.GetFileName(folder.Key.TrimEnd('\\', '/')), $"{Count(folder.Count(), "lesson")} · {folder.Key}", list));
        }
    }

    private StackPanel? _drafts;

    // where the time of the user's Claude Code turns went, week by week (read from their own session files)
    private void AddTime()
    {
        var content = new StackPanel();
        SessionStats.Data? data = SessionStats.Current;
        if (data == null)
        {
            content.Children.Add(Caption("Reading your sessions..."));
            _ = SessionStats.RefreshAsync();
        }
        else
        {
            List<SessionStats.WeekStats> weeks = data.Weeks(4);
            if (weeks.Count == 0) content.Children.Add(Caption("No Claude Code turns in the last weeks."));
            foreach (SessionStats.WeekStats week in weeks) content.Children.Add(WeekRow(week));
            if (weeks.Count > 0)
            {
                SessionStats.WeekStats latest = weeks[0];
                string waits = string.Join(" · ", latest.TopWaits(5).Select(w => $"{Short(w.Key)} {w.Hours * 60:0} min"));
                string failing = string.Join(" · ", latest.TopFailing(5).Select(f => $"{Short(f.Key)} ×{f.Failed}"));
                if (waits.Length > 0) content.Children.Add(Caption("Waited on most, this week: " + waits));
                if (failing.Length > 0) content.Children.Add(Caption("Failed most, this week: " + failing));
            }
        }
        var learn = new Button
        {
            Content = "Learn from my sessions", Style = (Style)Res("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0), IsEnabled = data != null,
            ToolTip = "Find the procedures you re-run in many sessions and draft a skill for each - you review every one",
        };
        _drafts = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        learn.Click += async (_, _) =>
        {
            learn.IsEnabled = false;
            await Learn(_drafts);
            learn.IsEnabled = true;
        };
        content.Children.Add(learn);
        content.Children.Add(_drafts);
        Body.Children.Add(Card("Where your Claude Code time goes", "your own sessions · last 4 weeks", content));
    }

    // one week: its hours as a bar (waiting, tools, the model, you), then failures, interruptions, corrections
    private static UIElement WeekRow(SessionStats.WeekStats w)
    {
        double otherTools = Math.Max(0, w.ToolH - w.WaitH - w.UserH);
        (string Label, double Hours, string Brush)[] parts =
        [
            ("waiting", w.WaitH, "CoralInkBrush"), ("tools", otherTools, "VioletBrush"), ("model", w.ModelH, "VioletRingBrush"), ("you", w.UserH, "MutedBrush"),
        ];
        double total = parts.Sum(p => p.Hours);
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new TextBlock
        {
            Text = $"Week of {w.Start:d MMM} · {w.ActiveH:0.#} h active · {(w.Calls > 0 ? 100.0 * w.Failed / w.Calls : 0):0}% of {w.Calls} calls failed · {w.Interrupts} stops · {w.Corrections} corrections",
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        var bar = new Grid { Height = 12, Margin = new Thickness(0, 4, 0, 2) };
        foreach ((string label, double hours, string brush) in parts.Where(p => p.Hours > 0))
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(hours, GridUnitType.Star) });
            var piece = new Border { Background = (Brush)Res(brush), ToolTip = $"{label}: {hours:0.#} h" };
            Grid.SetColumn(piece, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(piece);
        }
        if (total > 0) row.Children.Add(bar);
        row.Children.Add(Caption(string.Join("  ·  ", parts.Select(p => $"{p.Label} {p.Hours:0.#} h"))));
        return row;
    }

    // a command family's name for a one-line list (a PowerShell line can start with a long path)
    private static string Short(string key) => key.Length <= 32 ? key : key[..31] + "…";

    private static TextBlock Caption(string text) => new()
    {
        Text = text, Style = (Style)Res("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2),
    };

    // the procedures the user re-runs across sessions, minus what a skill already covers: a skill draft for each, to review
    private async Task Learn(StackPanel drafts)
    {
        drafts.Children.Clear();
        drafts.Children.Add(Caption("Looking at what you repeat across sessions..."));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<SessionStats.CommandFamily> families = SessionStats.Current?.Families(3) ?? [];
        IReadOnlyList<SlashCommand> commands;
        try
        {
            commands = await SlashCommands.For(home);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            commands = [];
        }
        List<SessionStats.CommandFamily> open = [.. SessionStats.Uncovered(families, commands.Select(c => c.Name + ": " + c.Description)).Take(8)];
        if (open.Count == 0)
        {
            drafts.Children.Clear();
            drafts.Children.Add(Caption("Nothing repeated enough that a skill does not already cover."));
            return;
        }
        string list = string.Join("\n\n", open.Select(f =>
            $"- `{f.Key}`: run in {f.Sessions} sessions, {f.Calls} times, failed {f.Failed}. Folders: {string.Join(", ", f.Folders)}.\n  Examples:\n" +
            string.Join("\n", f.Examples.Select(e => "    " + e.Replace("\n", " ")))));
        string prompt = $$"""
            A developer re-runs these command families across many separate Claude Code sessions, so each time the agent
            works the procedure out again. For each one that is a real, repeatable procedure (not a one-off), draft a
            Claude Code skill that does it the proven way: when to use it, the steps with the exact commands (keep the
            developer's own flags and paths from the examples), and the traps the failures point to. Skip the rest.

            {{list}}

            Reply with ONLY a JSON array, one object per skill (empty if none is worth it):
            [{"kind": "skill", "name": "short-kebab-name", "description": "when to use it, one line", "content": "the skill body in markdown"}]
            """;
        List<FactoryLesson> skills = await SessionLessons.AskForLessons(prompt, home, TimeSpan.FromMinutes(5), 8);
        drafts.Children.Clear();
        if (skills.Count == 0)
        {
            drafts.Children.Add(Caption("No skill worth drafting came back."));
            return;
        }
        drafts.Children.Add(Caption($"{skills.Count} skill draft{(skills.Count == 1 ? "" : "s")} - edit, then Save (to ~/.claude/skills) or Dismiss."));
        foreach (FactoryLesson skill in skills.Where(s => s.Kind == "skill"))
        {
            SessionLessons.Record("(all sessions)", "", skill);
            drafts.Children.Add(Draft(skill, home));
        }
    }

    // a skill draft, editable here (Insights is no read-only document)
    private UIElement Draft(FactoryLesson skill, string home)
    {
        var body = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
        TextBox Field(string text, string name, bool multi)
        {
            var box = new TextBox { Text = text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = multi, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4), MaxHeight = multi ? 220 : 50, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Automation.AutomationProperties.SetName(box, name);
            return box;
        }
        TextBox name = Field(skill.Name, "Skill name", false), description = Field(skill.Description, "Skill description", false), content = Field(skill.Content, "Skill text", true);
        body.Children.Add(name);
        body.Children.Add(description);
        body.Children.Add(content);
        var result = Caption("");
        var save = new Button { Content = "Save", Style = (Style)Res("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        var dismiss = new Button { Content = "Dismiss", Style = (Style)Res("SecondaryButton") };
        save.Click += (_, _) =>
        {
            (skill.Name, skill.Description, skill.Content) = (name.Text.Trim(), description.Text.Trim(), content.Text.Trim());
            try
            {
                string file = SessionLessons.Save(skill, null, home);
                SessionLessons.Record("(all sessions)", "", skill);
                result.Text = "Saved to " + file + " - Undo it from the list below.";
                save.IsEnabled = dismiss.IsEnabled = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result.Text = "Could not save: " + ex.Message;
            }
        };
        dismiss.Click += (_, _) =>
        {
            skill.Status = "dismissed";
            SessionLessons.Record("(all sessions)", "", skill);
            result.Text = "Dismissed.";
            save.IsEnabled = dismiss.IsEnabled = false;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(save);
        buttons.Children.Add(dismiss);
        body.Children.Add(buttons);
        body.Children.Add(result);
        return body;
    }

    private UIElement SessionLesson(SessionLessons.SavedLesson saved)
    {
        FactoryLesson lesson = saved.Lesson;
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var head = new TextBlock { TextWrapping = TextWrapping.Wrap };
        head.Inlines.Add(new Run(lesson.Kind.ToUpperInvariant() + "  ") { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)Res("VioletBrush") });
        head.Inlines.Add(new Run(lesson.Name) { FontWeight = FontWeights.Bold, FontSize = 13.5 });
        head.Inlines.Add(new Run("  ·  " + (lesson.Status == "saved" ? "kept in " + lesson.File : "waits under its turn")) { FontSize = 11.5, Foreground = (Brush)Res("InkSoftBrush") });
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = lesson.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 4) });
        body.Children.Add(new TextBlock { Text = lesson.Content, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)Res("InkSoftBrush") });
        if (lesson.Status == "saved")
        {
            var undo = new Button { Content = "Undo", Style = (Style)Res("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            undo.Click += (_, _) =>
            {
                SessionLessons.Undo(lesson);
                SessionLessons.Record(saved.Folder, saved.Session, lesson);
                Refresh();
            };
            body.Children.Add(undo);
        }
        return body;
    }
}
