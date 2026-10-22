using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DevBreak;

/// <summary>
/// The factory as a production line: each department is a line of stations, a task moves along a belt
/// beneath them, and the stations marked ◆ are quality gates it passes only when their check is green.
/// </summary>
public static class FactoryLine
{
    /// <summary>One station of a line: what it does, the check it runs, and why the line has it.</summary>
    public sealed record Station(string Name, FactoryStage[] Stages, bool Gate, string What, string Check, string Why);

    private static readonly Station GateStation = new("Gate", [FactoryStage.Locate], false,
        "Reads the task and chooses its department (when you chose Auto), then finds the repo for it in the folder.",
        "A department is chosen and the repo is found - or the task stops and says why.",
        "Each kind of work has its own line: a change, a customer's problem, a question, a company matter.");

    private static readonly Station WorkspaceStation = new("Workspace", [FactoryStage.Worktree], false,
        "Puts your checkout on a new branch for the task (or a separate worktree, if you chose one) and locks it.",
        "Your own uncommitted files are recorded, so they can never be part of what ships; no other run uses the checkout meanwhile.",
        "The task works where you work, without touching what is yours.");

    private static readonly Station LearnStation = new("Learn", [FactoryStage.Learn], false,
        "Looks back at the task: facts go into the repo's Claude memory, rules and skills into the factory's knowledge, lessons into Insights.",
        "Rules apply at once in autonomous mode; otherwise they wait for your OK.",
        "The next task on this line starts from what this one learned.");

    /// <summary>Development: a change, from the task to a draft PR.</summary>
    public static readonly Station[] Development =
    [
        GateStation,
        WorkspaceStation,
        new("Plan", [FactoryStage.Plan], false,
            "Claude reads the code and writes a plan: what to change, how to check it, and the questions it has.",
            "Autonomous: it takes the suggested answers. Otherwise you answer the questions first.",
            "A change is built against a plan, so the reviewer can check it did what it said."),
        new("Build", [FactoryStage.Build], false,
            "Claude changes the code with its Edit and Write tools. Every task sent back by a gate (↺) is fixed here.",
            "A stuck detector stops a turn that repeats the same call, so a loop cannot burn the run.",
            "The only station that changes the code."),
        new("Verify", [FactoryStage.Verify], true,
            "Runs the verify command - the build and the tests for what changed.",
            "It must pass. Failing, the task goes back to Build with the output, and comes back here.",
            "Nothing is reviewed that does not build and pass its tests."),
        new("Review", [FactoryStage.Review], true,
            "A second, read-only Claude reads the diff against the plan and your feedback.",
            "It must find nothing to fix. Findings send the task back to Build, then Verify again.",
            "A fresh pair of eyes that did not write the code."),
        new("Prove", [FactoryStage.Prove], true,
            "Runs the app with the change and keeps the evidence: screenshots, responses, log lines.",
            "The change must work in the running app, not only in the tests. If proving it changed code, the task is reviewed again.",
            "Evidence you can open, instead of a claim."),
        new("Ship", [FactoryStage.PullRequest], true,
            "Commits the change, checks the commit in code, pushes exactly that commit and opens a draft PR.",
            "Only the change's files, none of your local files, no secret, nothing pushed behind the factory's back - or nothing leaves the machine.",
            "What reaches the remote is exactly what was checked."),
        LearnStation,
    ];

    /// <summary>Troubleshooting, Research and Management: an investigation, ending in a checked answer and a report.</summary>
    public static readonly Station[] Investigation =
    [
        GateStation,
        WorkspaceStation,
        new("Investigate", [FactoryStage.Plan], false,
            "Gathers the facts with the tools on this machine: reproduces a problem, reads the code and the data, compares with what works.",
            "No file is changed - investigation steps cannot commit or push.",
            "An answer is built from evidence, not from a guess."),
        new("Check", [FactoryStage.Prove], true,
            "Checks the answer against the running system and keeps the evidence.",
            "The answer must hold when tried, the way you would use it.",
            "What it hands over has been tried."),
        new("Report", [], false,
            "Turns the result into a readable page: the summary first, then what was found, the evidence and what is not verified.",
            "Troubleshooting: a plain part for support and a technical part for developers.",
            "Something you can forward."),
        LearnStation,
    ];

    public static Station[] LineOf(FactoryDepartment department) => department == FactoryDepartment.Development ? Development : Investigation;

    // the line a task is on, by its department (a Development task with nothing to change passes Plan, then Prove
    // checks its answer - Build, Verify, Review and Ship show as skipped)
    public static Station[] LineOf(FactoryRun run) => LineOf(run.Department);

    public enum State { Waiting, Working, Passed, Stopped, Skipped }

    /// <summary>Where the item is at one station: its state, the work time spent there, and how often it came back.</summary>
    public sealed record At(State State, TimeSpan Time, int Visits, bool Slow);

    public static At StateAt(FactoryRun run, Station station)
    {
        if (station.Name == "Report")
        {
            bool made = run.ReportFile.Length > 0;
            return new At(made ? State.Passed : run.Report || run.Working ? State.Waiting : State.Skipped, TimeSpan.Zero, made ? 1 : 0, false);
        }
        var stages = station.Stages.ToHashSet();
        // the time Claude, the verify command and git spent at the station
        TimeSpan time = TimeSpan.Zero;
        bool slow = false;
        foreach (FactorySpan span in run.Trace.Where(s => stages.Contains(s.Stage)))
        {
            time += (span.Ended ?? DateTime.Now) - span.Started;
            slow |= span.Ended == null && span.Slow;
        }
        // how often the item entered the station (a gate that sent it back makes Build count twice)
        int visits = 0;
        FactoryStage? last = null;
        foreach (FactoryStep step in run.Log)
        {
            if (step.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped) continue;
            if (stages.Contains(step.Stage) && last != step.Stage) visits++;
            last = step.Stage;
        }
        FactoryStage? stoppedAt = run.Stage is FactoryStage.NeedsYou or FactoryStage.Stopped
            ? run.Log.LastOrDefault(s => s.Stage is not (FactoryStage.NeedsYou or FactoryStage.Stopped or FactoryStage.Learn))?.Stage : null;
        State state = run.Working && stages.Contains(run.Stage) ? State.Working
            : !run.Working && stoppedAt is { } at && stages.Contains(at) ? State.Stopped
            : visits > 0 ? State.Passed
            : !run.Working && run.Stage == FactoryStage.Done ? State.Skipped
            : State.Waiting;
        return new At(state, time, visits, slow);
    }

    // the station the item stands at: where it works, where it stopped, or the last one it passed
    private static int ItemAt(FactoryRun run, Station[] line)
    {
        At[] at = line.Select(s => StateAt(run, s)).ToArray();
        int here = Array.FindIndex(at, a => a.State is State.Working or State.Stopped);
        return here >= 0 ? here : Array.FindLastIndex(at, a => a.State == State.Passed);
    }

    public static string Time(TimeSpan time) => time.TotalSeconds < 1 ? "" : time.TotalMinutes < 1 ? $"{time.TotalSeconds:0}s"
        : time.TotalHours < 1 ? $"{(int)time.TotalMinutes}m {time.Seconds:00}s" : $"{(int)time.TotalHours}h {time.Minutes:00}m";

    private static string Tip(Station station) =>
        $"{(station.Gate ? "◆ Quality gate - " : "")}{station.Name}\n\nWhat it does: {station.What}\n\nThe check: {station.Check}\n\nWhy: {station.Why}";

    /// <summary>One run's line: the stations, each with its state and time, and the item on the belt beneath.</summary>
    public static FrameworkElement Build(FactoryRun run, Func<string, Brush> brush)
    {
        Station[] line = LineOf(run);
        int itemAt = ItemAt(run, line);
        var grid = Belt(line, brush, out int[] columns);
        for (int i = 0; i < line.Length; i++)
        {
            At at = StateAt(run, line[i]);
            grid.Children.Add(Place(Card(line[i], at, brush), 0, columns[i]));
        }
        if (itemAt >= 0)
        {
            At here = StateAt(run, line[itemAt]);
            string label = here.State switch
            {
                State.Working => "▣ here now",
                State.Stopped => "▣ needs you",
                _ => run.Stage == FactoryStage.Done ? (run.Accepted ? "▣ accepted" : run.PrUrl.Length > 0 ? "▣ draft PR" : run.ReportFile.Length > 0 ? "▣ report" : "▣ done") : "▣",
            };
            grid.Children.Add(Place(Item(label, here.State, brush), 2, columns[itemAt]));
        }
        return grid;
    }

    /// <summary>
    /// A department's line on the floor: the stations, and under each the items standing there now (working or
    /// waiting for a human); a click on an item opens its run.
    /// </summary>
    public static FrameworkElement Floor(string name, Station[] line, IReadOnlyList<FactoryRun> items, Func<string, Brush> brush, Action<FactoryRun> open)
    {
        var grid = Belt(line, brush, out int[] columns);
        var stands = new WrapPanel[line.Length];
        for (int i = 0; i < line.Length; i++)
        {
            // violet while an item is worked on here, coral when the items here only wait for a human
            int working = items.Count(r => ItemAt(r, line) == i && r.Working);
            int here = items.Count(r => ItemAt(r, line) == i && (r.Working || r.Stage == FactoryStage.NeedsYou));
            At look = new(working > 0 ? State.Working : here > 0 ? State.Stopped : State.Waiting, TimeSpan.Zero, 0, false);
            Border card = Card(line[i], look, brush, compact: true, count: here);
            grid.Children.Add(Place(card, 0, columns[i]));
            stands[i] = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            grid.Children.Add(Place(stands[i], 2, columns[i]));
        }
        foreach (FactoryRun run in items)
        {
            int i = ItemAt(run, line);
            if (i < 0) i = 0;
            State state = run.Working ? State.Working : State.Stopped;
            string title = run.Title.Length > 22 ? run.Title[..21] + "…" : run.Title;
            Border item = Item("▣ " + title, state, brush);
            item.Margin = new Thickness(2, 2, 2, 0);
            item.Cursor = System.Windows.Input.Cursors.Hand;
            item.ToolTip = $"{run.Title}\n{(run.Working ? "Working at " + line[i].Name : "Needs you")} - click to open it";
            item.MouseLeftButtonUp += (_, _) => open(run);
            stands[i].Children.Add(item);
        }
        var head = new TextBlock { Text = name, FontWeight = FontWeights.Bold, FontSize = 13.5, Margin = new Thickness(0, 0, 0, 6), Foreground = brush("InkBrush") };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(head);
        panel.Children.Add(grid);
        return panel;
    }

    // the grid of a line: a column per station with an arrow between, row 0 the stations, row 1 the belt, row 2 the items
    private static Grid Belt(Station[] line, Func<string, Brush> brush, out int[] columns)
    {
        var grid = new Grid();
        columns = new int[line.Length];
        for (int i = 0; i < line.Length; i++)
        {
            if (i > 0)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var arrow = new TextBlock { Text = "▸", FontSize = 14, Foreground = brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
                grid.Children.Add(Place(arrow, 0, grid.ColumnDefinitions.Count - 1));
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 58 });
            columns[i] = grid.ColumnDefinitions.Count - 1;
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // the belt: a rail with rollers
        var belt = new Rectangle
        {
            Height = 6, RadiusX = 3, RadiusY = 3, Fill = brush("RuleBrush"), Stroke = brush("LineBrush"), StrokeThickness = 1,
            StrokeDashArray = [3, 2], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetRow(belt, 1);
        Grid.SetColumnSpan(belt, grid.ColumnDefinitions.Count);
        grid.Children.Add(belt);
        return grid;
    }

    private static Border Card(Station station, At at, Func<string, Brush> brush, bool compact = false, int count = 0)
    {
        Brush ink = brush("InkBrush");
        bool working = at.State == State.Working;
        Brush back = at.Slow ? new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D))
            : working ? brush("VioletBrush") : at.State == State.Stopped ? brush("CoralTintBrush") : brush("CardBrush");
        Brush fore = working ? Brushes.White : ink;
        var name = new TextBlock
        {
            Text = (station.Gate ? "◆ " : "") + station.Name, FontWeight = FontWeights.SemiBold, FontSize = compact ? 11.5 : 12.5,
            Foreground = fore, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var stack = new StackPanel();
        stack.Children.Add(name);
        string status = compact
            ? (count > 0 ? $"{count} here" + (at.State == State.Stopped ? ", waiting" : "") : "")
            : at.State switch
            {
                State.Working => "● " + Time(at.Time) + "…",
                State.Passed => "✓ " + Time(at.Time),
                State.Stopped => "✗ stopped",
                State.Skipped => "–",
                _ => "",
            } + (at.Visits > 1 ? $"  ↺{at.Visits - 1}" : "");
        if (status.Length > 0 || !compact)
        {
            stack.Children.Add(new TextBlock
            {
                Text = status.Length > 0 ? status : " ", FontSize = 11, Foreground = working ? Brushes.White : at.State == State.Stopped ? brush("CoralInkBrush") : brush("InkSoftBrush"),
                TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
            });
        }
        var card = new Border
        {
            CornerRadius = new CornerRadius(station.Gate ? 4 : 10), BorderThickness = new Thickness(station.Gate ? 2 : 1.5),
            BorderBrush = at.State == State.Stopped ? brush("CoralInkBrush") : ink, Background = back,
            Padding = new Thickness(4, compact ? 4 : 6, 4, compact ? 4 : 6), Child = stack,
            Opacity = at.State is State.Waiting or State.Skipped && count == 0 ? (compact ? 0.85 : 0.45) : 1,
            ToolTip = Tip(station),
        };
        ToolTipService.SetShowDuration(card, 60000);
        ToolTipService.SetInitialShowDelay(card, 250);
        System.Windows.Automation.AutomationProperties.SetName(card, station.Name);
        return card;
    }

    // the item on the belt; it pulses while a station works on it
    private static Border Item(string label, State state, Func<string, Brush> brush)
    {
        var item = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Center,
            Background = state == State.Stopped ? brush("CoralBrush") : brush("MarkerBrush"), BorderBrush = brush("InkBrush"), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = brush("InkBrush") },
        };
        if (state == State.Working)
        {
            item.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        return item;
    }

    private static UIElement Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        return element;
    }

    /// <summary>The whole factory in plain words, for the "How the factory works" panel.</summary>
    public const string HowItWorks = """
        Every task - typed, a Jira issue or a spec - enters at the Gate. The Gate chooses its department, and each department is a production line of stations. The task moves along the belt from station to station; hover a station to see what it does, the check it runs and why it is there.

        Development builds a change: Plan, Build, then three quality gates (◆) - Verify runs the tests, Review is a second Claude that did not write the code, Prove runs the app and keeps evidence. A gate that is not green sends the task back to Build (↺ counts the rework) and the task comes through the gates again. Ship is the last gate: the commit is checked in code before anything leaves the machine, and a draft PR opens.

        Troubleshooting, Research and Management investigate instead of changing code: they gather facts, check the answer against the running system, and hand over a report.

        Autonomous tasks run with no stops. When one stops, the department manager looks at it: it restarts what the AI can fix, and calls you (a Windows notification) for what it cannot - a login, an access grant, a business decision. A safety stop always goes to you.

        Safety on every line: your own uncommitted files never ship; a commit that looks like it holds a secret is held; a step that may not push cannot push (git itself refuses), and before its own push the factory checks that nothing reached the remote behind its back.

        After each task the Learn station keeps what it taught: facts in the repo's memory, rules and skills for the next tasks, lessons in Insights. When a change is done you review it: Accept it, or Request changes - the task goes round the line again as the next revision.

        ● working   ✓ passed (time spent)   ↺ sent back and redone   ✗ stopped   ◆ quality gate
        """;
}
