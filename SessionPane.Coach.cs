using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// The pane watching its turn: what it waits on (a chip in the footer, with how long the same command usually takes in
/// the user's own sessions), and the tray telling the user, when the window is not in front, that a long wait started
/// and again when Claude needs them. Measured on the author's sessions: about a third of active turn time was waiting.
/// </summary>
public partial class SessionPane
{
    // a wait worth leaving the window for (the usual time of the command, or a background task with nothing else running)
    private static readonly TimeSpan LongWait = TimeSpan.FromMinutes(2);

    private void UpdateWait()
    {
        SessionState? state = _state;
        TurnStep? step = state?.Working == true ? state.Log?.Running : null;
        if (state == null || step == null || !step.IsShell || step.Sub)
        {
            WaitChip.Visibility = Visibility.Collapsed;
            if (state?.Working == true && state.Log?.Running == null && state.Tasks.Count > 0) TellWaiting(state, "bg", state.Tasks.Values.First(), null);
            return;
        }
        TimeSpan? usual = SessionStats.Usual(step.Key);
        WaitChip.Text = Watch.WaitText(step, DateTime.Now, usual);
        WaitChip.Visibility = Visibility.Visible;
        if (usual > LongWait) TellWaiting(state, step.Id, step.Command, usual);
    }

    // once per wait, and only when the user is not looking at Dev break
    private void TellWaiting(SessionState state, string about, string what, TimeSpan? usual)
    {
        if (state.WaitToldFor == about || WindowInFront()) return;
        state.WaitToldFor = about;
        state.ToldWaiting = true;
        string shortWhat = what.Length > 60 ? what[..59] + "…" : what;
        string text = $"Claude is waiting on {shortWhat}" + (usual is { } u ? $" - about {Math.Max(1, (int)Math.Round(u.TotalMinutes))} min." : ".") + " I'll tell you.";
        App.Alert("Dev break", text);
        Log("balloon: " + text);
    }

    // the second notice: the turn that said it waits now needs the user (it ended, asks, or the coach flagged it)
    private void CallBack(SessionState state, string text)
    {
        if (!state.ToldWaiting || WindowInFront()) return;
        state.ToldWaiting = false;
        App.Alert("Dev break", text);
        Log("balloon: " + text);
    }

    private bool WindowInFront() => Window.GetWindow(this) is { IsActive: true };

    // --- the coach ---

    private static readonly TimeSpan AiCheckAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AiCheckEvery = TimeSpan.FromSeconds(60);
    private const int AiChecksPerTurn = 10;
    private DateTime _lastWatch;

    // on the pane's clock (every second while a turn runs): the free rules every 2 s, the AI check once a minute
    private void WatchTurn(SessionState state)
    {
        if (!state.Working || state.Log is not { } log || DateTime.Now - _lastWatch < TimeSpan.FromSeconds(2)) return;
        _lastWatch = DateTime.Now;
        bool busy = log.Running != null || state.Tasks.Count > 0;
        if (state.Coach == null && Watch.Flag(log, DateTime.Now, busy) is { } reason && !state.CoachDismissed.Contains(Rule(reason)))
        {
            Raise(state, new CoachFlag(reason, Evidence(log), DefaultNudge(reason), Rule(reason)));
        }
        SessionSetting setting = SessionSettings.Of(state.Info.Id);
        int steps = log.Steps.Count(s => !s.Sub);
        if (setting.AiCheck && !state.AiChecking && state.Coach == null && state.AiChecks < AiChecksPerTurn
            && DateTime.Now - log.Start >= AiCheckAfter && DateTime.Now - state.AiCheckAt >= AiCheckEvery && steps > state.AiCheckSteps)
        {
            _ = AiCheck(state, log, steps);
        }
    }

    // a reason's rule, without its details: a dismissed rule is not raised again this turn
    private static string Rule(string reason) => reason.Split(':')[0].Split(' ').Take(4) is var words ? string.Join(' ', words) : reason;

    private static List<string> Evidence(TurnLog log) =>
        [.. log.Steps.Where(s => !s.Sub).TakeLast(5).Select(s => $"{s.Tool}: {Shorten(s.Summary, 90)}{(s.End == null ? "  (running)" : s.Error ? "  ✗ " + Shorten(s.Result, 80) : "  ✓")}")];

    private static string DefaultNudge(string reason) =>
        $"You seem stuck ({reason}). Stop, say in two lines what you have tried and why it failed, then try a different approach.";

    // a small model reads the user's ask and the last steps: "on track", or "drifting: why" and a nudge
    private async Task AiCheck(SessionState state, TurnLog log, int steps)
    {
        state.AiChecking = true;
        state.AiChecks++;
        state.AiCheckSteps = steps;
        state.AiCheckAt = DateTime.Now;
        string record = string.Join("\n", log.Steps.Where(s => !s.Sub).TakeLast(15)
            .Select(s => $"- {s.Tool}: {Shorten(s.Summary, 120)} [{(s.End == null ? "running" : s.Error ? "error: " + Shorten(s.Result, 100) : "ok")}]"));
        string prompt = $"""
            You watch a coding agent's turn for its user. The user asked:
            {Shorten(SessionAnswers.WithoutShape(log.Message), 1500)}

            It has worked {(int)(DateTime.Now - log.Start).TotalMinutes} min. Its last steps, oldest first:
            {record}

            Is it on track to do what the user asked? Answer with exactly one line: "on track", or
            "drifting: <why, one sentence>" followed by a second line "nudge: <one short message to the agent that would get it back on track>".
            Say drifting only when the steps show it (repeating, wandering off the ask, fighting the same error); slow but sensible work is on track.
            """;
        string answer;
        try
        {
            answer = await ClaudeOnce.AskAsync(prompt, "haiku", state.Info.Folder, "", "", TimeSpan.FromSeconds(90));
        }
        catch (Exception ex)
        {
            Log("ai check: could not run - " + ex.Message);
            return;
        }
        finally
        {
            state.AiChecking = false;
        }
        Log($"ai check ({state.Info.Id[..8]}): {answer.Replace('\n', ' ')}");
        // the turn it looked at ended meanwhile (stopped, finished): its answer is about nothing on screen
        if (state.Log != log || !state.Working || state.Coach != null) return;
        if (Drifting(answer) is not { } drift || state.CoachDismissed.Contains("ai check")) return;
        Raise(state, new CoachFlag("drifting: " + drift.Why, Evidence(log), drift.Nudge ?? DefaultNudge(drift.Why), "ai check"));
    }

    // the check's answer, when it says drifting; anything out of shape is ignored
    internal static (string Why, string? Nudge)? Drifting(string answer)
    {
        string[] lines = answer.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("drifting:", StringComparison.OrdinalIgnoreCase)) return null;
        string why = lines[0][9..].Trim();
        string? nudge = lines.Skip(1).FirstOrDefault(l => l.StartsWith("nudge:", StringComparison.OrdinalIgnoreCase)) is { } n ? n[6..].Trim() : null;
        return why.Length == 0 ? null : (why, string.IsNullOrEmpty(nudge) ? null : nudge);
    }

    private void Raise(SessionState state, CoachFlag flag)
    {
        state.Coach = flag;
        Log($"coach ({state.Info.Id[..8]}): {flag.Reason}");
        CallBack(state, "Claude may be stuck: " + Shorten(flag.Reason, 80));
        if (_state == state) UpdateCoach();
    }

    // the card above the box: why, the steps that show it, the nudge to send (editable) and what to do
    private void UpdateCoach()
    {
        SessionState? state = _state;
        CoachBody.Children.Clear();
        if (state?.Coach is not { } flag)
        {
            CoachBox.Visibility = Visibility.Collapsed;
            return;
        }
        CoachBody.Children.Add(new TextBlock
        {
            Text = "⚠ " + flag.Reason, FontWeight = FontWeights.Bold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("CoralInkBrush"), Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (string line in flag.Evidence)
        {
            CoachBody.Children.Add(new TextBlock
            {
                Text = line, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("InkSoftBrush"),
            });
        }
        var nudge = new TextBox
        {
            Text = flag.Nudge, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 90, FontSize = 13,
            Margin = new Thickness(0, 6, 0, 6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(nudge, "Coach nudge");
        CoachBody.Children.Add(nudge);
        var ideas = new WrapPanel();
        CoachBody.Children.Add(ideas);
        CoachBody.Children.Add(Buttons(
            CardButton(state.Working ? "Send nudge" : "Send", true, () => SendNudge(state, nudge.Text)),
            CardButton("Think differently", false, () => _ = ThinkDifferently(state, flag, nudge, ideas)),
            CardButton("Fresh start", false, () => _ = FreshStart(state, flag)),
            CardButton("Dismiss", false, () =>
            {
                state.CoachDismissed.Add(flag.Rule);
                state.Coach = null;
                UpdateCoach();
            })));
        CoachBox.Visibility = Visibility.Visible;
    }

    // into the running turn (it joins it), or - the turn over - as the next message
    private void SendNudge(SessionState state, string text)
    {
        if (text.Trim().Length == 0) return;
        state.CoachDismissed.Add(state.Coach?.Rule ?? "");
        state.Coach = null;
        UpdateCoach();
        InputBox.Text = text.Trim();
        Send(now: state.Working);
    }

    // a small model proposes other ways to the user's goal, given what failed; one click puts one in the nudge
    private async Task ThinkDifferently(SessionState state, CoachFlag flag, TextBox nudge, WrapPanel ideas)
    {
        ideas.Children.Clear();
        ideas.Children.Add(Say("Thinking of other ways..."));
        string goal = Shorten(SessionAnswers.WithoutShape(state.Log?.Message ?? state.TurnQuestion), 1500);
        string prompt = $"""
            A coding agent is stuck. The user asked: {goal}
            What is going wrong: {flag.Reason}
            Its last steps:
            {string.Join("\n", flag.Evidence)}

            Give 2 or 3 genuinely different approaches to reach the user's goal that avoid what failed. One line each,
            starting "- ", written as an instruction to the agent. Nothing else.
            """;
        string answer;
        try
        {
            answer = await ClaudeOnce.AskAsync(prompt, "haiku", state.Info.Folder, "", "", TimeSpan.FromSeconds(90));
        }
        catch (Exception ex)
        {
            ideas.Children.Clear();
            ideas.Children.Add(Say("Could not ask: " + ex.Message));
            return;
        }
        ideas.Children.Clear();
        foreach (string idea in answer.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ")).Take(3))
        {
            string text = idea[2..].Trim();
            Button pick = CardButton(Shorten(text, 70), false, () => nudge.Text = text);
            pick.ToolTip = text;
            pick.Margin = new Thickness(0, 0, 6, 6);
            ideas.Children.Add(pick);
        }
        if (ideas.Children.Count == 0) ideas.Children.Add(Say("No other approach came back."));
    }

    // stop, rewind the conversation to before this turn, and give the message back with what did not work
    private async Task FreshStart(SessionState state, CoachFlag flag)
    {
        TurnMark? mark = state.Marks.FirstOrDefault(m => m.Start == state.TurnStart);
        state.Coach = null;
        UpdateCoach();
        if (state.Working)
        {
            _ = StopTurn();
            for (int i = 0; i < 60 && state.Working; i++) await Task.Delay(250);
        }
        if (mark == null)
        {
            ShowAction("Fresh start - this turn has no rewind point", [Say("Its message was not echoed back by claude, so the conversation cannot go back to before it."),
                Buttons(CardButton("Close", false, CloseAction))]);
            return;
        }
        await Rewind(state, mark, code: false, talk: true);
        InputBox.Text = InputBox.Text.TrimEnd() + "\n\nWhat did not work last time: " + flag.Reason;
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    // after the turn: the coach's card goes (it was about the running turn), the diagnosis line goes under the turn
    private void AfterTurn(SessionState state)
    {
        if (state.Coach != null && state.Coach.Rule != "unverified")
        {
            state.Coach = null;
            if (_state == state) UpdateCoach();
        }
        if (state.Log is { } log && Watch.Diagnosis(log, DateTime.Now - log.Start) is { } diagnosis)
        {
            state.Document.Blocks.Add(new Paragraph(new Run("⏱ " + diagnosis))
            {
                FontSize = 11.5, Foreground = (Brush)FindResource("InkSoftBrush"), Margin = new Thickness(0, -4, 0, 8),
            });
        }
    }

    private void AiCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        SessionSetting setting = SessionSettings.Of(_state.Info.Id);
        setting.AiCheck = !setting.AiCheck;
        SessionSettings.Save();
        UpdateBar();
    }

    private void GradeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        SessionSetting setting = SessionSettings.Of(_state.Info.Id);
        setting.Grade = !setting.Grade;
        SessionSettings.Save();
        UpdateBar();
    }

    // what the coach did, for checking it afterwards (next to the exe, git-ignored)
    internal static void Log(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "devbreak.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a log line is not worth failing for
        }
    }
}
