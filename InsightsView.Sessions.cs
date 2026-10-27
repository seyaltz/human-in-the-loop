using System;
using System.IO;
using System.Linq;
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
