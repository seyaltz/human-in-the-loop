using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DevBreak;

/// <summary>The stats screen: rank and XP, streak, today's goal, badges, the last 14 days and how each topic goes.</summary>
public partial class StatsView : UserControl
{
    private const int Days = 14;
    private const int WeakestShown = 5;

    public StatsView() => InitializeComponent();

    public void Refresh()
    {
        Body.Children.Clear();
        Body.Children.Add(Summary());
        Body.Children.Add(Heading($"Badges ({Game.Badges.Count(b => Game.HasBadge(b.Id))} of {Game.Badges.Count})"));
        Body.Children.Add(BadgeWall());
        Body.Children.Add(Heading($"XP in the last {Days} days"));
        Body.Children.Add(Card(LastDays()));
        Body.Children.Add(Heading("By topic"));
        Body.Children.Add(Card(ByTopic()));
        Body.Children.Add(Heading("Weakest topics - asked more often until they improve"));
        Body.Children.Add(Card(Weakest()));
    }

    private static UIElement Summary()
    {
        int xp = Game.Xp;
        Rank rank = Game.RankOf(xp);
        Rank? next = Game.NextRank(xp);
        var rankCard = new StackPanel();
        rankCard.Children.Add(Pill(rank.Name.ToUpperInvariant(), rank.Color));
        rankCard.Children.Add(Big($"{xp:N0} XP"));
        rankCard.Children.Add(new ProgressBar
        {
            Height = 6, Maximum = 1, Margin = new Thickness(0, 6, 0, 4),
            Value = next == null ? 1 : (double)(xp - rank.From) / (next.From - rank.From),
        });
        rankCard.Children.Add(Small(next == null ? "Top rank reached" : $"{next.From - xp:N0} XP to {next.Name}"));

        var streakCard = new StackPanel();
        streakCard.Children.Add(Small("Daily streak"));
        streakCard.Children.Add(Big($"\U0001F525 {Game.Streak()} day(s)"));
        streakCard.Children.Add(Small($"Best: {Game.BestStreak()} day(s)"));

        int questions = Game.QuestionsOn(DateTime.Today);
        int lessons = Game.LessonsOn(DateTime.Today);
        var todayCard = new StackPanel();
        todayCard.Children.Add(Small($"Today's goal (+{Game.GoalBonusXp} XP)"));
        todayCard.Children.Add(Big($"\U0001F3AF {Math.Min(questions, Game.GoalQuestions)}/{Game.GoalQuestions} questions · "
                                   + $"{Math.Min(lessons, Game.GoalLessons)}/{Game.GoalLessons} lesson"));
        todayCard.Children.Add(Small($"Goals met so far: {Game.GoalsMet}"));

        var grid = new UniformGrid { Columns = 3 };
        foreach (UIElement content in new UIElement[] { rankCard, streakCard, todayCard })
        {
            Border card = Card(content);
            card.Margin = new Thickness(0, 0, 10, 0);
            grid.Children.Add(card);
        }
        return grid;
    }

    private static UIElement BadgeWall()
    {
        var wall = new WrapPanel();
        foreach (Badge badge in Game.Badges)
        {
            bool earned = Game.HasBadge(badge.Id);
            var tile = new StackPanel { Width = 150 };
            tile.Children.Add(new TextBlock { Text = badge.Icon, FontSize = 26, FontFamily = new FontFamily("Segoe UI Emoji") });
            tile.Children.Add(new TextBlock { Text = badge.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
            tile.Children.Add(new TextBlock { Text = badge.Description, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            Border card = Card(tile);
            card.Margin = new Thickness(0, 0, 8, 8);
            card.Opacity = earned ? 1 : 0.35;
            card.ToolTip = earned ? "Unlocked" : "Locked";
            wall.Children.Add(card);
        }
        return wall;
    }

    private static UIElement LastDays()
    {
        DateTime[] days = Enumerable.Range(0, Days).Select(i => DateTime.Today.AddDays(i - Days + 1)).ToArray();
        int[] xp = days.Select(Game.XpOn).ToArray();
        int max = Math.Max(1, xp.Max());
        var bars = new UniformGrid { Rows = 1, Columns = Days, Height = 120 };
        for (int i = 0; i < Days; i++)
        {
            var column = new DockPanel { Margin = new Thickness(3, 0, 3, 0), ToolTip = $"{days[i]:ddd d MMM}: {xp[i]} XP" };
            var label = new TextBlock { Text = days[i].ToString("dd"), FontSize = 10, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center };
            DockPanel.SetDock(label, Dock.Bottom);
            column.Children.Add(label);
            column.Children.Add(new Rectangle
            {
                Height = Math.Max(2, 96.0 * xp[i] / max), VerticalAlignment = VerticalAlignment.Bottom, RadiusX = 3, RadiusY = 3,
                Fill = (Brush)Application.Current.FindResource("AccentFillColorDefaultBrush"),
            });
            bars.Children.Add(column);
        }
        return bars;
    }

    private static UIElement ByTopic()
    {
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach (Category category in Category.All)
        {
            int answered = Game.Events.Count(e => e.Kind == Game.QuestionKind && e.Tab == category.Name);
            int[] scores = Game.ScoresFor(e => e.Tab == category.Name);
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(table, row, 0, new TextBlock { Text = category.Name, Margin = new Thickness(0, 3, 0, 3) });
            AddCell(table, row, 1, Small($"{answered} question(s)"));
            AddCell(table, row, 2, scores.Length == 0
                ? Small("no scores yet")
                : ScoreBar(scores.Average(), $"average {scores.Average():0.0}/10 from {scores.Length} checked"));
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
        if (weakest.Count == 0) return Small("Check a few answers to find your weak spots.");

        var list = new StackPanel();
        foreach ((string topic, double average, int count) in weakest)
        {
            var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var weight = Small($"asked x{Game.TopicWeight(topic):0.0}");
            DockPanel.SetDock(weight, Dock.Right);
            line.Children.Add(weight);
            line.Children.Add(new TextBlock { Text = $"{topic}  -  {average:0.0}/10 ({count})" });
            list.Children.Add(line);
        }
        return list;
    }

    private static UIElement ScoreBar(double average, string text)
    {
        var bar = new Grid { Height = 18, Margin = new Thickness(0, 3, 0, 3), ToolTip = text };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(average, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10 - average, GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(average >= 8 ? Color.FromRgb(0x2E, 0x7D, 0x32)
                : average >= 5 ? Color.FromRgb(0xEF, 0x6C, 0x00)
                : Color.FromRgb(0xC6, 0x28, 0x28)),
            Child = new TextBlock { Text = $" {average:0.0}", Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
        };
        bar.Children.Add(fill);
        return bar;
    }

    private static void AddCell(Grid table, int row, int column, UIElement cell)
    {
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        table.Children.Add(cell);
    }

    private static Border Card(UIElement content) =>
        new() { Style = (Style)Application.Current.FindResource("Card"), Child = content };

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8) };

    private static TextBlock Big(string text) =>
        new() { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };

    private static TextBlock Small(string text) =>
        new() { Text = text, FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };

    private static Border Pill(string text, string color) =>
        new()
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)new BrushConverter().ConvertFromString(color)!,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12 },
        };
}
