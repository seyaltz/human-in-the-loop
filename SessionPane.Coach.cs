using System;
using System.IO;
using System.Linq;
using System.Windows;

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
