using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Documents;

namespace DevBreak;

/// <summary>
/// A session opened in this run of Dev break: its transcript, whether a turn is running, how the last one ended,
/// and the pane it is on (at most one - a document can be shown in one place). A turn keeps running whichever pane,
/// if any, shows it; <see cref="Changed"/> tells the panes and the list lights.
/// </summary>
/// <summary>What the coach shows: why, the steps that show it, the nudge it suggests (editable), the rule it came from.</summary>
public sealed record CoachFlag(string Reason, List<string> Evidence, string Nudge, string Rule);

public sealed class SessionState
{
    public required SessionInfo Info { get; set; }
    public bool IsNew { get; set; }
    public bool StartedHere { get; set; }

    /// <summary>A branch not started yet: the session it branches from (its first process forks it).</summary>
    internal string? ForkFrom { get; set; }

    /// <summary>After a conversation rewind: the line the next process resumes the session at.</summary>
    internal string? ResumeAt { get; set; }
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

    /// <summary>The session's kept-open claude process, while it has one.</summary>
    internal SessionProcess? Process { get; set; }

    /// <summary>The session file's size and time when its process last finished writing: another change means another writer.</summary>
    internal (long Length, DateTime Time)? FileStamp { get; set; }

    /// <summary>Stop was pressed for the running turn.</summary>
    internal bool Stopping { get; set; }

    /// <summary>Messages written while a turn ran, sent one by one when it ends.</summary>
    internal List<string> Queued { get; } = [];

    /// <summary>The turns of this run that can be rewound: where each starts in the document, its message's uuid, and the uuid before it.</summary>
    internal List<TurnMark> Marks { get; } = [];

    /// <summary>What the turns cost in this run of the app.</summary>
    public double Cost { get; set; }

    /// <summary>How full the context was after the last turn (percent), or -1 before it is known.</summary>
    public int ContextPercent { get; set; } = -1;

    /// <summary>The categories behind <see cref="ContextPercent"/>, one per line.</summary>
    public string ContextDetail { get; set; } = "";

    /// <summary>The mode before Plan: approving a plan goes back to it.</summary>
    public string? ModeBeforePlan { get; set; }

    /// <summary>Background tasks running (Bash commands and agents moved to the background): id and what it is.</summary>
    internal Dictionary<string, string> Tasks { get; } = [];

    /// <summary>Questions of the CLI shown as cards and not answered yet; each closes its card with a line.</summary>
    internal List<(PermissionAsk Ask, Action<string> Close)> OpenCards { get; } = [];

    /// <summary>The pane showing this session, if any.</summary>
    internal SessionPane? ShownIn { get; set; }

    /// <summary>A turn or a "!" shell command runs.</summary>
    public bool Working => Running != null || Shell != null;

    /// <summary>The "!" shell command running, if one does.</summary>
    internal CancellationTokenSource? Shell { get; set; }

    /// <summary>"!" commands run since the last message, with their output: they go with the next message.</summary>
    internal List<string> PendingShell { get; } = [];

    /// <summary>What the user wrote in this session, oldest first (↑ / ↓ and Ctrl+R).</summary>
    internal List<string> History { get; } = [];

    /// <summary>Working, failed or idle changed, or a step was counted.</summary>
    /// <summary>This turn told the user (tray) it waits: it tells them again when it needs them. The step it was about.</summary>
    public bool ToldWaiting { get; set; }
    public string WaitToldFor { get; set; } = "";

    /// <summary>The coach's card for this session (null: none), the reasons dismissed this turn, its AI check's bookkeeping.</summary>
    public CoachFlag? Coach { get; set; }
    public HashSet<string> CoachDismissed { get; } = [];

    /// <summary>Each turn's done line, where its grade and review go when they come back later.</summary>
    public Dictionary<TurnLog, Block> DoneLines { get; } = [];
    public bool Grading { get; set; }

    /// <summary>The last turn was stopped by the user: a new message then is asking again (a lesson may be in it).</summary>
    public bool LastStopped { get; set; }

    /// <summary>The next message is one a card sent (a nudge, a review): it is not the user correcting Claude.</summary>
    public bool FromCard { get; set; }
    public int AiChecks { get; set; }
    public int AiCheckSteps { get; set; }
    public DateTime AiCheckAt { get; set; }
    public bool AiChecking { get; set; }

    /// <summary>The running (or last) turn's tool calls, read from the process's lines.</summary>
    public TurnLog? Log { get; set; }

    /// <summary>The turns of this run, oldest first: the lessons look back at them.</summary>
    public List<TurnLog> PastLogs { get; } = [];

    public event Action<SessionState>? Changed;

    public void Notify() => Changed?.Invoke(this);
}

/// <summary>A turn of this run that can be rewound: its first block, its message, its uuid and the uuid of the line before it.</summary>
internal sealed record TurnMark(Block Start, string Message, string Uuid, string? Before)
{
    /// <summary>The turn's one line once it is folded (its blocks are then out of the document).</summary>
    public Block? Line { get; set; }
}
