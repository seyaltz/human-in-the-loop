using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DevBreak;

/// <summary>The stats screen: rank and XP, streak and today's goal, the last 14 days, how each topic goes, and badges.</summary>
public partial class StatsView : UserControl
{
    private const int Days = 14;
    private const int WeakestShown = 5;

    // a line icon (24x24 grid) for every badge, drawn on a round sticker
    private static readonly Dictionary<string, string> BadgeIcons = new()
    {
        ["first-answer"] = "M4,20 L5,15 L15,5 L19,9 L9,19 Z M13,7 L17,11",
        ["perfect"] = "M12,3 L14.6,8.6 L20.6,9.2 L16,13.2 L17.4,19.2 L12,16 L6.6,19.2 L8,13.2 L3.4,9.2 L9.4,8.6 Z",
        ["hat-trick"] = "M3,18 H21 M6,18 V9 C6,6 18,6 18,9 V18 M6,13 H18",
        ["century"] = "M4,7 V17 M8,7 H12 V17 H8 Z M15,7 H19 V17 H15 Z",
        ["polyglot"] = "M12,3 A9,9 0 1 1 11.99,3 M3,12 H21 M12,3 C8,7 8,17 12,21 M12,3 C16,7 16,17 12,21",
        ["first-lesson"] = "M4,5 C7,4 10,4.5 12,6 C14,4.5 17,4 20,5 V19 C17,18 14,18.5 12,20 C10,18.5 7,18 4,19 Z M12,6 V20",
        ["scholar"] = "M2,9 L12,4 L22,9 L12,14 Z M6,11 V16 C9,18.5 15,18.5 18,16 V11 M22,9 V14",
        ["graduate"] = "M7,4 H17 V9 A5,5 0 0 1 7,9 Z M7,6 H4 A3,3 0 0 0 7,10 M17,6 H20 A3,3 0 0 1 17,10 M12,14 V18 M8,20 H16",
        ["streak-5"] = "M12,2 C13,7 18,9 18,15 A6,6 0 0 1 6,15 C6,11 9,10 9,6 C10.5,7.5 11,9 11,10 C12.5,8.5 12.5,5 12,2 Z",
        ["streak-14"] = "M13,2 L4,14 H11 L10,22 L20,9 H13 Z",
        ["goal-7"] = "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 M12,7.5 A4.5,4.5 0 1 1 11.99,7.5 M12,11 A1,1 0 1 1 11.99,11",
        ["night-owl"] = "M20,14.5 A8,8 0 1 1 9.5,4 A6.5,6.5 0 0 0 20,14.5 Z",
        ["early-bird"] = "M12,8 A4,4 0 1 1 11.99,8 M12,2 V4 M12,20 V22 M2,12 H4 M20,12 H22 M4.9,4.9 L6.3,6.3 M17.7,17.7 L19.1,19.1 M4.9,19.1 L6.3,17.7 M17.7,6.3 L19.1,4.9",
    };

    private static readonly string[] StickerColors = ["#D4F53C", "#FFD43B", "#FFB4A2", "#CFC4FF", "#7CF2C8"];

    public StatsView() => InitializeComponent();

    public void Refresh()
    {
        Body.Children.Clear();
        Body.Children.Add(Row(RankCard(), StreakCard()));
        Body.Children.Add(Row(
            Card("Last 14 days", "XP per day", LastDays()),
            Card("By topic", "average score", ByTopic())));
        Body.Children.Add(Card("Weakest topics", "asked more often until they improve", Weakest()));
        Body.Children.Add(Card("Badges", $"{Game.Badges.Count(b => Game.HasBadge(b.Id))} of {Game.Badges.Count} earned", BadgeWall()));
    }

    private static UIElement RankCard()
    {
        int xp = Game.Xp;
        Rank rank = Game.RankOf(xp);
        Rank? next = Game.NextRank(xp);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "YOUR RANK", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Opacity = 0.85 });

        var title = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        title.Children.Add(new TextBlock
        {
            Text = rank.Name, Style = (Style)Res("Heading"), FontSize = 42, Foreground = Brushes.White, Margin = new Thickness(0, 0, 12, 0),
        });
        title.Children.Add(new TextBlock
        {
            Text = next == null ? "top rank!" : $"{next.From - xp:N0} XP to {next.Name}",
            Style = (Style)Res("HandNote"), Foreground = (Brush)Res("MarkerBrush"), RenderTransform = null,
        });
        content.Children.Add(title);

        content.Children.Add(new ProgressBar
        {
            Style = (Style)Res("ThinBar"), Height = 12, Foreground = (Brush)Res("MarkerBrush"),
            Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            Value = next == null ? 1 : (double)(xp - rank.From) / (next.From - rank.From),
        });

        var ladder = new UniformGrid { Rows = 1, Margin = new Thickness(0, 8, 0, 0) };
        foreach (Rank step in Game.Ranks)
        {
            ladder.Children.Add(new TextBlock
            {
                Text = step.Name, FontSize = 12, Foreground = Brushes.White,
                FontWeight = step == rank ? FontWeights.Bold : FontWeights.Normal, Opacity = step == rank ? 1 : 0.7,
            });
        }
        content.Children.Add(ladder);
        content.Children.Add(new TextBlock { Text = $"{xp:N0} XP in total", FontSize = 12.5, Foreground = Brushes.White, Opacity = 0.85, Margin = new Thickness(0, 6, 0, 0) });

        return new ContentControl { Style = (Style)Res("ShadowCard"), Background = (Brush)Res("VioletBrush"), Content = content };
    }

    private static UIElement StreakCard()
    {
        int streak = Game.Streak();
        var content = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Path
        {
            Data = (Geometry)Res("FlameIcon"), Fill = (Brush)Res("CoralBrush"), Stroke = (Brush)Res("InkBrush"), StrokeThickness = 1.5,
            Stretch = Stretch.Uniform, Width = 24, Height = 28, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        title.Children.Add(new TextBlock { Text = streak == 1 ? "1-day streak" : $"{streak}-day streak", Style = (Style)Res("Heading"), FontSize = 28 });
        content.Children.Add(title);
        content.Children.Add(new TextBlock { Text = $"Best: {Game.BestStreak()} day(s) · goals met: {Game.GoalsMet}", Style = (Style)Res("Caption"), Margin = new Thickness(0, 2, 0, 12) });

        content.Children.Add(new TextBlock { Text = "TODAY'S GOAL", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)Res("InkSoftBrush"), Margin = new Thickness(0, 0, 0, 6) });
        int questions = Game.QuestionsOn(DateTime.Today);
        int lessons = Game.LessonsOn(DateTime.Today);
        content.Children.Add(GoalLine("Questions", questions, Game.GoalQuestions));
        content.Children.Add(GoalLine("Lesson", lessons, Game.GoalLessons));
        bool met = questions >= Game.GoalQuestions && lessons >= Game.GoalLessons;
        content.Children.Add(new TextBlock
        {
            Text = met ? "goal done, nice!" : $"+{Game.GoalBonusXp} XP when done", Style = (Style)Res("HandNote"), FontSize = 20,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return new ContentControl { Style = (Style)Res("ShadowCard"), Content = content };
    }

    /// <summary>A tick box (ticked when the part is done) with "Questions 2 / 3".</summary>
    private static UIElement GoalLine(string name, int done, int goal)
    {
        bool met = done >= goal;
        var box = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1.8), Margin = new Thickness(0, 0, 10, 0),
            BorderBrush = (Brush)Res("InkBrush"), Background = (Brush)Res(met ? "MarkerBrush" : "CardBrush"),
        };
        if (met)
        {
            box.Child = new Path
            {
                Data = (Geometry)Res("CheckIcon"), Style = (Style)Res("IconPath"), Width = 12, Height = 12, Stroke = (Brush)Res("InkBrush"), StrokeThickness = 3,
            };
        }
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        line.Children.Add(box);
        var text = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run(name + "  "));
        text.Inlines.Add(new System.Windows.Documents.Run($"{Math.Min(done, goal)} / {goal}") { FontWeight = FontWeights.Bold });
        line.Children.Add(text);
        return line;
    }

    private static UIElement LastDays()
    {
        DateTime[] days = Enumerable.Range(0, Days).Select(i => DateTime.Today.AddDays(i - Days + 1)).ToArray();
        int[] xp = days.Select(Game.XpOn).ToArray();
        int max = Math.Max(1, xp.Max());
        var chart = new DockPanel();

        var axis = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var today = new TextBlock { Text = "today", Style = (Style)Res("Eyebrow"), FontSize = 11 };
        DockPanel.SetDock(today, Dock.Right);
        axis.Children.Add(today);
        axis.Children.Add(new TextBlock { Text = days[0].ToString("MMM d"), Style = (Style)Res("Eyebrow"), FontSize = 11 });
        DockPanel.SetDock(axis, Dock.Bottom);
        chart.Children.Add(axis);

        var bars = new UniformGrid { Rows = 1, Columns = Days, Height = 110 };
        for (int i = 0; i < Days; i++)
        {
            bool isToday = i == Days - 1;
            var bar = new Border
            {
                Height = Math.Max(3, 104.0 * xp[i] / max), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 0),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                Background = (Brush)Res(xp[i] == 0 ? "RuleBrush" : isToday ? "MarkerBrush" : "VioletBrush"),
                BorderBrush = (Brush)Res("InkBrush"), BorderThickness = isToday && xp[i] > 0 ? new Thickness(1.5, 1.5, 1.5, 0) : new Thickness(0),
                ToolTip = $"{days[i]:ddd d MMM}: {xp[i]} XP",
            };
            bars.Children.Add(bar);
        }
        var baseline = new Border { BorderBrush = (Brush)Res("InkBrush"), BorderThickness = new Thickness(0, 0, 0, 1.5), Child = bars };
        chart.Children.Add(baseline);
        return chart;
    }

    private static UIElement ByTopic()
    {
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        int row = 0;
        foreach (Category category in Category.All)
        {
            int answered = Game.Events.Count(e => e.Kind == Game.QuestionKind && e.Tab == category.Name);
            int[] scores = Game.ScoresFor(e => e.Tab == category.Name);
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(table, row, 0, new TextBlock
            {
                Text = category.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center, ToolTip = $"{answered} question(s)",
            });
            if (scores.Length == 0)
            {
                AddCell(table, row, 1, new TextBlock { Text = "no scores yet", Style = (Style)Res("Caption"), VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                double average = scores.Average();
                AddCell(table, row, 1, new ProgressBar
                {
                    Style = (Style)Res("ThinBar"), Value = average / 10, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Res(average < 5 ? "CoralBrush" : "VioletBrush"),
                    ToolTip = $"average {average:0.0}/10 from {scores.Length} checked",
                });
                AddCell(table, row, 2, new TextBlock
                {
                    Text = $"{average:0.0}", Style = (Style)Res("Eyebrow"), FontSize = 13, Foreground = (Brush)Res("InkBrush"),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                });
            }
            row++;
        }
        return table;
    }

    private static UIElement Weakest()
    {
        var weakest = Game.Events
            .Where(e => e.Kind == Game.QuestionKind && e.Score != null)
            .GroupBy(e => e.Topic)
            .Select(g => (Topic: g.Key, Average: g.Average(e => e.Score!.Value), Count: g.Count()))
            .OrderBy(t => t.Average)
            .Take(WeakestShown)
            .ToList();
        if (weakest.Count == 0) return new TextBlock { Text = "Check a few answers to find your weak spots.", Style = (Style)Res("Caption") };

        var list = new StackPanel();
        foreach ((string topic, double average, int count) in weakest)
        {
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var weight = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 2), Background = (Brush)Res("CoralTintBrush"),
                Child = new TextBlock { Text = $"asked x{Game.TopicWeight(topic):0.0}", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Res("CoralInkBrush") },
            };
            DockPanel.SetDock(weight, Dock.Right);
            line.Children.Add(weight);
            var text = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            text.Inlines.Add(new System.Windows.Documents.Run(topic));
            text.Inlines.Add(new System.Windows.Documents.Run($"   {average:0.0}/10 ({count})") { Foreground = (Brush)Res("InkSoftBrush"), FontSize = 12.5 });
            line.Children.Add(text);
            list.Children.Add(line);
        }
        return list;
    }

    private static UIElement BadgeWall()
    {
        var wall = new WrapPanel();
        int i = 0;
        foreach (Badge badge in Game.Badges)
        {
            bool earned = Game.HasBadge(badge.Id);
            var sticker = new Border
            {
                Width = 58, Height = 58, CornerRadius = new CornerRadius(29), BorderThickness = new Thickness(2),
                BorderBrush = (Brush)Res(earned ? "InkBrush" : "LineBrush"),
                Background = earned ? (Brush)new BrushConverter().ConvertFromString(StickerColors[i % StickerColors.Length])! : (Brush)Res("PaperBrush"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(earned ? (i % 2 == 0 ? -6 : 5) : 0),
                Child = new Path
                {
                    Data = Geometry.Parse(BadgeIcons.GetValueOrDefault(badge.Id, BadgeIcons["perfect"])), Style = (Style)Res("IconPath"),
                    Width = 26, Height = 26, Stroke = (Brush)Res(earned ? "InkBrush" : "MutedBrush"),
                },
            };
            var tile = new StackPanel { Width = 92, Margin = new Thickness(0, 4, 6, 8), ToolTip = $"{badge.Description}{(earned ? "" : " (locked)")}" };
            tile.Children.Add(sticker);
            tile.Children.Add(new TextBlock
            {
                Text = badge.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0), Foreground = (Brush)Res(earned ? "InkBrush" : "InkSoftBrush"),
            });
            System.Windows.Automation.AutomationProperties.SetName(tile, $"{badge.Name}: {badge.Description}, {(earned ? "earned" : "locked")}");
            wall.Children.Add(tile);
            if (earned) i++;
        }
        return wall;
    }

    /// <summary>Two cards side by side.</summary>
    private static UIElement Row(UIElement left, UIElement right)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
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

    private static void AddCell(Grid table, int row, int column, UIElement cell)
    {
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        table.Children.Add(cell);
    }

    private static object Res(string key) => Application.Current.FindResource(key);
}
