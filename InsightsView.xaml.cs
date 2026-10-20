using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// What the factory learned while building features: every lesson its runs kept (memory notes, rules, skills, hooks),
/// per repo, newest first, with the run it came from. Dismissed and undone lessons are left out.
/// </summary>
public partial class InsightsView : UserControl
{
    private bool _listening;

    public InsightsView() => InitializeComponent();

    public void Refresh()
    {
        if (!_listening)
        {
            _listening = true;
            Factory.Changed += _ => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
        }
        Body.Children.Clear();
        var kept = Factory.Runs
            .SelectMany(run => run.Lessons.Where(l => l.Status is "saved" or "applied" or "pending").Select(lesson => (run, lesson)))
            .OrderByDescending(x => x.run.Created)
            .ToList();
        int repos = kept.Select(x => x.run.Repo).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int runs = kept.Select(x => x.run.Id).Distinct().Count();
        Body.Children.Add(new TextBlock
        {
            Text = kept.Count == 0
                ? "Nothing learned yet. Every factory run ends by keeping what the next run needs - how to start the service fast, where the logic lives, the language and framework facts it had to look up - and it shows up here."
                : $"{Count(kept.Count, "insight")} from {Count(runs, "factory run")} in {Count(repos, "repo")} - what the next run there starts with.",
            Style = (Style)Res("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 12),
        });
        foreach (var repo in kept.GroupBy(x => x.run.Repo, StringComparer.OrdinalIgnoreCase))
        {
            var list = new StackPanel();
            foreach ((FactoryRun run, FactoryLesson lesson) in repo) list.Children.Add(Insight(run, lesson));
            string name = repo.Key.Length > 0 ? Path.GetFileName(repo.Key.TrimEnd('\\', '/')) : "(no repo)";
            Body.Children.Add(Card(name, $"{Count(repo.Count(), "insight")} · {repo.Key}", list));
        }
    }

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    private static UIElement Insight(FactoryRun run, FactoryLesson lesson)
    {
        string kind = lesson.Kind switch { "memory" => "memory", "claude-md" => "rule", "hook" => "hook", _ => "skill" };
        string state = lesson.Status switch { "saved" => "in the repo's memory", "applied" => "applied", _ => "waits for your OK" };
        var head = new TextBlock { TextWrapping = TextWrapping.Wrap };
        head.Inlines.Add(new Run(kind.ToUpperInvariant() + "  ") { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)Res("VioletBrush") });
        head.Inlines.Add(new Run(lesson.Name) { FontWeight = FontWeights.Bold, FontSize = 13.5 });
        head.Inlines.Add(new Run("  ·  " + state) { FontSize = 11.5, Foreground = (Brush)Res("InkSoftBrush") });
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = lesson.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 4) });
        body.Children.Add(new TextBlock
        {
            Text = lesson.Content.Trim(), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)Res("InkSoftBrush"),
            FontFamily = lesson.Kind is "hook" ? (FontFamily)Res("MonoFont") : SystemFonts.MessageFontFamily,
        });
        body.Children.Add(new TextBlock
        {
            Text = $"from \"{(run.Title.Length > 0 ? run.Title : run.Task)}\" · {run.Created:yyyy-MM-dd HH:mm}",
            Style = (Style)Res("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        return body;
    }

    /// <summary>A flat card with a bold title and a small note on the right.</summary>
    private static Border Card(string title, string note, UIElement content)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var right = new TextBlock { Text = note, Style = (Style)Res("Caption"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 15 });
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(content);
        return new Border { Style = (Style)Res("Card"), Child = body, Margin = new Thickness(0, 0, 0, 14) };
    }

    private static object Res(string key) => Application.Current.FindResource(key);
}
