using System;
using System.Threading;
using System.Windows.Documents;

namespace DevBreak;

/// <summary>
/// A session opened in this run of Dev break: its transcript, whether a turn is running, how the last one ended,
/// and the pane it is on (at most one - a document can be shown in one place). A turn keeps running whichever pane,
/// if any, shows it; <see cref="Changed"/> tells the panes and the list lights.
/// </summary>
public sealed class SessionState
{
    public required SessionInfo Info { get; set; }
    public bool IsNew { get; set; }
    public bool StartedHere { get; init; }
    public bool Loaded { get; set; }
    public FlowDocument Document { get; } = CodeRenderer.Render("");
    public CancellationTokenSource? Running { get; set; }
    public int Steps { get; set; }

    /// <summary>The last turn ended with an error or was stopped.</summary>
    public bool Failed { get; set; }

    // where this turn's reply starts, and the work being folded together
    internal Block? ReplyStart { get; set; }
    internal StepGroup? Group { get; set; }

    // the turn on screen, folded to one line when the next one starts: where it starts, what was asked, how it ended
    internal Block? TurnStart { get; set; }
    internal string TurnQuestion { get; set; } = "";
    internal string TurnResult { get; set; } = "";
    internal string TurnDone { get; set; } = "";
    internal bool TurnFailed { get; set; }

    /// <summary>When the running turn started, and the last note Claude wrote in it (its progress) and when.</summary>
    public DateTime TurnStartedAt { get; set; }
    public string LastNote { get; set; } = "";
    public DateTime LastNoteAt { get; set; }

    /// <summary>The pane showing this session, if any.</summary>
    internal SessionPane? ShownIn { get; set; }

    public bool Working => Running != null;

    /// <summary>Working, failed or idle changed, or a step was counted.</summary>
    public event Action<SessionState>? Changed;

    public void Notify() => Changed?.Invoke(this);
}
