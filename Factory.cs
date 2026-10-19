using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DevBreak;

/// <summary>Where a factory task comes from: typed in, a Jira issue, or a spec document (a file or a link).</summary>
public enum FactorySource { Typed, Jira, Spec }

/// <summary>Where a factory run is.</summary>
public enum FactoryStage { Queued, Worktree, Plan, Build, Verify, Review, PullRequest, Learn, Done, NeedsYou, Stopped, Locate, Prove }

/// <summary>One line of a run's log.</summary>
public sealed class FactoryStep
{
    public DateTime At { get; set; }
    public FactoryStage Stage { get; set; }
    public string Text { get; set; } = "";
    public bool Ok { get; set; } = true;
}

/// <summary>
/// Something a run learned for the repo's Claude harness: a memory note (saved at once, can be undone), or a rule -
/// a CLAUDE.md line, a skill or a hook - that waits for the developer's OK.
/// </summary>
public sealed class FactoryLesson
{
    public string Kind { get; set; } = "memory"; // memory | claude-md | skill | hook
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    public string Status { get; set; } = "pending"; // saved | pending | applied | dismissed | undone
    public string? File { get; set; }
    /// <summary>Why the developer dismissed or undid it; later lessons for the repo avoid the like.</summary>
    public string Feedback { get; set; } = "";
}

/// <summary>A tool call Claude made in a step, and how it ended.</summary>
public sealed class FactoryToolCall
{
    public DateTime At { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Result { get; set; } = "running"; // running | ok | error | denied
    /// <summary>When its result came back.</summary>
    public DateTime? Ended { get; set; }
    /// <summary>It ran over the slow limit (a minute) - the developer was told.</summary>
    public bool Slow { get; set; }
}

/// <summary>
/// One step of a run in the trace: a Claude turn, a one-off Claude (locate, review, learn), the verify command or a
/// git command - when it ran, what it cost, the tools it used, and exactly what went in and came out.
/// </summary>
public sealed class FactorySpan
{
    public FactoryStage Stage { get; set; }
    public string Kind { get; set; } = ""; // turn | claude | verify | git
    public string Title { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime? Ended { get; set; }
    public double Cost { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Denied { get; set; }
    /// <summary>The step ran over the slow limit (a minute) - the developer was told.</summary>
    public bool Slow { get; set; }
    public string Prompt { get; set; } = "";
    public string Answer { get; set; } = "";
    public string Error { get; set; } = "";
    public List<FactoryToolCall> Tools { get; set; } = [];
}

/// <summary>
/// One piece of proof that the change works, from the run's evidence manifest: what kind it is, its file, what it
/// proves and how it came out.
/// </summary>
public sealed class FactoryEvidence
{
    public string Kind { get; set; } = ""; // screenshots | video | red-green | coverage | mutation | crap | api | logs | trace | perf | other
    public string File { get; set; } = "";
    public string Proves { get; set; } = "";
    public string Result { get; set; } = ""; // pass | fail | skipped
    public string Detail { get; set; } = "";
}

/// <summary>Something the developer told a run to change.</summary>
public sealed class FactoryFeedback
{
    public DateTime At { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>A task taken from its source to a draft pull request, in its own worktree and branch.</summary>
public sealed class FactoryRun
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public FactorySource Source { get; set; }
    public string Task { get; set; } = "";
    /// <summary>The folder the developer pointed at: a repo, or a folder with repos somewhere in it.</summary>
    public string Folder { get; set; } = "";
    /// <summary>The repo the run works on, once it is located.</summary>
    public string Repo { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string BaseCommit { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Worktree { get; set; } = "";
    public string Verify { get; set; } = "";
    public string SessionId { get; set; } = "";
    public FactoryStage Stage { get; set; }
    /// <summary>Why the run waits for the developer (NeedsYou), or how it ended.</summary>
    public string Note { get; set; } = "";
    public string Plan { get; set; } = "";
    /// <summary>A verify command used in this repo before: its environment and flags, for the plan to reuse.</summary>
    public string VerifyHint { get; set; } = "";
    /// <summary>The last verify run: its exit code and the end of its output (the reviewer reads it).</summary>
    public string VerifyResult { get; set; } = "";
    /// <summary>What Claude saw when it ran the app with the change, ending PROOF: PASS / FAIL / SKIPPED.</summary>
    public string Proof { get; set; } = "";
    /// <summary>The kinds of proof the plan chose for this feature.</summary>
    public List<string> EvidencePlan { get; set; } = [];
    /// <summary>The proof produced, read from the evidence manifest.</summary>
    public List<FactoryEvidence> Evidence { get; set; } = [];
    /// <summary>What the task asked to be handed back (a curl, an answer, a link), ready to use - shown on top.</summary>
    public string Result { get; set; } = "";
    public string Review { get; set; } = "";
    public string PrUrl { get; set; } = "";
    public double Cost { get; set; }
    public int Steps { get; set; }
    public DateTime Created { get; set; }
    public List<FactoryStep> Log { get; set; } = [];
    public List<FactoryLesson> Lessons { get; set; } = [];
    public List<FactorySpan> Trace { get; set; } = [];
    public List<FactoryFeedback> Feedback { get; set; } = [];
    /// <summary>The developer's verdict on the run: good, bad, or none yet; with a note.</summary>
    public string Rating { get; set; } = "";
    public string RatingNote { get; set; } = "";
    /// <summary>Its worktree, evidence and session files were deleted; the record and trace stay.</summary>
    public bool CleanedUp { get; set; }
    /// <summary>The run works in the developer's own checkout (the default), on its branch; Worktree is then the repo.</summary>
    public bool InCheckout { get; set; }
    /// <summary>The developer chose a separate worktree for this run (when their checkout could not be used).</summary>
    public bool UseWorktree { get; set; }
    /// <summary>The branch (or commit) the developer's checkout was on, to switch back to when the run is done.</summary>
    public string ReturnTo { get; set; } = "";
    /// <summary>
    /// The developer's own uncommitted changes in their checkout when the run started (path → a fingerprint of its
    /// content): never committed by the run; the run stops and asks when its change touches one of them.
    /// </summary>
    public Dictionary<string, string> LocalChanges { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore] public CancellationTokenSource? Running { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Working => Running != null;
}

/// <summary>
/// The software factory: a task goes plan → build → verify → review → draft PR, then what the run learned goes into
/// the repo's Claude harness. Each run works in its own git worktree and branch, so the developer's checkout is never
/// touched. Claude works with the developer's full setup and full access in that worktree (as in Sessions), but
/// may not push or open a PR until the checks passed - that is the last step, done on purpose. The verify command
/// is run by Dev break itself, and the reviewer is a fresh Claude that can only read.
/// </summary>
public static class Factory
{
    private const int MaxFixes = 3;
    private const int MaxReviews = 2;
    // the kinds of proof a run can produce, and how to produce each (the prove step's guide)
    private static readonly string[] EvidenceKinds = ["screenshots", "video", "red-green", "coverage", "mutation", "crap", "api", "logs", "trace", "perf", "other"];
    private const string EvidenceGuide = """
        - screenshots: the changed UI in the running app, at the sizes that matter (desktop and phone for a web page);
          a "before" too when it helps, from the base commit in a separate worktree - never the developer's checkout.
        - video: a short recording (webm/mp4/gif) of the interaction the task changed, e.g. a browser tool's video recording.
        - red-green: the new or changed tests fail without the change (on the base commit, or with the change stashed
          and restored) and pass with it - save both outputs.
        - coverage: coverage of the changed lines (not the whole project), with the lines left uncovered.
        - mutation: mutation testing scoped to the changed files (e.g. Stryker, PIT, mutmut) - the score and the
          surviving mutants.
        - crap: the CRAP score (complexity and coverage) of the changed methods, with the worst ones.
        - api: requests and responses that exercise the changed behaviour. logs: the log lines it produces.
          trace: the traced path of a request through it. perf: before/after timings when speed is the point.
        Use the repo's own tools first; a tool it lacks can be run without adding it to the repo's dependencies. When a
        kind cannot be produced here, it is "skipped" with the reason - never invented.
        """;
    private const string HookExample ="""{"PostToolUse":[{"matcher":"Edit|Write","hooks":[{"type":"command","command":"..."}]}]}""";
    private static readonly string StorePath = Path.Combine(AppContext.BaseDirectory, "factory.json");
    private static readonly string WorktreesDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBreak", "worktrees");

    // every turn: work only inside the run's folder; what must change outside it is the developer's to decide
    private const string OutsideRule = """

        Change files only inside this folder. Never change anything outside it - shared build homes (Gradle, Maven, npm
        or Ivy settings), the developer's profile, tool or system settings - not even to make a command work. If the
        work needs a change there, do not make it: end your reply with one line NEEDS_YOU: <what should change, where, and why>.
        """;

    /// <summary>Claude stopped to ask the developer (NEEDS_YOU, or the plan's questions): the run waits with the question.</summary>
    private sealed class NeedsYouException(string message) : Exception(message);

    // before the checks pass, a run may not publish anything
    private static readonly string[] NoPublish =
        ["--disallowedTools", "Bash(git push:*)", "Bash(gh pr create:*)", "PowerShell(git push:*)", "PowerShell(gh pr create:*)"];

    private static List<FactoryRun>? _runs;

    /// <summary>A run changed: its stage, its log, its cost or its lessons.</summary>
    public static event Action<FactoryRun>? Changed;

    /// <summary>All runs, newest first.</summary>
    public static List<FactoryRun> Runs
    {
        get
        {
            if (_runs != null) return _runs;
            try
            {
                _runs = File.Exists(StorePath) ? JsonSerializer.Deserialize<List<FactoryRun>>(File.ReadAllText(StorePath)) ?? [] : [];
            }
            catch (Exception)
            {
                _runs = [];
            }
            // a run that was working when Dev break closed has stopped
            foreach (FactoryRun run in _runs.Where(r => r.Stage is not (FactoryStage.Done or FactoryStage.NeedsYou or FactoryStage.Stopped)))
            {
                run.Note = $"Dev break closed while this run was at {run.Stage}.";
                run.Stage = FactoryStage.Stopped;
            }
            return _runs;
        }
    }

    public static FactoryRun Create(FactorySource source, string task, string folder, string baseBranch, string verify)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        string first = task.Trim().Split('\n')[0].Trim();
        string title = source switch
        {
            FactorySource.Jira => first.ToUpperInvariant(),
            FactorySource.Spec => "Spec: " + (Uri.TryCreate(first, UriKind.Absolute, out Uri? uri) && !uri.IsFile ? uri.Host + uri.AbsolutePath : Path.GetFileName(first)),
            _ => first.Length > 70 ? first[..69] + "…" : first,
        };
        var run = new FactoryRun
        {
            Id = id, Title = title, Source = source, Task = task.Trim(), Folder = folder.TrimEnd('\\'), BaseBranch = baseBranch.Trim(),
            Verify = verify.Trim(), SessionId = Guid.NewGuid().ToString(), Created = DateTime.Now,
        };
        Runs.Insert(0, run);
        Save();
        return run;
    }

    /// <summary>The branch a repo has checked out now: the default base for a run.</summary>
    public static async Task<string> CurrentBranchAsync(string repo)
    {
        (int exit, string output) = await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD");
        return exit == 0 ? output.Trim() : "";
    }

    /// <summary>Takes a run from its task to a draft PR, then learns from it. Ends at Done, NeedsYou or Stopped.</summary>
    public static Task RunAsync(FactoryRun run) => DriveAsync(run, learnAfter: false, async cancel =>
    {
        if (!await LocateAsync(run, cancel)) return false;
        if (!await WorktreeAsync(run, cancel)) return false;
        // from here on there is something to learn from
        if (!await PlanAsync(run, cancel)) return true;
        if (!await BuildAsync(run, cancel)) return true;
        if (await CheckAsync(run, cancel)) await PullRequestAsync(run, cancel);
        return true;
    });

    /// <summary>
    /// Picks up a run that stopped or waits for the developer where it left off: the steps already done (repo, worktree,
    /// plan) are not redone; Claude finishes what is missing, then verify, prove, review and the draft PR.
    /// </summary>
    public static Task ContinueAsync(FactoryRun run) => DriveAsync(run, learnAfter: false, async cancel =>
    {
        if (run.CleanedUp) return Wait(run, "This run was cleaned up - start a new run for the task.");
        FactoryStage stoppedAt = run.Log.LastOrDefault(s => s.Stage is not (FactoryStage.NeedsYou or FactoryStage.Stopped or FactoryStage.Learn))?.Stage ?? FactoryStage.Queued;
        string why = run.Note;
        if (run.Repo.Length == 0 && !await LocateAsync(run, cancel)) return false;
        if (run.InCheckout)
        {
            if (!await OnRunBranchAsync(run)) return false;
        }
        else if (run.Worktree.Length == 0 || !Directory.Exists(run.Worktree))
        {
            if (!await WorktreeAsync(run, cancel)) return false;
        }
        if (run.Plan.Length == 0)
        {
            // the plan never finished: a fresh session plans it
            run.SessionId = Guid.NewGuid().ToString();
            if (!await PlanAsync(run, cancel)) return true;
            if (!await BuildAsync(run, cancel)) return true;
        }
        else if (!run.Log.Any(s => s.Stage == FactoryStage.Build))
        {
            // it waited on the plan's questions: Continue goes with the suggested answers
            Log(run, "Continue without answers: the plan's suggested answers are used.");
            if (!await BuildAsync(run, cancel)) return true;
        }
        else
        {
            Enter(run, FactoryStage.Build, $"Claude continues where the run stopped ({stoppedAt}).");
            (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
                The run stopped at its {stoppedAt} step: {why}
                Continue the task from where this folder is now: finish what is missing, fix what stopped it.
                Do not commit or push. Reply with what you changed.
                """, "Continue", cancel);
            if (error != null) return Wait(run, "The continue turn failed: " + error);
            Log(run, "Continue:\n" + answer);
        }
        if (await CheckAsync(run, cancel)) await PullRequestAsync(run, cancel);
        return true;
    });

    /// <summary>What cleaning up a run would delete, and whether that loses work.</summary>
    public sealed record CleanupInfo(long Bytes, int Uncommitted, bool Pushed, bool HasWorktree);

    // a separate worktree of the run - never the developer's checkout
    private static bool HasOwnWorktree(FactoryRun run) =>
        !run.InCheckout && run.Worktree.Length > 0 && Directory.Exists(run.Worktree)
        && !run.Worktree.TrimEnd('\\').Equals(run.Repo.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static async Task<CleanupInfo> InspectAsync(FactoryRun run)
    {
        bool worktree = HasOwnWorktree(run);
        int uncommitted = 0;
        bool pushed = false;
        // uncommitted work of the run: in its worktree, or in the checkout while it is on the run's branch
        string? workDir = worktree ? run.Worktree
            : run.InCheckout && Directory.Exists(run.Repo) && await HeadAsync(run.Repo) == run.Branch ? run.Repo : null;
        if (workDir != null) uncommitted = (await RunChangesAsync(run, workDir)).Count;
        if (run.Repo.Length > 0 && run.Branch.Length > 0 && Directory.Exists(run.Repo))
        {
            (int code, string heads) = await GitAsync(run.Repo, "ls-remote", "--heads", "origin", run.Branch);
            pushed = code == 0 && heads.Contains(run.Branch, StringComparison.Ordinal);
        }
        long bytes = await Task.Run(() => CleanupPaths(run).Sum(SizeOf));
        return new CleanupInfo(bytes, uncommitted, pushed, worktree);
    }

    /// <summary>
    /// Deletes a run's drafted data: its worktree (and its local branch when that is pushed - an unpushed branch
    /// keeps its commits), its evidence and exported trace files, and its Claude session files. Never the developer's
    /// checkout, its memory, or the remote branch and PR. The record and its trace stay. Returns what was freed.
    /// </summary>
    public static async Task<string> CleanUpAsync(FactoryRun run)
    {
        if (run.Working) return "the run is working";
        CleanupInfo info = await InspectAsync(run);
        // what to delete is decided before anything is: once the worktree is gone, its session folder is not recognised
        List<string> paths = CleanupPaths(run).ToList();
        var done = new List<string>();
        if (info.HasWorktree && Directory.Exists(run.Repo))
        {
            (int code, _) = await GitAsync(run.Repo, "worktree", "remove", "--force", run.Worktree);
            if (code != 0 && Directory.Exists(run.Worktree)) await Task.Run(() => DeleteTree(run.Worktree));
            await GitAsync(run.Repo, "worktree", "prune");
            done.Add("worktree");
        }
        else if (run.InCheckout && Directory.Exists(run.Repo) && await HeadAsync(run.Repo) == run.Branch)
        {
            // the checkout is still on the run's branch: back to the developer's branch first, when nothing is lost
            if (info.Uncommitted > 0) return $"Your checkout is on {run.Branch} with {info.Uncommitted} uncommitted change(s) - commit or stash them first.";
            (int back, string output) = await GitAsync(run.Repo, "switch", run.ReturnTo.Length == 40 ? "--detach" : "--no-guess", run.ReturnTo.Length > 0 ? run.ReturnTo : run.BaseBranch);
            if (back != 0) return $"Could not switch your checkout off {run.Branch}: {output.Trim()}";
            done.Add($"checkout back on {(run.ReturnTo.Length > 0 ? run.ReturnTo : run.BaseBranch)}");
        }
        if (info.Pushed && Directory.Exists(run.Repo) && await HeadAsync(run.Repo) != run.Branch)
        {
            (int deleted, _) = await GitAsync(run.Repo, "branch", "-D", run.Branch);
            if (deleted == 0) done.Add("local branch (it is on the remote)");
        }
        foreach (string path in paths.Where(p => p != run.Worktree))
        {
            if (Directory.Exists(path)) await Task.Run(() => DeleteTree(path));
            else if (File.Exists(path)) File.Delete(path);
        }
        done.Add("evidence, exported trace and session files");
        run.CleanedUp = true;
        string freed = $"Cleaned up: {string.Join(", ", done)} - {Size(info.Bytes)} freed.";
        Log(run, freed);
        return freed;
    }

    // everything a run left on disk besides its record: its own worktree and that worktree's session files, its
    // evidence, its exported traces. A run in the developer's checkout has no worktree, and its sessions are among
    // the developer's own for that repo - never deleted.
    private static IEnumerable<string> CleanupPaths(FactoryRun run)
    {
        bool worktree = HasOwnWorktree(run);
        if (worktree) yield return run.Worktree;
        if (Directory.Exists(ProofDir(run))) yield return ProofDir(run);
        string traces = Path.Combine(AppContext.BaseDirectory, "traces");
        if (Directory.Exists(traces)) foreach (string f in Directory.GetFiles(traces, $"*-{run.Id}.*")) yield return f;
        // Claude Code keeps a folder's sessions under projects\<the path with every other character as '-'>
        if (worktree)
        {
            string key = string.Concat(run.Worktree.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
            string sessions = Path.Combine(ClaudeProjects.ProjectsDir, key);
            if (Directory.Exists(sessions)) yield return sessions;
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length
                : new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // a folder tree, read-only files too (git keeps its objects read-only)
    private static void DeleteTree(string dir)
    {
        foreach (FileInfo file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        Directory.Delete(dir, recursive: true);
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{bytes / 1024.0:0} KB";

    /// <summary>After a run waited on failed checks: opens the draft PR anyway (it says what failed).</summary>
    public static Task PullRequestAnywayAsync(FactoryRun run) => DriveAsync(run, learnAfter: false, async cancel =>
    {
        if (!await OnRunBranchAsync(run)) return false;
        await PullRequestAsync(run, cancel, anyway: true);
        return false;
    });

    /// <summary>
    /// The developer's feedback goes back to the run: Claude applies it in the run's session, verify and review run
    /// again, and the draft PR is updated (or opened); then the run learns again.
    /// </summary>
    public static Task FeedbackAsync(FactoryRun run, string text) => DriveAsync(run, learnAfter: true, async cancel =>
    {
        run.Feedback.Add(new FactoryFeedback { At = DateTime.Now, Text = text.Trim() });
        if (!Directory.Exists(run.Worktree)) return Wait(run, "The run's worktree is gone, so the feedback has nowhere to go.");
        if (!await OnRunBranchAsync(run)) return true;
        // nothing built yet: the feedback answers the plan's questions - the plan is updated, then built
        if (!run.Log.Any(s => s.Stage == FactoryStage.Build))
        {
            Enter(run, FactoryStage.Plan, "Your answers: " + text.Trim());
            (string plan, string? planError) = await TurnAsync(run, isNew: false, $"""
                The developer answered the plan's questions:
                {text.Trim()}

                Update the plan with these answers. For any question the developer did not answer, use the answer you
                suggested. Reply with the updated plan as numbered steps; add QUESTIONS: only for something the answers
                made newly unclear or contradictory - not for questions they skipped. Do not change any file yet.
                """, "Your answers", cancel);
            if (planError != null) return Wait(run, "The answers turn failed: " + planError);
            run.Plan = plan;
            Log(run, "Plan:\n" + plan);
            if (Questions(plan) is { Length: > 0 } open) throw new NeedsYouException("The plan still has questions for you:\n" + open);
            if (!await BuildAsync(run, cancel)) return true;
            if (await CheckAsync(run, cancel)) await PullRequestAsync(run, cancel);
            return true;
        }
        Enter(run, FactoryStage.Build, "Your feedback: " + text.Trim());
        (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
            The developer looked at your change and says:
            {text.Trim()}

            Apply it in this folder (if part of it is wrong, say why instead of doing it). Do not commit or push.
            Reply with what you changed.
            """, "Your feedback", cancel);
        if (error != null) return Wait(run, "The feedback turn failed: " + error);
        Log(run, "Fix:\n" + answer);
        if (await CheckAsync(run, cancel)) await PullRequestAsync(run, cancel);
        return true;
    });

    /// <summary>The developer rates the run (good / bad, with a note); the run learns again from what they said.</summary>
    public static Task RateAsync(FactoryRun run, string rating, string note) => DriveAsync(run, learnAfter: true, cancel =>
    {
        run.Rating = rating;
        run.RatingNote = note.Trim();
        Log(run, $"You rated the run {rating}" + (run.RatingNote.Length > 0 ? ": " + run.RatingNote : "."));
        return Task.FromResult(false);
    });

    // runs a body that moves the run on; afterwards (unless stopped) the run learns, when the body says there is
    // something to learn from or the caller asks for it, and its stage and note are what the body left
    private static async Task DriveAsync(FactoryRun run, bool learnAfter, Func<CancellationToken, Task<bool>> body)
    {
        if (run.Working) return;
        run.Running = new CancellationTokenSource();
        CancellationToken cancel = run.Running.Token;
        bool learn = learnAfter;
        try
        {
            learn |= await body(cancel);
        }
        catch (OperationCanceledException)
        {
        }
        catch (NeedsYouException ex)
        {
            Wait(run, ex.Message);
        }
        catch (Exception ex)
        {
            Wait(run, "Something went wrong: " + ex.Message);
        }
        finally
        {
            if (cancel.IsCancellationRequested) End(run, FactoryStage.Stopped, "Stopped.");
            else if (learn) await LearnAsync(run);
            try
            {
                await LeaveCheckoutAsync(run);
            }
            catch (Exception ex)
            {
                Log(run, "Could not switch your checkout back: " + ex.Message, ok: false);
            }
            run.Running.Dispose();
            run.Running = null;
            Notify(run);
        }
    }

    public static void Stop(FactoryRun run) => run.Running?.Cancel();

    // --- the stages ---

    // the repo the task is for: the folder's own repo, the one repo in it, or the one Claude picks for the task
    private static async Task<bool> LocateAsync(FactoryRun run, CancellationToken cancel)
    {
        Enter(run, FactoryStage.Locate, $"Finding the repo for the task in {run.Folder}.");
        if (!Directory.Exists(run.Folder)) return Wait(run, $"The folder {run.Folder} does not exist.");
        (int exit, string top) = await GitAsync(run.Folder, "rev-parse", "--show-toplevel");
        string? repo = exit == 0 ? Path.GetFullPath(top.Trim()).TrimEnd('\\') : null;
        if (repo == null)
        {
            IReadOnlyList<string> repos = await System.Threading.Tasks.Task.Run(() => ClaudeProjects.ReposUnder(run.Folder, 3));
            if (repos.Count == 0) return Wait(run, $"There is no git repository in {run.Folder} (3 levels down).");
            repo = repos.Count == 1 ? repos[0] : await PickRepoAsync(run, repos, cancel);
            if (repo == null) return false;
        }
        run.Repo = repo;
        if (run.BaseBranch.Length == 0) run.BaseBranch = await CurrentBranchAsync(repo);
        if (run.BaseBranch.Length == 0)
        {
            run.Repo = repo;
            return Wait(run, $"{repo} has no branch with a commit to start from (a new repo with nothing committed yet?). Commit first, or name the base branch.");
        }
        // a verify command used here before is a hint (its environment and flags), not this change's test selection
        if (run.Verify.Length == 0) run.VerifyHint = AppSettings.Get("factoryVerify:" + repo.ToLowerInvariant()) ?? "";
        run.Branch = $"devbreak/{Slug(run.Title)}-{run.Id}";
        Log(run, $"The repo: {repo} (base {run.BaseBranch}).");
        return true;
    }

    // several repos in the folder: Claude reads the task (Jira and specs with the tools on the machine, changing
    // nothing) and the repos' fingerprints, and names the one the task is for
    private static async Task<string?> PickRepoAsync(FactoryRun run, IReadOnlyList<string> repos, CancellationToken cancel)
    {
        string list = string.Join("\n", repos.Select((r, i) => $"{i + 1}. {r} - {ClaudeProjects.Fingerprint(r)}"));
        string task = run.Source switch
        {
            FactorySource.Jira => $"The task is the Jira issue {run.Task}; read it with the tools you have on this machine.",
            FactorySource.Spec => $"The task is the spec at {run.Task} (a file or a link); read it with the tools you have on this machine.",
            _ => "The task:\n" + run.Task,
        };
        string prompt = $"""
            A developer gave a coding task and a folder with several git repositories in it. Find the one repository the
            task has to be implemented in. Do not change anything - read only.
            {task}

            THE REPOSITORIES:
            {list}

            You may look inside them. End your reply with one line: REPO: <number>. If none fits, or the task needs
            several of them, end with: REPO: NONE <why>.
            """;
        // a typed task needs only the files: read-only tools, enforced. A Jira issue or a spec needs the machine's own
        // tools (MCP servers, CLIs) to be read - the same full access the run's plan turn gets - minus editing and publishing.
        string[] tools = run.Source == FactorySource.Typed
            ? ["--tools", "Read,Grep,Glob", "--allowedTools", "Read,Grep,Glob"]
            : ["--permission-mode", "bypassPermissions", "--disallowedTools", "Edit", "Write", "NotebookEdit", .. NoPublish[1..]];
        string answer = await ClaudeOnceAsync(run, run.Folder, prompt, tools, "Pick the repo", cancel);
        string pick = answer.Replace("\r", "").Split('\n').Select(l => l.Trim().Trim('*', '`').Trim()).LastOrDefault(l => l.StartsWith("REPO:", StringComparison.OrdinalIgnoreCase)) ?? "";
        string value = pick.Length > 5 ? pick[5..].Trim() : "";
        Log(run, $"{repos.Count} repos in the folder; Claude picked: {value}");
        if (int.TryParse(value.Split(' ')[0], out int n) && n >= 1 && n <= repos.Count) return repos[n - 1];
        Wait(run, value.StartsWith("NONE", StringComparison.OrdinalIgnoreCase)
            ? "Claude found no single repo for the task: " + value[4..].Trim()
            : "Claude did not name a repo:\n" + answer);
        return null;
    }

    // where the run works: the developer's own checkout (the default) on the run's new branch - no copy, its
    // dependencies already there - and when the checkout cannot be used, the run asks; or, when the developer chose
    // it, a separate worktree beside the repo
    private static async Task<bool> WorktreeAsync(FactoryRun run, CancellationToken cancel)
    {
        (int exit, string output) = await GitSpanAsync(run, run.Repo, "rev-parse", run.BaseBranch);
        if (exit != 0) return Wait(run, $"Can't find branch {run.BaseBranch} in {run.Repo}: {output.Trim()}");
        run.BaseCommit = output.Trim();
        cancel.ThrowIfCancellationRequested();
        bool branchExists = (await GitAsync(run.Repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + run.Branch)).Exit == 0;
        if (run.UseWorktree)
        {
            // beside the repo, so paths are hardly longer than in the checkout (a deep repo passes Windows' 260
            // characters under a long folder), and long paths on for the checkout of its files
            run.Worktree = Path.Combine(Path.GetDirectoryName(run.Repo)!, $"{Path.GetFileName(run.Repo)}-{run.Id[..4]}");
            Enter(run, FactoryStage.Worktree, $"New worktree {run.Worktree} on branch {run.Branch}, from {run.BaseBranch}.");
            string[] add = branchExists
                ? ["-c", "core.longpaths=true", "worktree", "add", run.Worktree, run.Branch]
                : ["-c", "core.longpaths=true", "worktree", "add", run.Worktree, "-b", run.Branch, run.BaseCommit];
            (exit, output) = await GitSpanAsync(run, run.Repo, add);
            if (exit != 0) return Wait(run, "Could not create the worktree: " + Tail(output.Trim(), 1500));
            return true;
        }
        Enter(run, FactoryStage.Worktree, $"Working in your checkout {run.Repo}, on a new branch {run.Branch} from {run.BaseBranch}.");
        if (await CheckoutProblemAsync(run, "start") is { } problem) return Wait(run, problem);
        // the developer's own local changes (temp files, local settings) stay theirs: noted with a fingerprint each
        run.LocalChanges = [];
        foreach (string path in await ChangedPathsAsync(run.Repo)) run.LocalChanges[path] = Fingerprint(run.Repo, path);
        if (run.LocalChanges.Count > 0)
        {
            Log(run, $"Your {run.LocalChanges.Count} local change{(run.LocalChanges.Count == 1 ? "" : "s")} stay yours - not committed, and the run " +
                "stops and asks if the change needs to touch them: " + string.Join(", ", run.LocalChanges.Keys.Take(20)) +
                (run.LocalChanges.Count > 20 ? ", ..." : ""));
        }
        run.ReturnTo = await HeadAsync(run.Repo);
        (exit, output) = branchExists
            ? await GitSpanAsync(run, run.Repo, "switch", run.Branch)
            : await GitSpanAsync(run, run.Repo, "switch", "-c", run.Branch, run.BaseCommit);
        if (exit != 0) return Wait(run, $"Could not switch your checkout to {run.Branch}: {Tail(output.Trim(), 800)}");
        run.InCheckout = true;
        run.Worktree = run.Repo;
        Log(run, $"Your checkout is on {run.Branch}; it goes back to {run.ReturnTo} when the run is done.");
        return true;
    }

    // why the developer's checkout cannot be used now - another run working in it, or git cannot read it - as a
    // question; null when it can. The developer's own local changes do not block a run (they stay theirs).
    private static async Task<string?> CheckoutProblemAsync(FactoryRun run, string what)
    {
        if (Runs.FirstOrDefault(r => r != run && r.Working && r.InCheckout && r.Repo.Equals(run.Repo, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            return $"Another run (\"{other.Title}\") is working in your checkout {run.Repo}, so this one cannot {what} there. " +
                "Wait for it and press Continue - or press Use a separate worktree to run this one beside it.";
        }
        (int code, string status) = await GitAsync(run.Repo, "status", "--porcelain");
        return code == 0 ? null : $"git cannot read your checkout {run.Repo}: {status.Trim()}";
    }

    // the files with uncommitted changes in a folder (modified, staged, new, deleted; the new name of a rename)
    private static async Task<List<string>> ChangedPathsAsync(string dir)
    {
        (int code, string status) = await GitAsync(dir, "status", "--porcelain", "-z", "--untracked-files=all");
        if (code != 0) return [];
        var paths = new List<string>();
        string[] entries = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Length < 4) continue;
            paths.Add(entries[i][3..].Replace('\\', '/'));
            // a rename or copy is followed by its old name
            if (entries[i][0] is 'R' or 'C') i++;
        }
        return paths;
    }

    // what a file holds now, as a fingerprint - to see whether the run changed a file the developer had changed
    private static string Fingerprint(string dir, string path)
    {
        string file = Path.Combine(dir, path.Replace('/', '\\'));
        if (!File.Exists(file)) return "missing";
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    // the run's own uncommitted changes: what changed in its folder, besides the developer's local changes it left alone
    private static async Task<List<string>> RunChangesAsync(FactoryRun run, string dir) =>
        (await ChangedPathsAsync(dir)).Where(p => !run.LocalChanges.ContainsKey(p) || Fingerprint(dir, p) != run.LocalChanges[p]).ToList();

    // the developer's local changes the run's change has touched (their content is not what it was)
    private static List<string> TouchedLocalChanges(FactoryRun run) =>
        run.InCheckout ? run.LocalChanges.Where(kv => Fingerprint(run.Repo, kv.Key) != kv.Value).Select(kv => kv.Key).ToList() : [];

    // stops the run when its change touched the developer's own local changes - they decide what happens to them
    private static bool LocalChangesTouched(FactoryRun run)
    {
        List<string> touched = TouchedLocalChanges(run);
        if (touched.Count == 0) return false;
        Wait(run, $"The change touched {(touched.Count == 1 ? "a file that holds" : "files that hold")} your own local changes: {string.Join(", ", touched)}. " +
            "The run does not commit your changes, so decide: undo the run's edits there and press Continue - or commit or " +
            "stash your own changes first, or run it in a separate worktree.");
        return true;
    }

    // the branch the checkout is on, or its commit when it is detached
    private static async Task<string> HeadAsync(string repo)
    {
        (_, string head) = await GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD");
        return head.Trim() != "HEAD" ? head.Trim() : (await GitAsync(repo, "rev-parse", "HEAD")).Output.Trim();
    }

    // a run in the developer's checkout goes on (continue, feedback, PR anyway) on its own branch: the checkout is
    // switched there when it is clean; the run asks when it is not
    private static async Task<bool> OnRunBranchAsync(FactoryRun run)
    {
        if (!run.InCheckout || await HeadAsync(run.Repo) == run.Branch) return true;
        if (await CheckoutProblemAsync(run, "switch to its branch " + run.Branch) is { } problem) return Wait(run, problem);
        run.ReturnTo = await HeadAsync(run.Repo);
        (int exit, string output) = await GitSpanAsync(run, run.Repo, "switch", run.Branch);
        if (exit != 0) return Wait(run, $"Could not switch your checkout to {run.Branch}: {Tail(output.Trim(), 800)}");
        Log(run, $"Your checkout is on {run.Branch} again; it goes back to {run.ReturnTo} when this is done.");
        return true;
    }

    // a run in the developer's checkout is done for now: back to their branch when its work is committed; otherwise
    // the checkout stays on the run's branch, and the run says so
    private static async Task LeaveCheckoutAsync(FactoryRun run)
    {
        if (!run.InCheckout || run.ReturnTo.Length == 0 || await HeadAsync(run.Repo) != run.Branch) return;
        // the run's own work is committed: back to the developer's branch, their local changes carried along
        if ((await RunChangesAsync(run, run.Repo)).Count > 0)
        {
            string stay = $"Your checkout stays on {run.Branch}: the run's changes are not committed. Continue the run - or commit or stash them yourself to switch back.";
            run.Note = (run.Note.Length > 0 ? run.Note + "\n" : "") + stay;
            Log(run, stay, ok: false);
            return;
        }
        (int exit, string output) = await GitSpanAsync(run, run.Repo, "switch", run.ReturnTo.Length == 40 ? "--detach" : "--no-guess", run.ReturnTo);
        Log(run, exit == 0 ? $"Your checkout is back on {run.ReturnTo}." : $"Could not switch your checkout back to {run.ReturnTo}: {output.Trim()}", ok: exit == 0);
    }

    /// <summary>The developer's checkout could not be used: this run goes on in a separate worktree beside the repo.</summary>
    public static Task UseWorktreeAsync(FactoryRun run)
    {
        if (run.Working || run.InCheckout) return Task.CompletedTask;
        run.UseWorktree = true;
        Save();
        return ContinueAsync(run);
    }

    private static async Task<bool> PlanAsync(FactoryRun run, CancellationToken cancel)
    {
        Enter(run, FactoryStage.Plan, "Claude reads the task and plans.");
        string task = run.Source switch
        {
            FactorySource.Jira => $"""
                The task is the Jira issue {run.Task}. Read it with the tools you have on this machine: its summary,
                description, acceptance criteria and comments.
                """,
            FactorySource.Spec => $"""
                The task is the spec at {run.Task} (a file or a link). Read all of it with the tools you have on this machine.
                """,
            _ => "The task:\n" + run.Task,
        };
        string verify = run.Verify.Length > 0
            ? $"The command that verifies this repo is: {run.Verify}"
            : $"""
              End with one line: VERIFY: <one shell command, run from this folder in a fresh PowerShell>, chosen by
              regression risk, not the whole suite by habit: it compiles everything, and runs the tests that cover the
              code you will change and the code that uses it (find its usages) - name the test classes. Run the full
              suite only when the change touches shared code that many places use, and say so. Make the tests really run
              (no build-cache result). It runs in a new shell with the machine's default environment: include what your
              own commands needed, e.g. $env:JAVA_HOME='...'; before the build. Say in one line before it why these
              tests are the regression scope.{(run.VerifyHint.Length > 0 ? $"\nThis repo was verified before with: {run.VerifyHint}\nReuse its environment and flags; choose the tests for this change." : "")}
              """;
        string prompt = $"""
            You are working in {(run.InCheckout ? "the developer's own checkout of " + run.Repo : "a separate git worktree of " + run.Repo)}, on a new branch {run.Branch} (from {run.BaseBranch}).
            {task}

            First, understand the mission and plan - do not change any file yet. Use the skills, MCP servers and tools on
            this machine that fit the task (ones that run the app or its services, read logs or data, inspect or debug a
            running service) to see the current behaviour and where it lives, not only the code. Learn how this repo is
            built, run and tested (its README, build files and scripts), so the plan and the verify command fit it, and
            say how the change can be seen in the running app. If you cannot read the task, reply with one line:
            CANNOT_READ: <why>. Otherwise reply with a short title for the work on the first line (TITLE: <title>), then
            the plan as short numbered steps, naming the files you will change and the tests you will add or run.
            Then choose the proof this feature needs - what a careful reviewer would want to see before believing it
            works - and end with one line: EVIDENCE: <comma-separated kinds from: {string.Join(", ", EvidenceKinds)}>.
            (A UI change: screenshots, often a video of the interaction. A logic change: red-green, coverage, mutation,
            crap. A service: api, logs, trace. Pick only what proves this change.)
            If the task leaves a decision that is the developer's to make - behaviour, naming, defaults, which of several
            options - do not guess: after the plan, add a line QUESTIONS: and then the numbered questions, each with the
            answer you suggest. The run waits for the answers before building. Leave QUESTIONS out when nothing is open.
            {verify}
            """;
        (string answer, string? error) = await TurnAsync(run, isNew: true, prompt, "Plan", cancel);
        if (error != null) return Wait(run, "The plan turn failed: " + error);
        if (Line(answer, "CANNOT_READ:") is { } why) return Wait(run, "Claude could not read the task: " + why);
        if (Line(answer, "TITLE:") is { Length: > 0 } title && run.Source != FactorySource.Typed) run.Title = title;
        if (run.Verify.Length == 0)
        {
            run.Verify = Line(answer, "VERIFY:")?.Trim('`', ' ') ?? "";
            if (run.Verify.Length == 0) return Wait(run, "The plan named no verify command. Add one and start again.");
        }
        run.Plan = answer;
        run.EvidencePlan = (Line(answer, "EVIDENCE:") ?? "").Split(',', ';').Select(k => k.Trim().Trim('`', '*', '.').ToLowerInvariant())
            .Where(k => EvidenceKinds.Contains(k)).Distinct().ToList();
        Log(run, "Plan:\n" + answer);
        // decisions that are the developer's: the run asks before it builds
        if (Questions(answer) is { Length: > 0 } questions)
        {
            throw new NeedsYouException("The plan has questions for you. Answer them in the box below and press Send to the run - " +
                "the ones you skip take the suggested answer (or press Continue to take all the suggestions):\n" + questions);
        }
        return true;
    }

    // the text after a line starting with a marker, up to a line starting with the end marker (or the end)
    private static string Block(string text, string marker, string end)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim().TrimStart('*', '#', ' ').StartsWith(marker, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return "";
        var block = new List<string> { lines[start].Trim().TrimStart('*', '#', ' ')[marker.Length..].Trim().Trim('*').Trim() };
        for (int i = start + 1; i < lines.Length && !lines[i].Trim().TrimStart('*', '#', ' ').StartsWith(end, StringComparison.OrdinalIgnoreCase); i++) block.Add(lines[i]);
        return string.Join("\n", block).Trim();
    }

    // the plan's QUESTIONS block: everything after the QUESTIONS: line up to the EVIDENCE: or VERIFY: line
    private static string Questions(string plan)
    {
        string[] lines = plan.Replace("\r", "").Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim().TrimStart('*', '#', ' ').StartsWith("QUESTIONS:", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return "";
        var block = new List<string>();
        string first = lines[start].Trim().TrimStart('*', '#', ' ')["QUESTIONS:".Length..].Trim().Trim('*').Trim();
        if (first.Length > 0) block.Add(first);
        for (int i = start + 1; i < lines.Length; i++)
        {
            string l = lines[i].Trim().TrimStart('*', '#', ' ');
            if (l.StartsWith("EVIDENCE:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("VERIFY:", StringComparison.OrdinalIgnoreCase)) break;
            block.Add(lines[i]);
        }
        string text = string.Join("\n", block).Trim();
        return text.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : text;
    }

    private static async Task<bool> BuildAsync(FactoryRun run, CancellationToken cancel)
    {
        Enter(run, FactoryStage.Build, "Claude implements the plan.");
        (string answer, string? error) = await TurnAsync(run, isNew: false, """
            Now implement the plan in this folder. Change only what the task needs, and add or update tests for it.
            Keep it fast: run only the tests for what you changed - Dev break runs the full verify command right after -
            and do not reinstall dependencies that are already installed.
            Do not commit, push or open a pull request - that comes after the checks. Reply with a short summary of
            what you changed.
            """, "Build", cancel);
        if (error != null) return Wait(run, "The build turn failed: " + error);
        Log(run, "Build:\n" + answer);
        return true;
    }

    // verify, fixing up to MaxFixes times; prove it in the running app, fixing up to MaxFixes times; then review,
    // fixing what the reviewer finds, up to MaxReviews rounds
    private static async Task<bool> CheckAsync(FactoryRun run, CancellationToken cancel)
    {
        for (int review = 1; ; review++)
        {
            if (!await VerifyAsync(run, cancel)) return false;
            if (!await ProveAsync(run, cancel)) return false;
            Enter(run, FactoryStage.Review, $"A reviewer that can only read checks the change (round {review}).");
            (bool pass, string findings) = await ReviewAsync(run, cancel);
            run.Review = findings;
            Log(run, (pass ? "Review: PASS\n" : "Review: FIX\n") + findings, ok: pass);
            if (pass) return true;
            if (review == MaxReviews) return Wait(run, $"The reviewer still found problems after {MaxReviews} rounds.");
            Enter(run, FactoryStage.Build, "Claude fixes what the reviewer found.");
            (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
                A reviewer checked your change against the task and the plan and found:
                {findings}

                Fix what is right in it (say why if you leave something). Do not commit or push. Reply with what you changed.
                """, $"Fix review round {review}", cancel);
            if (error != null) return Wait(run, "The fix turn failed: " + error);
            Log(run, "Fix:\n" + answer);
        }
    }

    private static async Task<bool> VerifyAsync(FactoryRun run, CancellationToken cancel)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (LocalChangesTouched(run)) return false;
            if ((await RunChangesAsync(run, run.Worktree)).Count == 0) return Wait(run, "Claude changed no files.");
            Enter(run, FactoryStage.Verify, $"Dev break runs the verify command: {run.Verify}");
            FactorySpan span = Begin(run, "verify", $"Verify (attempt {attempt})", run.Verify);
            (int exit, string output) = await ShellAsync(run.Worktree, run.Verify, cancel);
            span.Error = exit == 0 ? "" : $"exit {exit}";
            Finish(run, span, $"exit {exit}\n{Tail(output, 20000)}");
            run.VerifyResult = $"`{run.Verify}` exited {exit}. The end of its output:\n{Tail(output, 3000)}";
            Log(run, $"Verify exit {exit}:\n```\n{Tail(output, 3000)}\n```", ok: exit == 0);
            if (exit == 0) return true;
            if (attempt > MaxFixes) return Wait(run, $"The verify command still fails after {MaxFixes} fixes.");
            Enter(run, FactoryStage.Build, $"Claude fixes the failure (fix {attempt} of {MaxFixes}).");
            (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
                The verify command `{run.Verify}` failed with exit code {exit}. The end of its output:
                {Tail(output, 6000)}

                Find the cause and fix it. Fix the code, not the test, unless the test itself is wrong (then say why).
                If the cause is the verify command itself or its environment (a wrong JDK, a missing setting) and not
                the code, do not change code or build files to get around it: end your reply with one line
                VERIFY: <the corrected command> - it runs in a new PowerShell with the machine's default environment.
                Do not commit or push. Reply with what you changed.
                """, $"Fix verify failure {attempt}", cancel);
            if (error != null) return Wait(run, "The fix turn failed: " + error);
            Log(run, "Fix:\n" + answer);
            // the command was the problem: the corrected one is used from now on, and remembered for the repo
            if (Line(answer, "VERIFY:")?.Trim('`', ' ') is { Length: > 0 } corrected && corrected != run.Verify)
            {
                Log(run, $"The verify command is corrected: {corrected}");
                run.Verify = corrected;
                AppSettings.Set("factoryVerify:" + run.Repo.ToLowerInvariant(), corrected);
            }
        }
    }

    /// <summary>Where a run's evidence goes (screenshots, responses, log excerpts): next to its exported trace.</summary>
    public static string ProofDir(FactoryRun run) => Path.Combine(AppContext.BaseDirectory, "traces", run.Id + "-proof");

    // the change in the running app: Claude starts what it needs, exercises what the task changed and saves the
    // evidence; a failure goes back to Claude (then verify again), up to MaxFixes times; SKIPPED when it cannot run here
    private static async Task<bool> ProveAsync(FactoryRun run, CancellationToken cancel)
    {
        for (int attempt = 1; ; attempt++)
        {
            string dir = ProofDir(run);
            Directory.CreateDirectory(dir);
            Enter(run, FactoryStage.Prove, "Claude runs the app with the change and checks it works.");
            string planned = run.EvidencePlan.Count > 0 ? string.Join(", ", run.EvidencePlan) : "(the plan chose none - pick what proves this change)";
            (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
                Now prove the change works, the way a careful developer would before opening a PR. Start what it needs -
                the app and the services it depends on - with the skills, MCP servers and tools on this machine, from
                this folder, exercise exactly what the task changed, and stop what you started when you are done.

                The proof this feature needs, from your plan: {planned}
                How to produce each kind:
                {EvidenceGuide}
                Save every file into {dir} (not into the repo), and write {Path.Combine(dir, ManifestName)}: a JSON array,
                one entry per piece of proof:
                [{"{"}"kind": "<kind>", "file": "<file name in that folder, or empty>", "proves": "<what it shows, one line>",
                  "result": "pass|fail|skipped", "detail": "<the numbers or the reason>"{"}"}]

                Do not commit or push. Reply with what you ran and what you saw. If the task asked for something to be
                handed back - a curl or command to try it, an answer, a link - give it ready to use after a line RESULT:
                (as it works now, checked). Then end with exactly one line:
                PROOF: PASS - it works as the task asks
                PROOF: FAIL <what is wrong>
                PROOF: SKIPPED <why it cannot be run on this machine>
                """, $"Prove (attempt {attempt})", cancel);
            if (error != null) return Wait(run, "The prove turn failed: " + error);
            run.Proof = answer;
            if (Block(answer, "RESULT:", "PROOF:") is { Length: > 0 } result) run.Result = result;
            run.Evidence = ReadManifest(dir);
            string verdict = Line(answer, "PROOF:") ?? "";
            bool pass = verdict.StartsWith("PASS", StringComparison.OrdinalIgnoreCase);
            bool skipped = verdict.StartsWith("SKIPPED", StringComparison.OrdinalIgnoreCase);
            int files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
            List<string> missing = MissingEvidence(run);
            Log(run, $"Proof: {(verdict.Length > 0 ? verdict : "no verdict")} ({files} evidence file{(files == 1 ? "" : "s")}, " +
                $"{run.Evidence.Count} in the manifest{(missing.Count > 0 ? "; planned but missing: " + string.Join(", ", missing) : "")})\n" +
                string.Join("\n", run.Evidence.Select(e => $"- {e.Kind} - {e.Result}: {e.Proves}{(e.Detail.Length > 0 ? " (" + e.Detail + ")" : "")}")) +
                "\n\n" + answer, ok: pass || skipped);
            if (pass || skipped) return true;
            if (verdict.Length == 0) return Wait(run, "Claude ended the app check without a PROOF: line.");
            if (attempt > MaxFixes) return Wait(run, $"The app check still fails after {MaxFixes} fixes.");
            Enter(run, FactoryStage.Build, $"Claude fixes what the app check found (fix {attempt} of {MaxFixes}).");
            (string fix, string? fixError) = await TurnAsync(run, isNew: false, $"""
                Running the app showed a problem: {verdict}
                Find the cause and fix it. Do not commit or push. Reply with what you changed.
                """, $"Fix app check {attempt}", cancel);
            if (fixError != null) return Wait(run, "The fix turn failed: " + fixError);
            Log(run, "Fix:\n" + fix);
            if (!await VerifyAsync(run, cancel)) return false;
        }
    }

    private const string ManifestName = "evidence.json";

    // the evidence manifest Claude wrote; empty when there is none or it is not the expected JSON
    private static List<FactoryEvidence> ReadManifest(string dir)
    {
        string file = Path.Combine(dir, ManifestName);
        if (!File.Exists(file)) return [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            string Str(JsonElement e, string key) => e.TryGetProperty(key, out JsonElement v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
            return doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => new FactoryEvidence
            {
                Kind = Str(e, "kind").ToLowerInvariant(), File = Str(e, "file"), Proves = Str(e, "proves"),
                Result = Str(e, "result").ToLowerInvariant(), Detail = Str(e, "detail"),
            }).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // the kinds the plan chose that the manifest does not have
    private static List<string> MissingEvidence(FactoryRun run) =>
        run.EvidencePlan.Where(k => !run.Evidence.Any(e => e.Kind == k)).ToList();

    // the evidence, as the reviewer and the PR description read it
    private static string EvidenceSummary(FactoryRun run)
    {
        if (run.Evidence.Count == 0) return "No evidence manifest.";
        string list = string.Join("\n", run.Evidence.Select(e => $"- {e.Kind}: {e.Result} - {e.Proves}{(e.Detail.Length > 0 ? " (" + e.Detail + ")" : "")}{(e.File.Length > 0 ? " [" + e.File + "]" : "")}"));
        List<string> missing = MissingEvidence(run);
        return list + (missing.Count > 0 ? "\nPlanned but not produced: " + string.Join(", ", missing) : "");
    }

    private static async Task<(bool Pass, string Findings)> ReviewAsync(FactoryRun run, CancellationToken cancel)
    {
        // the change against the base - the run's files only: new files are marked "intent to add" so the diff shows
        // them, and the developer's local changes are left out (their files and their index are not touched)
        List<string> mine = await RunChangesAsync(run, run.Worktree);
        string[] untracked = (await GitAsync(run.Worktree, "ls-files", "--others", "--exclude-standard", "-z")).Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => mine.Contains(p.Replace('\\', '/'))).ToArray();
        if (untracked.Length > 0) await GitAsync(run.Worktree, ["add", "--intent-to-add", "--", .. untracked]);
        string[] exclude = run.LocalChanges.Keys.Where(k => !mine.Contains(k)).Select(k => ":(exclude)" + k).ToArray();
        (_, string diff) = await GitAsync(run.Worktree, ["diff", run.BaseCommit, "--", ".", .. exclude]);
        string prompt = $"""
            You review a change before it becomes a pull request. Be strict and concrete; you can read the repo.
            THE TASK ({run.Source}):
            {run.Task}

            THE PLAN:
            {run.Plan}

            THE DIFF (against {run.BaseBranch}):
            {(diff.Length > 60000 ? diff[..60000] + "\n... (cut)" : diff)}

            THE EVIDENCE - you can only read, so this is what was run for you:
            The verify command, run by the tool itself on this exact worktree: {run.VerifyResult}
            The proof the plan asked for: {(run.EvidencePlan.Count > 0 ? string.Join(", ", run.EvidencePlan) : "(none chosen)")}
            The evidence produced (its manifest):
            {EvidenceSummary(run)}
            The check in the running app, by the coding agent:
            {(run.Proof.Length > 0 ? Tail(run.Proof, 4000) : "(none)")}
            Weak or missing proof for what the task changes (a planned kind not produced, a mutation score that leaves
            the changed logic untested, uncovered changed lines) is a real problem.

            Judge the evidence; do not ask for a command to be run that was run above, and do not report something only
            because your read-only tools cannot see it (say "not verified" in a finding only when it really matters).
            Report only real problems: bugs, a requirement of the task that is missing, changes the task did not ask
            for, missing or weak tests, security issues. No style nits. Reply with VERDICT: PASS or VERDICT: FIX on the
            first line, then the findings as a short list (file:line - problem - what to do). PASS when nothing real is left.
            """;
        string answer = await ClaudeOnceAsync(run, run.Worktree, prompt, ["--tools", "Read,Grep,Glob", "--allowedTools", "Read,Grep,Glob"], "Review", cancel);
        bool pass = Line(answer, "VERDICT:")?.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) == true;
        int newline = answer.IndexOf('\n');
        return (pass, newline > 0 ? answer[(newline + 1)..].Trim() : answer.Trim());
    }

    private static async Task<bool> PullRequestAsync(FactoryRun run, CancellationToken cancel, bool anyway = false)
    {
        bool update = run.PrUrl.Length > 0;
        Enter(run, FactoryStage.PullRequest, update ? "Claude commits and pushes, so the draft PR updates." : "Claude commits, pushes the branch and opens a draft PR.");
        string checks = anyway
            ? $"""
              The checks did NOT all pass: {run.Note} The developer asked for the draft PR anyway - say clearly in its
              description what failed. Last review:
              {run.Review}
              """
            : $"""
              The checks passed: `{run.Verify}` exited 0, the app check said {Line(run.Proof, "PROOF:") ?? "nothing"}, and the
              reviewer passed the change. Review notes:
              {run.Review}
              """;
        if (LocalChangesTouched(run)) return false;
        if (run.LocalChanges.Count > 0)
        {
            checks += "\nThese files hold the developer's own local changes - never stage or commit them, and leave them exactly " +
                "as they are; commit only this change's files (stage them by name, not with git add -A or git add .): " +
                string.Join(", ", run.LocalChanges.Keys) + ".";
        }
        if (run.Result.Length > 0) checks += $"\nWhat the task asked to be handed back (put it in the PR description too):\n{run.Result}";
        checks += $"\nThe evidence:\n{EvidenceSummary(run)}\nIts files are in {ProofDir(run)} - list it in the PR description (attach the images or video to the PR if the host's tools can), do not commit it. The app was already run after the last change; do not run it again unless a check on this machine requires it.";
        string link = run.Source == FactorySource.Jira ? $" Link it to the Jira issue {run.Task} the way this repo does." : "";
        // the git host's own MCP server first (Atlassian's for Bitbucket, GitHub's for GitHub), then its CLI or tools
        link += " For the pull request, prefer the git host's official MCP server when one is connected and signed in" +
            " (for example the Atlassian MCP for Bitbucket, the GitHub MCP for GitHub); otherwise use the host's CLI or" +
            " the tools on this machine. Say in your reply which one you used.";
        string todo = update
            ? $"""
              The draft pull request {run.PrUrl} already exists for branch {run.Branch}. Commit the new changes with a
              clear message and push to the same branch, so the PR updates. If the description no longer fits (what
              changed, how it was verified), update it with the tools on this machine. Do not merge.
              Reply with the pull request URL on the last line.
              """
            : $"""
              Now commit all the changes on branch {run.Branch} with a clear message, push the branch to the remote, and
              open a DRAFT pull request into {run.BaseBranch} with the tools on this machine.{link} The description: what
              changed and why, how it was verified (the command and its result), and the review notes. Do not merge.
              Reply with the pull request URL on the last line.
              """;
        (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
            {checks}

            {todo}
            """, update ? "Update the draft PR" : "Draft PR", cancel, publish: true);
        if (error != null) return Wait(run, "The pull request turn failed: " + error);
        // the PR is the URL on the reply's last line, as asked - a link elsewhere in the reply (in a quoted
        // description, say) is not a PR
        string last = answer.Replace("\r", "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        Match url = Regex.Match(last, @"https?://[^\s)>\]""'`*]+");
        if (!url.Success) return Wait(run, "Claude did not give a pull request URL:\n" + answer);
        run.PrUrl = url.Value.TrimEnd('.', ',');
        Log(run, "Draft PR: " + run.PrUrl);
        End(run, FactoryStage.Done, (update ? "Draft PR updated" : "Draft PR opened") + (anyway ? " - with failed checks." : "."));
        return true;
    }

    // what the run taught about this repo: memory notes are saved at once; rules wait for the developer's OK
    private static async Task LearnAsync(FactoryRun run)
    {
        FactoryStage ended = run.Stage;
        string note = run.Note;
        Enter(run, FactoryStage.Learn, "Claude looks back at the run for what to remember.");
        try
        {
            if (run.Repo.Length == 0) throw new InvalidOperationException("the run never found its repo");
            string? memoryDir = await CliSetup.MemoryDirAsync(run.Repo);
            string index = memoryDir != null && File.Exists(Path.Combine(memoryDir, "MEMORY.md")) ? File.ReadAllText(Path.Combine(memoryDir, "MEMORY.md")) : "";
            string log = string.Join("\n\n", run.Log.Select(s => $"[{s.Stage}{(s.Ok ? "" : ", FAILED")}] {Tail(s.Text, 1500)}"));
            // what the developer said: their feedback, their rating, and the lessons they turned down for this repo
            string said = string.Join("\n", run.Feedback.Select(f => "- feedback: " + f.Text));
            if (run.Rating.Length > 0) said += $"\n- rated the run {run.Rating}" + (run.RatingNote.Length > 0 ? ": " + run.RatingNote : "");
            string rejected = string.Join("\n", Runs.Where(r => r.Repo.Equals(run.Repo, StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.Lessons).Where(l => l.Status is "dismissed" or "undone")
                .Select(l => $"- {l.Kind} {l.Name}: {l.Description}" + (l.Feedback.Length > 0 ? $" - why: {l.Feedback}" : "")));
            string already = string.Join("\n", run.Lessons.Select(l => $"- {l.Kind} {l.Name}: {l.Description}"));
            int before = run.Lessons.Count;
            string prompt = $"""
                A coding agent just worked on a task in the repo {run.Repo}. Below is the run: the task, its log (plan,
                build, verify output, fixes, review findings), how it ended and what the developer said about it. Find
                what is worth keeping for the next agent working in this repo, so the same mistakes and detours do not
                happen again. What the developer said weighs most: it is the truth about whether the run did well.

                THE TASK ({run.Source}): {run.Task}
                HOW IT ENDED: {ended} - {note}
                THE LOG:
                {Tail(log, 24000)}

                WHAT THE DEVELOPER SAID:
                {(said.Trim().Length > 0 ? said.Trim() : "(nothing yet)")}

                WHAT THE REPO'S MEMORY ALREADY SAYS (do not repeat it):
                {(index.Length > 0 ? index : "(nothing yet)")}

                ALREADY LEARNED FROM THIS RUN (do not repeat it):
                {(already.Length > 0 ? already : "(nothing yet)")}

                LESSONS THE DEVELOPER TURNED DOWN FOR THIS REPO (do not suggest anything like them):
                {(rejected.Length > 0 ? rejected : "(none)")}

                WHAT WAS SLOW (the developer wants every run faster - steps and single tool calls, slowest first):
                {SpeedSummary(run)}
                For what was slow and avoidable here, add a lesson that makes the next run faster: e.g. dependencies are
                already installed (do not reinstall), run only the tests for the change, a faster command for the same
                check, a step that waited for something it did not need.

                Lessons can be:
                - memory: a fact about this repo the run had to discover (a build or test quirk, a command, where things
                  live, a gotcha). Under 100 words. Most lessons.
                - claude-md: one imperative rule line for the repo's CLAUDE.md - only for a mistake the agent made that
                  a standing rule would prevent.
                - hook: only for a check that must always run; the content is a JSON object of Claude Code hook settings,
                  e.g. {HookExample}.
                - skill: only for a multi-step procedure that will repeat here; numbered steps, under 250 words.
                At most 4 lessons; only ones a future run would really use. Reply in exactly this format, one block each:
                LESSON: <memory|claude-md|hook|skill> | <short-kebab-case-name> | <one-line description>
                <content>
                END
                Or reply with the single word NONE.
                """;
            string answer = await ClaudeOnceAsync(run, Path.GetTempPath(), prompt,
                ["--tools", "", "--strict-mcp-config", "--setting-sources", ""], "Learn", CancellationToken.None);
            foreach (FactoryLesson lesson in ParseLessons(answer))
            {
                if (lesson.Kind == "memory" && memoryDir != null)
                {
                    lesson.Content += $"\n\nLearned by a Dev break factory run on {DateTime.Now:yyyy-MM-dd} ({run.Title}).";
                    lesson.File = SaveMemory(memoryDir, lesson);
                    lesson.Status = "saved";
                }
                run.Lessons.Add(lesson);
            }
            List<FactoryLesson> learned = run.Lessons[before..];
            Log(run, learned.Count == 0 ? "Nothing new to remember." :
                $"Learned {learned.Count}: {learned.Count(l => l.Status == "saved")} saved to memory, {learned.Count(l => l.Status == "pending")} rules wait for you.");
        }
        catch (Exception ex)
        {
            Log(run, "Could not learn from the run: " + ex.Message, ok: false);
        }
        run.Stage = ended;
        run.Note = note;
        Save();
        Notify(run);
    }

    // --- lessons ---

    private static IEnumerable<FactoryLesson> ParseLessons(string output)
    {
        string[] lines = output.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("LESSON:", StringComparison.Ordinal)) continue;
            string[] parts = lines[i]["LESSON:".Length..].Split('|').Select(p => p.Trim()).ToArray();
            int end = Array.FindIndex(lines, i + 1, l => l.Trim() == "END");
            if (end < 0) end = lines.Length;
            if (parts.Length >= 3 && parts[0] is "memory" or "claude-md" or "hook" or "skill")
            {
                yield return new FactoryLesson
                {
                    Kind = parts[0], Name = Slug(parts[1]), Description = string.Join(" | ", parts[2..]),
                    Content = string.Join("\n", lines[(i + 1)..end]).Trim(),
                };
            }
            i = end;
        }
    }

    // one note per file in the memory folder, listed in its MEMORY.md
    private static string SaveMemory(string dir, FactoryLesson lesson)
    {
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, lesson.Name + ".md");
        for (int i = 2; File.Exists(file); i++) file = Path.Combine(dir, $"{lesson.Name}-{i}.md");
        File.WriteAllText(file, $"---\nname: {lesson.Name}\ndescription: {lesson.Description}\nmetadata:\n  type: project\n---\n\n{lesson.Content.Trim()}\n");
        string index = Path.Combine(dir, "MEMORY.md");
        string existing = File.Exists(index) ? File.ReadAllText(index) : "";
        File.AppendAllText(index, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") +
            $"- [{lesson.Name}]({Path.GetFileName(file)}) — {lesson.Description}\n");
        return file;
    }

    /// <summary>Takes a saved memory note back out: its file and its line in MEMORY.md.</summary>
    public static void Undo(FactoryRun run, FactoryLesson lesson, string why = "")
    {
        if (lesson.Status != "saved" || lesson.File == null) return;
        lesson.Feedback = why.Trim();
        string name = Path.GetFileName(lesson.File);
        if (File.Exists(lesson.File)) File.Delete(lesson.File);
        string index = Path.Combine(Path.GetDirectoryName(lesson.File)!, "MEMORY.md");
        if (File.Exists(index))
        {
            File.WriteAllLines(index, File.ReadAllLines(index).Where(l => !l.Contains($"]({name})", StringComparison.Ordinal)));
        }
        lesson.Status = "undone";
        Save();
        Notify(run);
    }

    /// <summary>Applies a rule the developer approved; returns the file it went into.</summary>
    public static string Apply(FactoryRun run, FactoryLesson lesson)
    {
        string file;
        switch (lesson.Kind)
        {
            case "claude-md":
                file = Path.Combine(run.Repo, "CLAUDE.md");
                string existing = File.Exists(file) ? File.ReadAllText(file) : "";
                File.AppendAllText(file, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + lesson.Content.Trim() + "\n");
                break;
            case "skill":
                file = ClaudeProjects.Save(new ShareSuggestion(new ClaudeProject("", run.Repo, ""), "skill", lesson.Name, lesson.Description, lesson.Content), lesson.Content);
                break;
            case "hook":
                file = Path.Combine(run.Repo, ".claude", "settings.local.json");
                // the JSON object, without a code fence around it
                int open = lesson.Content.IndexOf('{'), close = lesson.Content.LastIndexOf('}');
                if (open < 0 || close < open) throw new InvalidOperationException("the hook is not a JSON object");
                JsonObject hooks = JsonNode.Parse(lesson.Content[open..(close + 1)])?.AsObject()
                    ?? throw new InvalidOperationException("the hook is not a JSON object");
                JsonObject settings = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? [] : [];
                if (settings["hooks"] is not JsonObject target) settings["hooks"] = target = [];
                foreach ((string hookEvent, JsonNode? groups) in hooks)
                {
                    if (target[hookEvent] is not JsonArray list) target[hookEvent] = list = [];
                    foreach (JsonNode? group in groups?.AsArray() ?? []) list.Add(group?.DeepClone());
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                break;
            default:
                throw new InvalidOperationException("not a rule: " + lesson.Kind);
        }
        lesson.Status = "applied";
        lesson.File = file;
        Save();
        Notify(run);
        return file;
    }

    public static void Dismiss(FactoryRun run, FactoryLesson lesson, string why = "")
    {
        lesson.Feedback = why.Trim();
        lesson.Status = "dismissed";
        Save();
        Notify(run);
    }

    // --- running things ---

    // a turn of the run's own Claude Code session, in its worktree; traced as a span
    private static async Task<(string Answer, string? Error)> TurnAsync(FactoryRun run, bool isNew, string prompt, string title,
        CancellationToken cancel, bool publish = false)
    {
        prompt += OutsideRule;
        FactorySpan span = Begin(run, "turn", title, prompt);
        string answer = "";
        string? error = null;
        try
        {
            await ClaudeSessions.RunTurnAsync(run.Worktree, run.SessionId, isNew, prompt, entry =>
            {
                switch (entry.Kind)
                {
                    case SessionEntryKind.Tool:
                        run.Steps++;
                        break;
                    case SessionEntryKind.Answer:
                        answer = entry.Text;
                        break;
                    case SessionEntryKind.Error:
                        error = entry.Text;
                        break;
                }
            }, cancel, publish ? null : NoPublish, root => Observe(run, span, root));
        }
        finally
        {
            span.Error = error ?? (cancel.IsCancellationRequested ? "stopped" : "");
            Finish(run, span, answer);
        }
        cancel.ThrowIfCancellationRequested();
        // Claude needs something changed outside the folder: the developer decides
        if (error == null && Line(answer, "NEEDS_YOU:") is { Length: > 0 } need)
        {
            Log(run, $"{title}:\n{answer}");
            throw new NeedsYouException("Claude needs you: " + need);
        }
        return (answer, error);
    }

    // a one-off claude -p (locate, review, learn), streamed so its tools show in the trace; its answer
    private static async Task<string> ClaudeOnceAsync(FactoryRun run, string folder, string prompt, IEnumerable<string> args, string title, CancellationToken cancel)
    {
        FactorySpan span = Begin(run, "claude", title, prompt);
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = folder, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence" }.Concat(args)) start.ArgumentList.Add(arg);
        string answer = "";
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start claude");
            using CancellationTokenRegistration stop = cancel.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            bool finished = false;
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith('{')) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    Observe(run, span, root);
                    if (root.TryGetProperty("type", out JsonElement t) && t.GetString() == "result")
                    {
                        finished = true;
                        answer = root.TryGetProperty("result", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
                        if (root.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True) span.Error = "claude: " + answer;
                    }
                }
                catch (JsonException)
                {
                    // not a whole JSON line: skip it
                }
            }
            await process.WaitForExitAsync();
            cancel.ThrowIfCancellationRequested();
            if (!finished) span.Error = $"claude exited with {process.ExitCode}: {(await stderr).Trim()}";
        }
        catch (OperationCanceledException)
        {
            span.Error = "stopped";
            throw;
        }
        catch (Exception ex) when (span.Error.Length == 0)
        {
            span.Error = ex.Message;
            throw;
        }
        finally
        {
            Finish(run, span, answer);
        }
        if (span.Error.Length > 0) throw new InvalidOperationException(span.Error);
        return answer;
    }

    // --- speed ---

    /// <summary>A step or one tool call running longer than this is slow: the developer is told, and the run learns from it.</summary>
    public static readonly TimeSpan SlowLimit =
        // DEVBREAK_SLOW_SECONDS sets another limit (for tests and demos); a minute otherwise
        int.TryParse(Environment.GetEnvironmentVariable("DEVBREAK_SLOW_SECONDS"), out int seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMinutes(1);

    /// <summary>
    /// The watchdog, called every few seconds while runs work: a step, or one tool call in it, that has just passed the
    /// slow limit is marked slow and logged; returns what to tell the developer (each only once).
    /// </summary>
    public static List<(FactoryRun Run, string Text)> CheckSlow()
    {
        var alerts = new List<(FactoryRun, string)>();
        DateTime now = DateTime.Now;
        foreach (FactoryRun run in Runs.Where(r => r.Working))
        {
            if (run.Trace.LastOrDefault(s => s.Ended == null) is not { } span) continue;
            if (span.Tools.LastOrDefault(t => t.Ended == null && t.Result == "running" && !t.Slow && now - t.At > SlowLimit) is { } tool)
            {
                tool.Slow = true;
                string text = $"{span.Title}: {tool.Name} {Short(tool.Detail, 80)} has run over {LimitText}.";
                Log(run, "Slow - " + text);
                alerts.Add((run, text));
            }
            if (!span.Slow && now - span.Started > SlowLimit)
            {
                span.Slow = true;
                FactoryToolCall? longest = span.Tools.Where(t => t.Ended != null).MaxBy(t => t.Ended!.Value - t.At);
                string text = $"{span.Title} has run over {LimitText}" +
                    (longest != null ? $" - longest so far: {longest.Name} {Short(longest.Detail, 60)} ({(int)(longest.Ended!.Value - longest.At).TotalSeconds} s)." : ".");
                Log(run, "Slow - " + text);
                alerts.Add((run, text));
            }
        }
        return alerts;
    }

    // the slow steps and tool calls of a run, slowest first - what the Learn step turns into speed lessons
    private static string LimitText => SlowLimit.TotalSeconds < 60 ? $"{SlowLimit.TotalSeconds:0} s" : SlowLimit.TotalMinutes == 1 ? "a minute" : $"{SlowLimit.TotalMinutes:0} minutes";

    private static string SpeedSummary(FactoryRun run)
    {
        var lines = new List<(double Seconds, string Text)>();
        foreach (FactorySpan s in run.Trace.Where(s => s.Ended != null))
        {
            double seconds = (s.Ended!.Value - s.Started).TotalSeconds;
            if (seconds >= 30) lines.Add((seconds, $"step {s.Title}: {seconds:0} s, {s.Tools.Count} tool calls, {s.InputTokens:N0} tokens read"));
            foreach (FactoryToolCall t in s.Tools.Where(t => t.Ended != null))
            {
                double toolSeconds = (t.Ended!.Value - t.At).TotalSeconds;
                if (toolSeconds >= 15) lines.Add((toolSeconds, $"  tool in {s.Title}: {t.Name} {Short(t.Detail, 120)}: {toolSeconds:0} s"));
            }
        }
        return lines.Count == 0 ? "(nothing slow)" : string.Join("\n", lines.OrderByDescending(l => l.Seconds).Take(12).Select(l => l.Text));
    }

    private static string Short(string text, int max)
    {
        string line = text.Replace("\r", "").Replace('\n', ' ');
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    // --- the trace ---

    private static FactorySpan Begin(FactoryRun run, string kind, string title, string prompt)
    {
        var span = new FactorySpan
        {
            Stage = run.Stage, Kind = kind, Title = title, Started = DateTime.Now,
            Prompt = prompt.Length > 100_000 ? prompt[..100_000] + "\n... (cut)" : prompt,
        };
        run.Trace.Add(span);
        Notify(run);
        return span;
    }

    private static void Finish(FactoryRun run, FactorySpan span, string answer)
    {
        span.Ended = DateTime.Now;
        span.Answer = answer;
        foreach (FactoryToolCall tool in span.Tools.Where(t => t.Result == "running")) tool.Result = "no result";
        run.Cost += span.Cost;
        Save();
        Notify(run);
    }

    // one streamed line of claude: a tool it calls, how a tool call ended, and the turn's cost and tokens
    private static void Observe(FactoryRun run, FactorySpan span, JsonElement root)
    {
        string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
        IEnumerable<JsonElement> Blocks() =>
            root.TryGetProperty("message", out JsonElement m) && m.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().ToList() : [];
        if (type == "assistant")
        {
            foreach (JsonElement block in Blocks().Where(b => b.TryGetProperty("type", out JsonElement k) && k.GetString() == "tool_use"))
            {
                span.Tools.Add(new FactoryToolCall
                {
                    At = DateTime.Now, Id = block.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? "" : "",
                    Name = block.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "tool" : "tool",
                    Detail = ClaudeSessions.ToolSummary(block),
                });
            }
            Notify(run);
        }
        else if (type == "user")
        {
            foreach (JsonElement block in Blocks().Where(b => b.TryGetProperty("type", out JsonElement k) && k.GetString() == "tool_result"))
            {
                string id = block.TryGetProperty("tool_use_id", out JsonElement i) ? i.GetString() ?? "" : "";
                if (span.Tools.FirstOrDefault(x => x.Id == id) is not { } tool) continue;
                bool error = block.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True;
                string content = block.TryGetProperty("content", out JsonElement body) ? body.ToString() : "";
                tool.Result = !error ? "ok" : content.Contains("denied", StringComparison.OrdinalIgnoreCase) ? "denied" : "error";
                tool.Ended = DateTime.Now;
            }
            Notify(run);
        }
        else if (type == "result")
        {
            span.Cost = root.TryGetProperty("total_cost_usd", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
            if (root.TryGetProperty("usage", out JsonElement u))
            {
                long Num(string key) => u.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                span.InputTokens = Num("input_tokens") + Num("cache_creation_input_tokens") + Num("cache_read_input_tokens");
                span.OutputTokens = Num("output_tokens");
            }
            span.Denied = root.TryGetProperty("permission_denials", out JsonElement d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0;
        }
    }

    // a git command of the run, traced
    private static async Task<(int Exit, string Output)> GitSpanAsync(FactoryRun run, string folder, params string[] args)
    {
        FactorySpan span = Begin(run, "git", "git " + args[0], "git -C \"" + folder + "\" " + string.Join(" ", args));
        (int exit, string output) = await GitAsync(folder, args);
        span.Error = exit == 0 ? "" : $"exit {exit}";
        Finish(run, span, output.Trim());
        return (exit, output);
    }

    /// <summary>
    /// Writes a run's whole trace to the traces folder next to the app: <c>.json</c> (everything) and <c>.md</c> (to
    /// read). Returns the Markdown file.
    /// </summary>
    public static string Export(FactoryRun run)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "traces");
        Directory.CreateDirectory(dir);
        string name = $"{run.Created:yyyy-MM-dd-HHmm}-{Slug(run.Title)}-{run.Id}";
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
        var md = new StringBuilder();
        const string Fence = "````";
        md.AppendLine($"# {run.Title}").AppendLine();
        md.AppendLine($"- Task ({run.Source}): {run.Task.Replace("\n", " ")}");
        md.AppendLine($"- Folder: {run.Folder}  ·  repo: {run.Repo}  ·  branch: {run.Branch} from {run.BaseBranch}");
        md.AppendLine($"- Ended: {run.Stage} - {run.Note.Replace("\n", " ")}");
        md.AppendLine($"- Cost: ${run.Cost:0.00}  ·  steps: {run.Steps}  ·  PR: {(run.PrUrl.Length > 0 ? run.PrUrl : "none")}");
        if (run.Rating.Length > 0) md.AppendLine($"- Your rating: {run.Rating}{(run.RatingNote.Length > 0 ? " - " + run.RatingNote : "")}");
        foreach (FactoryFeedback f in run.Feedback) md.AppendLine($"- Your feedback ({f.At:HH:mm}): {f.Text.Replace("\n", " ")}");
        md.AppendLine().AppendLine("## Trace").AppendLine();
        foreach (FactorySpan s in run.Trace)
        {
            md.AppendLine($"### {s.Started:HH:mm:ss} · {s.Stage} · {s.Title}{(s.Error.Length > 0 ? " ✗ " + s.Error : "")}");
            md.AppendLine($"{Duration(s)} · ${s.Cost:0.00} · {s.InputTokens:N0} in / {s.OutputTokens:N0} out tokens · {s.Tools.Count} tools{(s.Denied > 0 ? $", {s.Denied} denied" : "")}").AppendLine();
            foreach (FactoryToolCall tool in s.Tools) md.AppendLine($"- `{tool.Name}` {tool.Detail} - {tool.Result}");
            if (s.Tools.Count > 0) md.AppendLine();
            md.AppendLine("Prompt:").AppendLine(Fence).AppendLine(s.Prompt.Trim()).AppendLine(Fence);
            md.AppendLine("Answer:").AppendLine(Fence).AppendLine(s.Answer.Trim()).AppendLine(Fence).AppendLine();
        }
        string proof = ProofDir(run);
        if (Directory.Exists(proof) && Directory.EnumerateFiles(proof, "*", SearchOption.AllDirectories).Any())
        {
            md.AppendLine("## Evidence").AppendLine();
            md.AppendLine($"Planned: {(run.EvidencePlan.Count > 0 ? string.Join(", ", run.EvidencePlan) : "none")}").AppendLine();
            foreach (FactoryEvidence e in run.Evidence) md.AppendLine($"- **{e.Kind}** {e.Result}: {e.Proves}{(e.Detail.Length > 0 ? " (" + e.Detail + ")" : "")}{(e.File.Length > 0 ? " - " + Path.Combine(proof, e.File) : "")}");
            md.AppendLine().AppendLine("Files:");
            foreach (string f in Directory.EnumerateFiles(proof, "*", SearchOption.AllDirectories)) md.AppendLine($"- {f}");
            md.AppendLine();
        }
        md.AppendLine("## Learned").AppendLine();
        foreach (FactoryLesson l in run.Lessons)
        {
            md.AppendLine($"- {l.Kind} **{l.Name}** ({l.Status}{(l.Feedback.Length > 0 ? ", why: " + l.Feedback : "")}): {l.Description}");
        }
        string file = Path.Combine(dir, name + ".md");
        File.WriteAllText(file, md.ToString());
        return file;
    }

    /// <summary>How long a span took ("running" while it runs).</summary>
    public static string Duration(FactorySpan span)
    {
        if (span.Ended is not { } ended) return "running";
        double seconds = (ended - span.Started).TotalSeconds;
        return seconds < 60 ? $"{seconds:0.0} s" : $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s";
    }

    private static async Task<(int Exit, string Output)> GitAsync(string folder, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(folder);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output + await error);
    }

    // the verify command, in PowerShell in the worktree, with its output (both streams); at most 30 minutes
    private static async Task<(int Exit, string Output)> ShellAsync(string folder, string command, CancellationToken cancel)
    {
        var start = new ProcessStartInfo("powershell")
        {
            WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command + "; exit $LASTEXITCODE" }) start.ArgumentList.Add(arg);
        // the command is done when ITS process exits (the Exited event) - not when every process holding its output
        // pipe has closed it: a build tool's daemon started by the command (Gradle) inherits the pipe and lives for
        // hours, and waiting for it hung a verify (measured: 11.4 s for a 12 s child against 0.5 s this way)
        using Process process = new() { StartInfo = start, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource();
        process.Exited += (_, _) => exited.TrySetResult();
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        if (!process.Start()) throw new InvalidOperationException("could not start PowerShell");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (process.HasExited) exited.TrySetResult();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await exited.Task.WaitAsync(timeout.Token);
            await Task.Delay(300, CancellationToken.None); // the last lines in flight
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancel.ThrowIfCancellationRequested();
            lock (output) return (-1, output + "\n(stopped after 30 minutes)");
        }
        lock (output) return (process.ExitCode, output.ToString());
    }

    // --- the log ---

    private static void Enter(FactoryRun run, FactoryStage stage, string text)
    {
        run.Stage = stage;
        Log(run, text);
    }

    private static void Log(FactoryRun run, string text, bool ok = true)
    {
        run.Log.Add(new FactoryStep { At = DateTime.Now, Stage = run.Stage, Text = text, Ok = ok });
        Save();
        Notify(run);
    }

    // the run stops and waits for the developer; returns false so a stage can end with it
    private static bool Wait(FactoryRun run, string why)
    {
        Log(run, why, ok: false);
        End(run, FactoryStage.NeedsYou, why);
        return false;
    }

    private static void End(FactoryRun run, FactoryStage stage, string note)
    {
        run.Stage = stage;
        run.Note = note;
        Save();
        Notify(run);
    }

    private static void Notify(FactoryRun run) => Changed?.Invoke(run);

    public static void Save()
    {
        try
        {
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Runs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // kept in memory; saved with the next change
        }
    }

    private static string? Line(string text, string prefix) =>
        text.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('*', '#', ' ')).Where(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(l => l[prefix.Length..].Trim().Trim('*').Trim()).FirstOrDefault();

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];

    private static string Slug(string name)
    {
        string slug = string.Concat(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "task" : slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }
}
