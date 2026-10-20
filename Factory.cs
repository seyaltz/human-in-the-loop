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

/// <summary>
/// Which department a run belongs to: Development builds a change to a draft PR; Troubleshooting diagnoses a
/// customer's or a site's problem for the call center (reproduce, compare with what works, root cause - no code);
/// Research answers a question about the system; Management thinks through a vision, an idea or a company-wide matter and
/// ends in a decision brief. All but Development change no code and end in a checked answer and a designed report.
/// </summary>
public enum FactoryDepartment { Development, Troubleshooting, Research, Management }

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
/// Something a run learned for the repo: a memory note (in the repo's Claude memory, saved at once, can be undone), or
/// a rule - a rule line, a skill or a hook, kept by the factory outside the repo - that waits for the developer's OK.
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
    /// <summary>How to take an applied rule back out: the text appended, or the file's backup before it changed.</summary>
    public string UndoData { get; set; } = "";
}

/// <summary>An insight kept from a factory run that was deleted.</summary>
public sealed class FactoryInsight
{
    public string Repo { get; set; } = "";
    public string Kind { get; set; } = "insight";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>The title of the run it came from.</summary>
    public string From { get; set; } = "";
    public DateTime Created { get; set; }
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
    /// <summary>What kind of change it asks for: wrong behaviour, missing, change design, bug - or empty.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The revision it asked for (2 is the first round of changes).</summary>
    public int Revision { get; set; }
    /// <summary>What the run understood it to mean, in its own words, before it built.</summary>
    public string Understood { get; set; } = "";
}

/// <summary>A task taken from its source to a draft pull request, in its own worktree and branch.</summary>
public sealed class FactoryRun
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public FactorySource Source { get; set; }
    public FactoryDepartment Department { get; set; }
    /// <summary>
    /// Where the run's knowledge lives: its repo - or, for a folder-wide run (troubleshooting, research or management
    /// that belongs to no single repo), the folder it was given.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Home => Repo.Length > 0 ? Repo : Folder;
    /// <summary>A troubleshooting, research or management run that belongs to no single repo: it works in the folder.</summary>
    public bool FolderWide { get; set; }
    /// <summary>The gate chooses the department when the run starts (the developer chose Auto); false once it has.</summary>
    public bool AutoDepartment { get; set; }
    /// <summary>Why the gate sent it to its department, one line.</summary>
    public string Routing { get; set; } = "";
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
    /// <summary>
    /// No human in the loop: the plan's questions take the suggested answers, the run may change what it needs outside
    /// its folder (backed up, listed), learned rules apply at once, and it stops only when nothing else is left.
    /// </summary>
    public bool Autonomous { get; set; }
    /// <summary>The developer asked for a designed report: a readable HTML page of the result once the run is done.</summary>
    public bool Report { get; set; }
    /// <summary>The designed report, once written.</summary>
    public string ReportFile { get; set; } = "";
    /// <summary>The run already had its fresh second attempt after getting stuck.</summary>
    public bool Retried { get; set; }
    /// <summary>How often the department manager resumed the run after it stopped (at most MaxManagerResumes).</summary>
    public int ManagerResumes { get; set; }
    /// <summary>Which version of the product this is: 1 the first, and one more for each round of requested changes.</summary>
    public int Revision { get; set; } = 1;
    /// <summary>The developer accepted the run's work as it is.</summary>
    public bool Accepted { get; set; }
    /// <summary>Not autonomous: what the run understood the requested changes to mean, waiting for the developer's OK.</summary>
    public string PendingChange { get; set; } = "";
    /// <summary>Runs started for parts of a change that belong in another repo, by id.</summary>
    public List<string> LinkedRuns { get; set; } = [];
    /// <summary>The secret-scan findings the run stopped on, and the ones the developer said are not secrets (Continue).</summary>
    public string SecretsFound { get; set; } = "";
    public string SecretsAccepted { get; set; } = "";
    /// <summary>The manager's case for a human: what is blocked, what it tried, what it needs. Empty when none.</summary>
    public string ManagerCase { get; set; } = "";
    /// <summary>Changes to the factory itself the manager proposes, for a human to approve.</summary>
    public List<string> FactoryFixes { get; set; } = [];
    /// <summary>Files the run changed outside its folder, as it reported them: path - what - backup.</summary>
    public List<string> OutsideChanges { get; set; } = [];
    /// <summary>Decisions the run took on its own (the plan's questions, answered with its suggestions).</summary>
    public string Decisions { get; set; } = "";
    /// <summary>The plan found nothing to change: the run hands back an answer (a curl, an explanation), no PR.</summary>
    public bool NoChange { get; set; }
    /// <summary>What made the first attempt stuck, for the fresh attempt's plan.</summary>
    public string RetryContext { get; set; } = "";
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
    /// <summary>Saved with the run: it is being driven right now (also while Learn or the manager runs) - for anything
    /// outside this process that reads factory.json, and for a restart after Dev break closed mid-run.</summary>
    public bool Busy { get; set; }
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

    // an autonomous run: it fixes what it needs outside its folder itself, backed up and reported; only what no tool
    // can do - a login, a credential, an access grant - stops it
    // edits the developer's own tooling can see: hooks and PR gates count Edit and Write calls - a run that changed
    // every file through python in Bash had its PR blocked ("0 Edit/Write calls")
    private const string EditRule = """

        Change files in the repo with the Edit and Write tools, never with scripts in the shell (python, sed, heredocs,
        Set-Content): the developer's hooks and PR checks follow the Edit and Write calls. Shell commands are for
        building, testing, running and reading.
        """;

    private const string AutonomousRule = """

        You work with no human in the loop: solve problems yourself. When the work needs a change outside this folder
        (local machine settings, a shared build home, a local service's config), you may make it: first copy each file
        to <file>.bak-devbreak, change it, and add one line per file to your reply: OUTSIDE: <path> - <what changed> - <backup>.
        Never touch secrets, credentials or logins. Only when the blocker is something no tool can do for you - a login
        in a browser, a credential, an access grant - end your reply with one line NEEDS_YOU: <exactly what the developer must do>.
        """;

    /// <summary>Claude stopped to ask the developer (NEEDS_YOU, or the plan's questions): the run waits with the question.</summary>
    private sealed class NeedsYouException(string message) : Exception(message);

    // before the checks pass, a run may not publish anything
    private static readonly string[] NoPublish =
        ["--disallowedTools", "Bash(git push:*)", "Bash(gh pr create:*)", "PowerShell(git push:*)", "PowerShell(gh pr create:*)"];

    // the turn that pushes and opens the PR may not change what Dev break checked was committed
    private static readonly string[] Frozen =
    [
        "--disallowedTools",
        .. new[] { "git commit", "git add", "git reset", "git rebase", "git cherry-pick", "git merge", "git stash", "git checkout", "git switch", "git restore", "git rm", "git mv" }
            .SelectMany(c => new[] { $"Bash({c}:*)", $"PowerShell({c}:*)" }),
    ];

    private static List<FactoryRun>? _runs;

    // each working run's open claude process, by run id: its turns share it instead of starting claude each time
    private static readonly Dictionary<string, ClaudeSessions.LiveSession> Live = [];

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
            foreach (FactoryRun run in _runs.Where(r => r.Busy || r.Stage is not (FactoryStage.Done or FactoryStage.NeedsYou or FactoryStage.Stopped)))
            {
                run.Note = $"Dev break closed while this run was at {run.Stage}.";
                run.Stage = FactoryStage.Stopped;
                run.Busy = false;
            }
            return _runs;
        }
    }

    public static FactoryRun Create(FactorySource source, string task, string folder, string baseBranch, string verify, bool autonomous = false,
        bool report = false, FactoryDepartment? department = FactoryDepartment.Development)
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
            Verify = verify.Trim(), SessionId = Guid.NewGuid().ToString(), Created = DateTime.Now, Autonomous = autonomous,
            // troubleshooting and research end in a report: it is what they hand over
            // null: the gate decides when the run starts
            Report = report || department is not (null or FactoryDepartment.Development),
            Department = department ?? FactoryDepartment.Development, AutoDepartment = department == null,
        };
        Runs.Insert(0, run);
        Save();
        return run;
    }

    /// <summary>
    /// A troubleshooting run's diagnosis handed to Development: a new run in the same repo and base whose task is the
    /// diagnosis - the root cause and the fix it named. Its proof of the fix is the problem gone where it was reproduced.
    /// </summary>
    public static FactoryRun HandOff(FactoryRun diagnosed)
    {
        string task = $"""
            Fix the problem diagnosed by the troubleshooting run "{diagnosed.Title}" ({diagnosed.Id}).
            The case as reported: {diagnosed.Task}

            THE DIAGNOSIS (checked against the running system):
            {diagnosed.Result}

            Make the fix it names in this repo; prove it where the problem was reproduced - the problem is gone there,
            and the control still works.
            """;
        return Create(FactorySource.Typed, task, diagnosed.Repo.Length > 0 ? diagnosed.Repo : diagnosed.Folder, diagnosed.BaseBranch, "",
            diagnosed.Autonomous, diagnosed.Report, FactoryDepartment.Development);
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
        if (run.AutoDepartment && !await RouteAsync(run, cancel)) return false;
        if (!await LocateAsync(run, cancel)) return false;
        if (!run.FolderWide && !await WorktreeAsync(run, cancel)) return false;
        // from here on there is something to learn from
        if (!await PlanAsync(run, cancel)) return true;
        if (run.NoChange) return await AnswerAsync(run, cancel);
        if (!await BuildAsync(run, cancel)) return true;
        if (await CheckAsync(run, cancel)) await PullRequestAsync(run, cancel);
        else await FreshAttemptAsync(run, cancel);
        return true;
    });

    // autonomous and stuck on its checks (not on something only the developer can do): one fresh attempt - a new
    // session plans again from what went wrong, builds, checks and opens the PR; stuck again, the developer is told
    private static async Task FreshAttemptAsync(FactoryRun run, CancellationToken cancel)
    {
        if (!run.Autonomous || run.Retried || run.Stage != FactoryStage.NeedsYou || run.Note.StartsWith("Claude needs you", StringComparison.Ordinal)) return;
        run.Retried = true;
        string stuck = run.Note;
        run.RetryContext = $"""
            It stopped with: {stuck}
            The last verify: {Tail(run.VerifyResult, 2500)}
            The last review findings: {Tail(run.Review, 2500)}
            The last app check: {Tail(run.Proof, 1500)}
            Its changes are still in this folder - keep what is right, redo what is not.
            """;
        Log(run, $"Stuck: {stuck.Split('\n')[0]} - a fresh attempt: a new session plans again from what went wrong.");
        run.SessionId = Guid.NewGuid().ToString();
        if (!await PlanAsync(run, cancel)) return;
        if (!await BuildAsync(run, cancel)) return;
        if (await CheckAsync(run, cancel)) await PullRequestAsync(run, cancel);
    }

    // the plan found nothing to change: the answer (a curl, an explanation) is checked against the running app and
    // handed back in the Result - no build, no verify, no PR
    private static async Task<bool> AnswerAsync(FactoryRun run, CancellationToken cancel)
    {
        string dir = ProofDir(run);
        Directory.CreateDirectory(dir);
        string again = "";
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            Enter(run, FactoryStage.Prove, "No change needed: Claude checks the answer against the running app.");
            (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
                {again}{(run.Department == FactoryDepartment.Management ? ManagementCheck : run.Department == FactoryDepartment.Troubleshooting ? TroubleshootingCheck : "The task needs no change to the repo - it asks for an answer. Check the answer works against the running app (start what it needs with the tools on this machine), exactly as the developer would use it.")} {NamedSources}
                {ProofReuse(run)}
                Save the evidence (the call and its response, log lines) into {dir}, and write {Path.Combine(dir, ManifestName)}
                as a JSON array of {"{"}"kind", "file", "proves", "result", "detail"{"}"}. Change no file in the repo.
                Give the final, checked answer - ready to use - after a line RESULT:. Then end with exactly one line:
                PROOF: PASS / PROOF: FAIL <what is wrong> / PROOF: SKIPPED <why it cannot be checked here>
                """, $"Answer (attempt {attempt})", cancel);
            if (error != null) return Wait(run, "The answer turn failed: " + error);
            run.Proof = answer;
            run.Evidence = ReadManifest(dir);
            if (Block(answer, "RESULT:", "PROOF:") is { Length: > 0 } result) run.Result = result;
            string verdict = Line(answer, "PROOF:") ?? "";
            Log(run, $"Answer: {(verdict.Length > 0 ? verdict : "no verdict")}\n{answer}", ok: !verdict.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase));
            // done only on an explicit verdict: no PROOF line is not a pass - the next attempt asks for one
            bool fail = verdict.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase);
            if (verdict.Length > 0 && !fail && run.Result.Length > 0)
            {
                End(run, FactoryStage.Done, "Answered - no change was needed, so no PR." +
                    (verdict.StartsWith("SKIPPED", StringComparison.OrdinalIgnoreCase) ? " Not checked: " + verdict[7..].Trim() : ""));
                return true;
            }
            again = verdict.Length == 0
                ? "Your last reply had no PROOF: line, so the answer counts as not checked. Check it now and end with the PROOF: line.\n"
                : "";
        }
        return Wait(run, "Claude could not give a checked answer: " + (Line(run.Proof, "PROOF:") ?? "no RESULT"));
    }

    /// <summary>
    /// Picks up a run that stopped or waits for the developer where it left off: the steps already done (repo, worktree,
    /// plan) are not redone; Claude finishes what is missing, then verify, prove, review and the draft PR.
    /// </summary>
    public static Task ContinueAsync(FactoryRun run, bool byDeveloper = false) => DriveAsync(run, learnAfter: false, async cancel =>
    {
        if (run.CleanedUp) return Wait(run, "This run was cleaned up - start a new run for the task.");
        // Continue on a secret-scan stop: the developer says those findings are not secrets (only those). Only the
        // developer's own Continue counts - the manager resuming an autonomous run never accepts a finding.
        if (byDeveloper && run.Note.StartsWith("The commit looks like it holds a secret", StringComparison.Ordinal)) run.SecretsAccepted = run.SecretsFound;
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
            if (run.NoChange) return await AnswerAsync(run, cancel);
            if (!await BuildAsync(run, cancel)) return true;
        }
        else if (run.NoChange)
        {
            return await AnswerAsync(run, cancel);
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
            // what stopped it, in full: a continue that sees only the one-line note judged the work done and changed
            // nothing, and the reviewer found the same open finding again (seen: two missing tests, twice)
            string open = stoppedAt switch
            {
                FactoryStage.Review when run.Review.Length > 0 => $"\nTHE REVIEWER'S OPEN FINDINGS - fix each one, or say why it is wrong:\n{Tail(run.Review, 6000)}",
                FactoryStage.Verify when run.VerifyResult.Length > 0 => $"\nTHE FAILING VERIFY OUTPUT:\n{Tail(run.VerifyResult, 4000)}",
                FactoryStage.Prove when run.Proof.Length > 0 => $"\nTHE LAST APP CHECK:\n{Tail(run.Proof, 4000)}",
                _ => run.Review.Length > 0 && why.Contains("review", StringComparison.OrdinalIgnoreCase)
                    ? $"\nTHE REVIEWER'S OPEN FINDINGS - fix each one, or say why it is wrong:\n{Tail(run.Review, 6000)}" : "",
            };
            (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
                The run stopped at its {stoppedAt} step: {why}{open}
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

    /// <summary>Something a deleted run leaves behind on purpose: where it goes, its name, one line, and the note itself.</summary>
    public sealed record KeepSuggestion(string Where, string Name, string Description, string Content); // repo | global | insights

    /// <summary>
    /// Before a run is deleted: what is worth keeping from it. The run's own lessons that are still kept come first (as
    /// insights, so the Insights tab keeps them once the record is gone); then a Claude without tools reads the run
    /// against the repo's memory, the global CLAUDE.md and Insights and suggests what else to keep, and where.
    /// </summary>
    public static async Task<List<KeepSuggestion>> DiagnoseAsync(FactoryRun run)
    {
        var keep = run.Lessons.Where(l => l.Status is "saved" or "applied" or "pending")
            .Select(l => new KeepSuggestion("insights", l.Name, l.Description, l.Content)).ToList();
        string? memoryDir = run.Repo.Length > 0 && Directory.Exists(run.Repo) ? await CliSetup.MemoryDirAsync(run.Repo) : null;
        string repoIndex = memoryDir != null && File.Exists(Path.Combine(memoryDir, "MEMORY.md")) ? File.ReadAllText(Path.Combine(memoryDir, "MEMORY.md")) : "";
        string global = File.Exists(GlobalClaudeMd) ? File.ReadAllText(GlobalClaudeMd) : "";
        string insights = string.Join("\n", Insights.Select(i => $"- {i.Name}: {i.Description}"));
        string log = string.Join("\n\n", run.Log.Select(s => $"[{s.Stage}{(s.Ok ? "" : ", FAILED")}] {Tail(s.Text, 1200)}"));
        string prompt = $"""
            A coding agent's run is about to be deleted for good: its record, trace, evidence and report. Find what is
            worth keeping from it before that, and where. Keep only what a later run or the developer would really use
            and what is not kept already; most runs leave 0-3 things.

            THE TASK: {run.Task}
            HOW IT ENDED: {run.Stage} - {run.Note}
            THE RESULT:
            {Tail(run.Result, 6000)}
            THE LOG:
            {Tail(log, 14000)}
            ITS LESSONS (kept as insights already - do not repeat them):
            {(run.Lessons.Count > 0 ? string.Join("\n", run.Lessons.Select(l => $"- {l.Kind} {l.Name}: {l.Description}")) : "(none)")}

            THE REPO'S MEMORY ALREADY SAYS (do not repeat it):
            {(repoIndex.Length > 0 ? repoIndex : "(nothing)")}
            THE GLOBAL CLAUDE.md ALREADY SAYS (do not repeat it):
            {(global.Length > 0 ? Tail(global, 6000) : "(nothing)")}
            INSIGHTS ALREADY KEPT (do not repeat them):
            {(insights.Length > 0 ? insights : "(nothing)")}

            Where each can go:
            - repo: a fact about this repo for its Claude memory (how to run or check it, where logic lives, a gotcha,
              what this run found out). Under 120 words.
            - global: one short imperative line true across ALL repos on this machine (a tool, a login, a workflow) -
              it goes into the global CLAUDE.md that every session loads, so only when it really is general.
            - insights: what this mission found or decided, for the developer to read later in Dev break.
            Reply in exactly this format, one block each:
            KEEP: <repo|global|insights> | <short-kebab-case-name> | <one-line description>
            <content>
            END
            Or reply with the single word NONE.
            """;
        string answer = await ClaudeOnceAsync(run, Path.GetTempPath(), prompt,
            ["--tools", "", "--strict-mcp-config", "--setting-sources", ""], "Diagnose before delete", CancellationToken.None);
        foreach (Match block in Regex.Matches(answer.Replace("\r", ""), @"^KEEP:\s*(repo|global|insights)\s*\|\s*([^|\n]+?)\s*\|\s*([^\n]*)\n(.*?)\nEND\s*$",
                     RegexOptions.Multiline | RegexOptions.Singleline))
        {
            keep.Add(new KeepSuggestion(block.Groups[1].Value, Slug(block.Groups[2].Value), block.Groups[3].Value.Trim(), block.Groups[4].Value.Trim()));
        }
        return keep;
    }

    /// <summary>Commits on the run's local branch beyond its base that are not on the remote: what deleting it loses.</summary>
    public static async Task<int> UnpushedCommitsAsync(FactoryRun run)
    {
        if (run.Repo.Length == 0 || run.Branch.Length == 0 || run.BaseCommit.Length == 0 || !Directory.Exists(run.Repo)) return 0;
        (int exists, _) = await GitAsync(run.Repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + run.Branch);
        if (exists != 0) return 0;
        (int code, string count) = await GitAsync(run.Repo, "rev-list", "--count", run.BaseCommit + ".." + run.Branch, "--not", "--remotes");
        return code == 0 && int.TryParse(count.Trim(), out int n) ? n : 0;
    }

    // the developer's global Claude Code memory: every session on this machine loads it
    private static string GlobalClaudeMd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");

    /// <summary>
    /// Deletes a run for good, after saving what the developer kept from it: everything a clean up deletes, its
    /// designed report, its local branch (even unpushed - the developer was warned) and its record. The remote branch
    /// and PR only when asked: a one-off Claude with the developer's setup closes the PR and deletes the branch there.
    /// Never the developer's checkout or its own files. Returns what happened.
    /// </summary>
    public static async Task<string> DeleteAsync(FactoryRun run, IReadOnlyList<KeepSuggestion> keep, bool remote)
    {
        if (run.Working) return "the run is working";
        var done = new List<string>();
        // nothing is kept or deleted while the checkout still holds the run's uncommitted work
        CleanupInfo before = await InspectAsync(run);
        if (run.InCheckout && before.Uncommitted > 0 && Directory.Exists(run.Repo) && await HeadAsync(run.Repo) == run.Branch)
        {
            return $"Your checkout is on {run.Branch} with {before.Uncommitted} uncommitted change(s) - commit or stash them first.";
        }
        // 1. what the developer kept
        string? memoryDir = run.Repo.Length > 0 && Directory.Exists(run.Repo) ? await CliSetup.MemoryDirAsync(run.Repo) : null;
        foreach (KeepSuggestion k in keep)
        {
            switch (k.Where)
            {
                case "repo" when memoryDir != null:
                    SaveMemory(memoryDir, new FactoryLesson
                    {
                        Name = k.Name, Description = k.Description,
                        Content = k.Content + $"\n\nKept by Dev break when deleting the factory run \"{run.Title}\" ({DateTime.Now:yyyy-MM-dd}).",
                    });
                    done.Add("repo memory: " + k.Name);
                    break;
                case "global":
                    Directory.CreateDirectory(Path.GetDirectoryName(GlobalClaudeMd)!);
                    string existing = File.Exists(GlobalClaudeMd) ? File.ReadAllText(GlobalClaudeMd) : "";
                    File.AppendAllText(GlobalClaudeMd, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + $"- {k.Content.Replace("\n", " ").Trim()}\n");
                    done.Add("global CLAUDE.md: " + k.Name);
                    break;
                case "insights":
                    Insights.Add(new FactoryInsight
                    {
                        Repo = run.Repo, Name = k.Name, Description = k.Description, Content = k.Content,
                        Kind = run.Lessons.FirstOrDefault(l => l.Name == k.Name)?.Kind ?? "insight",
                        From = run.Title.Length > 0 ? run.Title : run.Task, Created = run.Created,
                    });
                    done.Add("insight: " + k.Name);
                    break;
            }
        }
        SaveInsights();
        // 2. the remote branch and PR, when asked
        if (remote && run.Repo.Length > 0 && run.Branch.Length > 0 && Directory.Exists(run.Repo))
        {
            string pr = run.PrUrl.Length > 0
                ? $"First decline (close without merging) the pull request {run.PrUrl} with the git host's tools on this machine - its official MCP server when one is connected, else its CLI or API. "
                : "";
            string answer = await ClaudeOnceAsync(run, run.Repo, $"""
                {pr}Then delete the remote branch {run.Branch} on origin (git push origin --delete {run.Branch}) if it exists.
                Do nothing else. Reply with one line per action and whether it worked.
                """, ["--permission-mode", "bypassPermissions"], "Delete remote branch and PR", CancellationToken.None);
            done.Add("remote: " + answer.Replace("\r", "").Replace("\n", " ").Trim());
        }
        // 3. everything on this machine
        CleanupInfo info = await InspectAsync(run);
        if (!run.CleanedUp || HasOwnWorktree(run))
        {
            string cleaned = await CleanUpAsync(run);
            if (!cleaned.StartsWith("Cleaned up", StringComparison.Ordinal)) return cleaned;
        }
        if (run.Repo.Length > 0 && run.Branch.Length > 0 && Directory.Exists(run.Repo) && await HeadAsync(run.Repo) != run.Branch)
        {
            (int deleted, _) = await GitAsync(run.Repo, "branch", "-D", run.Branch);
            if (deleted == 0) done.Add("local branch " + run.Branch);
        }
        string traces = Path.Combine(AppContext.BaseDirectory, "traces");
        if (Directory.Exists(traces))
        {
            foreach (string f in Directory.GetFiles(traces, run.Id + "-*")) File.Delete(f);
            foreach (string d in Directory.GetDirectories(traces, run.Id + "-*")) await Task.Run(() => DeleteTree(d));
        }
        // 4. the record
        Runs.Remove(run);
        Save();
        return $"Deleted the run \"{run.Title}\" - {Size(info.Bytes)} freed." + (done.Count > 0 ? " Kept and done: " + string.Join("; ", done) + "." : "");
    }

    /// <summary>Insights kept from runs that were deleted: the Insights tab shows them with the live runs' lessons.</summary>
    public static List<FactoryInsight> Insights => _insights ??= File.Exists(InsightsPath)
        ? JsonSerializer.Deserialize<List<FactoryInsight>>(File.ReadAllText(InsightsPath)) ?? [] : [];

    private static List<FactoryInsight>? _insights;
    private static readonly string InsightsPath = Path.Combine(AppContext.BaseDirectory, "insights.json");

    private static void SaveInsights() =>
        File.WriteAllText(InsightsPath, JsonSerializer.Serialize(Insights, new JsonSerializerOptions { WriteIndented = true }));

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
    public static Task FeedbackAsync(FactoryRun run, string text, string kind = "") => DriveAsync(run, learnAfter: true, async cancel =>
    {
        // not autonomous, and the run is waiting for an OK on what it understood: an OK builds that change
        if (run.PendingChange.Length > 0 && IsOk(text)) return await ReviseAsync(run, cancel, confirmed: true);
        run.Accepted = false;
        run.PendingChange = "";
        // the next revision gets its own report, written when it is done
        run.ReportFile = "";
        run.Feedback.Add(new FactoryFeedback { At = DateTime.Now, Text = text.Trim(), Kind = kind, Revision = run.Revision + 1 });
        if (!Directory.Exists(run.Worktree)) return Wait(run, "The run's worktree is gone, so the feedback has nowhere to go.");
        if (!run.FolderWide && !await OnRunBranchAsync(run)) return true;
        // an answered run (troubleshooting, research, management, or an answer with no change): the answer is revised
        // and checked again as the next revision - nothing in the repo changes
        if (run.NoChange && run.Result.Length > 0)
        {
            FactoryFeedback ask = run.Feedback[^1];
            string asked = ask.Kind.Length > 0 ? $" ({ask.Kind})" : "";
            Enter(run, FactoryStage.Plan, $"Revision {ask.Revision}{asked}: " + ask.Text);
            (string redo, string? redoError) = await TurnAsync(run, isNew: false, $"""
                The developer reviewed your answer (revision {run.Revision}) and asks for changes{asked}:
                {ask.Text.Trim()}

                First, in one or two lines, say what the answer must now cover: a line UNDERSTOOD: <...>. If it overturns a
                decision you took earlier, say which, and follow the developer. Then work it out with the tools on this
                machine - change no file in the repo.
                """, $"Revision {ask.Revision}", cancel);
            if (redoError != null) return Wait(run, $"The revision {ask.Revision} turn failed: " + redoError);
            ask.Understood = Line(redo, "UNDERSTOOD:") ?? "";
            if (ask.Understood.Length > 0) Log(run, $"Revision {ask.Revision} - understood: {ask.Understood}");
            run.Revision = ask.Revision;
            Log(run, $"Revision {ask.Revision}:\n" + redo);
            return await AnswerAsync(run, cancel);
        }
        // troubleshooting, research and management never build - one with no answer yet asks instead
        if (run.Department != FactoryDepartment.Development || run.FolderWide)
        {
            return Wait(run, "This run has no answer yet to revise - press Continue to let it finish its answer first.");
        }
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
            run.NoChange = (Line(plan, "CHANGE:") ?? "").StartsWith("none", StringComparison.OrdinalIgnoreCase);
            if (run.NoChange) return await AnswerAsync(run, cancel);
            if (!await BuildAsync(run, cancel)) return true;
            if (await CheckAsync(run, cancel)) await PullRequestAsync(run, cancel);
            return true;
        }
        return await ReviseAsync(run, cancel, confirmed: false);
    });

    private static bool IsOk(string text) =>
        text.Trim().TrimEnd('.', '!').ToLowerInvariant() is "ok" or "yes" or "go" or "confirm" or "confirmed" or "build it" or "כן" or "אוקיי";

    // a round of requested changes: the run says what "done" now means, flags a decision it overturns and parts that
    // belong in another repo (those start linked runs there), then builds, checks and updates the draft PR as the
    // next revision. Not autonomous, it first stops with what it understood and builds on the developer's OK.
    private static async Task<bool> ReviseAsync(FactoryRun run, CancellationToken cancel, bool confirmed)
    {
        FactoryFeedback ask = run.Feedback[^1];
        int revision = ask.Revision > 0 ? ask.Revision : run.Revision + 1;
        string kind = ask.Kind.Length > 0 ? $" ({ask.Kind})" : "";
        bool firstUnderstand = !run.Autonomous && !confirmed;
        Enter(run, FactoryStage.Build, $"Revision {revision}{kind}: " + ask.Text);
        string earlier = run.Decisions.Length > 0 ? $"\nThe decisions you took on your own earlier:\n{run.Decisions}\n" : "";
        (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
            The developer reviewed revision {run.Revision} of your work and asks for changes{kind}:
            {ask.Text.Trim()}
            {earlier}
            First, in one or two lines, say what "done" now means for this change: a line UNDERSTOOD: <...>. If it
            overturns a decision you took earlier, say which, and follow the developer. If part of it needs a change in
            another repository than this one, keep to your part here and add one line per other repository:
            OTHER_REPO: <its folder> - <what must change there, as a task>. List them in the order they must be
            done - a library before what uses it.
            {(firstUnderstand
                ? "Then stop: do not change any file yet - the developer confirms your understanding first."
                : "Then apply it in this folder. Do not commit or push. Reply with what you changed.")}
            """, $"Revision {revision}", cancel);
        if (error != null) return Wait(run, $"The revision {revision} turn failed: " + error);
        ask.Understood = Line(answer, "UNDERSTOOD:") ?? ask.Understood;
        if (ask.Understood.Length > 0) Log(run, $"Revision {revision} - understood: {ask.Understood}");
        if (firstUnderstand)
        {
            run.PendingChange = ask.Understood.Length > 0 ? ask.Understood : answer;
            // the other repos it would start runs in are part of what the developer approves
            string others = string.Join("\n", answer.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('*', '-', ' '))
                .Where(l => l.StartsWith("OTHER_REPO:", StringComparison.OrdinalIgnoreCase)).Select(l => "- " + l[11..].Trim()));
            if (others.Length > 0) run.PendingChange += "\nIt would also start linked runs in:\n" + others;
            return Wait(run, $"Check what the run understood for revision {revision}: {run.PendingChange}\n" +
                "Send \"ok\" to the run to build it, or send what is wrong with it.");
        }
        run.PendingChange = "";
        run.Revision = revision;
        Log(run, $"Revision {revision}:\n" + answer);
        // the other repos' parts first, one after another in the order given (a library before what uses it), so this
        // run's checks see the whole change and no two runs restart the same services at once
        List<FactoryRun> linked = StartLinkedRuns(run, answer, revision);
        foreach (FactoryRun part in linked)
        {
            Log(run, $"Waits for the linked run in {(part.Repo.Length > 0 ? part.Repo : part.Folder)} ({part.Id}).");
            await RunAsync(part);
            cancel.ThrowIfCancellationRequested();
            Log(run, $"Linked run {part.Id}: {part.Stage} - {part.Note.Split('\n')[0]}", ok: part.Stage == FactoryStage.Done);
        }
        if (await CheckAsync(run, cancel)) await PullRequestAsync(run, cancel);
        return true;
    }

    /// <summary>Another repo has to change too: a linked Development run there, its task the part this one cannot do.</summary>
    public static event Action<FactoryRun>? RunCreated;

    private static List<FactoryRun> StartLinkedRuns(FactoryRun run, string answer, int revision)
    {
        var started = new List<FactoryRun>();
        foreach (string line in answer.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('*', '-', ' '))
                     .Where(l => l.StartsWith("OTHER_REPO:", StringComparison.OrdinalIgnoreCase)))
        {
            string rest = line[11..].Trim();
            int dash = rest.IndexOf(" - ", StringComparison.Ordinal);
            string folder = (dash > 0 ? rest[..dash] : rest).Trim().Trim('`', '"');
            string what = dash > 0 ? rest[(dash + 3)..].Trim() : rest;
            // only a git repo under the folder the developer gave the run - never a path the model made up
            string full;
            try
            {
                full = Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(run.Folder, folder)).TrimEnd('\\', '/');
            }
            catch (Exception)
            {
                full = "";
            }
            string? known = ClaudeProjects.ReposUnder(run.Folder, 3).FirstOrDefault(r => Path.GetFullPath(r).TrimEnd('\\', '/').Equals(full, StringComparison.OrdinalIgnoreCase));
            if (known == null || known.Equals(run.Repo, StringComparison.OrdinalIgnoreCase))
            {
                Log(run, $"Revision {revision} names another repo ({rest}), but it is not a git repo under {run.Folder} - nothing was started; do it there yourself.", ok: false);
                continue;
            }
            folder = known;
            FactoryRun linked = Create(FactorySource.Typed, $"""
                {what}

                This is the part of revision {revision} of the run "{run.Title}" ({run.Id}) that belongs in this repo.
                {(run.PrUrl.Length > 0 ? "That run's draft PR: " + run.PrUrl + " - say in this PR's description that the two go together." : "")}
                """, folder, "", "", run.Autonomous, run.Report, FactoryDepartment.Development);
            run.LinkedRuns.Add(linked.Id);
            Log(run, $"Revision {revision}: started a linked run in {folder} for: {what}");
            RunCreated?.Invoke(linked);
            started.Add(linked);
        }
        return started;
    }

    /// <summary>The developer accepts the run's work as it is; after rounds of changes, it learns what they meant.</summary>
    public static Task AcceptAsync(FactoryRun run) => DriveAsync(run, learnAfter: run.Revision > 1, cancel =>
    {
        run.Accepted = true;
        Log(run, run.Revision > 1 ? $"Accepted at revision {run.Revision}." : "Accepted.");
        End(run, FactoryStage.Done, run.Note);
        return Task.FromResult(false);
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
        run.Busy = true;
        Save();
        CancellationToken cancel = run.Running.Token;
        bool learn = learnAfter;
        bool resume = false;
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
            // stopped for the developer inside a step (NEEDS_YOU): there is something to learn from, as after a plan
            learn |= run.Trace.Any(s => s.Kind == "turn");
        }
        catch (Exception ex)
        {
            Wait(run, "Something went wrong: " + ex.Message);
        }
        finally
        {
            CloseLive(run);
            if (cancel.IsCancellationRequested) End(run, FactoryStage.Stopped, "Stopped.");
            else
            {
                if (run.Report && run.Stage == FactoryStage.Done && run.ReportFile.Length == 0) await ReportAsync(run);
                // an autonomous run that stopped goes to the department manager first: it resumes it or calls a human
                if (run.Autonomous && run.Stage == FactoryStage.NeedsYou) resume = await ManageAsync(run);
                // a resumed run learns when it ends
                if (learn && !resume) await LearnAsync(run);
            }
            try
            {
                await LeaveCheckoutAsync(run);
            }
            catch (Exception ex)
            {
                Log(run, "Could not switch your checkout back: " + ex.Message, ok: false);
            }
            FreeLock(run);
            run.Running.Dispose();
            run.Running = null;
            run.Busy = false;
            Save();
            Notify(run);
        }
        // the manager removed the cause: the run goes on from where it stopped
        if (resume) await ContinueAsync(run);
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
            if (repo.Length == 0)
            {
                // troubleshooting, research or management about no single repo: it works in the folder itself - no
                // branch, no checkout; every repo under it can be read
                run.FolderWide = true;
                run.Worktree = run.Folder;
                Log(run, $"No single repo: it works across {run.Folder} - no branch, nothing is changed.");
                return true;
            }
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
        // what earlier runs in this folder were about and where they went: tasks that read alike go to the same repo
        // without searching every repository again (each run used to grep all of them)
        string earlier = string.Join("\n", Runs.Where(r => r != run && r.Repo.Length > 0 && r.Folder.Equals(run.Folder, StringComparison.OrdinalIgnoreCase))
            .Take(20).Select(r => $"- \"{Tail(r.Task.Replace("\r", "").Replace('\n', ' '), 140)}\" -> {r.Repo}"));
        string prompt = $"""
            A developer gave a coding task and a folder with several git repositories in it. Find the one repository the
            task has to be implemented in. Do not change anything - read only.
            {task}

            THE REPOSITORIES:
            {list}
            {(earlier.Length > 0 ? $"""

            EARLIER TASKS IN THIS FOLDER AND THE REPO EACH WENT TO (newest first):
            {earlier}
            When the task clearly belongs with one of these, answer from it - no need to search the repositories.
            """ : "")}

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
        // only Development has to change one repo; the others may work across the folder ("" - folder-wide)
        if (value.StartsWith("NONE", StringComparison.OrdinalIgnoreCase) && run.Department != FactoryDepartment.Development) return "";
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
        if (await CheckoutProblemAsync(run, "start") is { } problem)
        {
            // autonomous: another run in the checkout is no reason to wait - this one goes beside it
            if (!run.Autonomous || !problem.StartsWith("Another run", StringComparison.Ordinal)) return Wait(run, problem);
            Log(run, problem.Split(" Wait for it")[0] + " This run goes on in a separate worktree.");
            run.UseWorktree = true;
            return await WorktreeAsync(run, cancel);
        }
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
        TakeLock(run);
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
        // a run of another Dev break on this machine (each keeps its own list): its lock file in the repo's .git
        if (LockHolder(run.Repo) is { } held && held.RunId != run.Id)
        {
            return $"Another run (\"{held.Title}\", in the Dev break at {held.App}) is working in your checkout {run.Repo}, so this one cannot {what} there. " +
                "Wait for it and press Continue - or press Use a separate worktree to run this one beside it.";
        }
        (int code, string status) = await GitAsync(run.Repo, "status", "--porcelain");
        return code == 0 ? null : $"git cannot read your checkout {run.Repo}: {status.Trim()}";
    }

    // --- the checkout lock: one run at a time in the developer's checkout, across every Dev break on the machine ---

    private sealed record CheckoutLock(int Pid, string App, string RunId, string Title);

    private static string LockPath(string repo) => Path.Combine(repo, ".git", "devbreak-run.lock");

    // who holds the checkout now - null when nobody does, or the holder's process is gone (a stale lock)
    private static CheckoutLock? LockHolder(string repo)
    {
        try
        {
            string file = LockPath(repo);
            if (!File.Exists(file)) return null;
            CheckoutLock? held = JsonSerializer.Deserialize<CheckoutLock>(File.ReadAllText(file));
            if (held == null) return null;
            try
            {
                using Process p = Process.GetProcessById(held.Pid);
                if (p.HasExited) return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            return held;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TakeLock(FactoryRun run)
    {
        if (!run.InCheckout || !Directory.Exists(Path.Combine(run.Repo, ".git"))) return;
        try
        {
            File.WriteAllText(LockPath(run.Repo), JsonSerializer.Serialize(new CheckoutLock(Environment.ProcessId, AppContext.BaseDirectory, run.Id, run.Title)));
        }
        catch (Exception)
        {
            // a lock we cannot write only means the other Dev break cannot see us
        }
    }

    private static void FreeLock(FactoryRun run)
    {
        if (run.Repo.Length == 0) return;
        if (LockHolder(run.Repo) is { } held && held.RunId == run.Id && held.Pid == Environment.ProcessId)
        {
            try
            {
                File.Delete(LockPath(run.Repo));
            }
            catch (Exception)
            {
                // gone already, or locked by a reader: the holder check treats a dead process as free anyway
            }
        }
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
        // files under .claude/ are Claude's own tooling state - a hook's log grows with every edit any session makes
        // (seen: .claude/session-changes.log stopped a run that never touched it). They still never get committed
        // (they stay in LocalChanges for the commit check); they just do not stop the run.
        List<string> touched = TouchedLocalChanges(run)
            .Where(p => !p.Replace('\\', '/').StartsWith(".claude/", StringComparison.OrdinalIgnoreCase)).ToList();
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
        if (!run.InCheckout) return true;
        if (await HeadAsync(run.Repo) == run.Branch)
        {
            if (LockHolder(run.Repo) is { } held && held.RunId != run.Id) return Wait(run, await CheckoutProblemAsync(run, "go on") ?? "Another run holds your checkout.");
            TakeLock(run);
            return true;
        }
        if (await CheckoutProblemAsync(run, "switch to its branch " + run.Branch) is { } problem) return Wait(run, problem);
        run.ReturnTo = await HeadAsync(run.Repo);
        (int exit, string output) = await GitSpanAsync(run, run.Repo, "switch", run.Branch);
        if (exit != 0) return Wait(run, $"Could not switch your checkout to {run.Branch}: {Tail(output.Trim(), 800)}");
        TakeLock(run);
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
        // an answer-only run left its branch without a commit: it goes
        if (exit == 0 && run.NoChange && run.Stage == FactoryStage.Done && (await GitAsync(run.Repo, "rev-list", "--count", run.BaseCommit + ".." + run.Branch)).Output.Trim() == "0")
        {
            await GitAsync(run.Repo, "branch", "-D", run.Branch);
        }
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
        string where = run.FolderWide
            ? $"You are working in the folder {run.Folder}: the item belongs to no single repo, so read across the repos and systems under it as needed. Change nothing."
            : $"You are working in {(run.InCheckout ? "the developer's own checkout of " + run.Repo : "a separate git worktree of " + run.Repo)}, on a new branch {run.Branch} (from {run.BaseBranch}).";
        if (run.Department != FactoryDepartment.Development) return await InvestigateAsync(run, where, task, cancel);
        string prompt = $"""
            {where}
            {task}

            First, understand the mission and plan - do not change any file yet. Use the skills, MCP servers and tools on
            this machine that fit the task (ones that run the app or its services, read logs or data, inspect or debug a
            running service) to see the current behaviour and where it lives, not only the code. Learn how this repo is
            built, run and tested (its README, build files and scripts), so the plan and the verify command fit it, and
            say how the change can be seen in the running app. A memory note marked "Checked on <date> - re-check" tells
            a state that can change (access, a connector, a running service): when the plan depends on it, re-check it
            with one quick call instead of trusting it. If you cannot read the task, reply with one line:
            CANNOT_READ: <why>. Otherwise reply with a short title for the work on the first line (TITLE: <title>), then
            the plan as short numbered steps, naming the files you will change and the tests you will add or run.
            Then choose the proof this feature needs - what a careful reviewer would want to see before believing it
            works - and end with one line: EVIDENCE: <comma-separated kinds from: {string.Join(", ", EvidenceKinds)}>.
            (A UI change: screenshots, often a video of the interaction. A logic change: red-green, coverage, mutation,
            crap. A service: api, logs, trace. Pick only what proves this change.)
            If the task leaves a decision that is the developer's to make - behaviour, naming, defaults, which of several
            options - do not guess: after the plan, add a line QUESTIONS: and then the numbered questions, each with the
            answer you suggest. {(run.Autonomous ? "No one will answer: the run goes on with your suggested answers, so make them the best choice and they are recorded as the decisions taken." : "The run waits for the answers before building.")} Leave QUESTIONS out when nothing is open.
            If the task needs no change to the repo - it asks for information, like a curl to try something, an
            explanation, where something is - add one line CHANGE: none, and plan how to check the answer against the
            running app instead.{(run.RetryContext.Length > 0 ? "\n\nA FIRST ATTEMPT GOT STUCK - plan so this one finishes:\n" + run.RetryContext : "")}
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
        run.NoChange = (Line(answer, "CHANGE:") ?? "").StartsWith("none", StringComparison.OrdinalIgnoreCase);
        // decisions that are the developer's: the run asks before it builds - or, autonomous, takes its suggestions
        string questions = Questions(answer);
        if (questions.Length > 0 && run.Autonomous)
        {
            run.Decisions = questions;
            Log(run, "Decisions taken on my own (the suggested answers):\n" + questions);
        }
        else if (questions.Length > 0)
        {
            throw new NeedsYouException("The plan has questions for you. Answer them in the box below and press Send to the run - " +
                "the ones you skip take the suggested answer (or press Continue to take all the suggestions):\n" + questions);
        }
        return true;
    }

    // the gate: which department handles a new item - read from the task itself, before anything else runs
    private static async Task<bool> RouteAsync(FactoryRun run, CancellationToken cancel)
    {
        Enter(run, FactoryStage.Locate, "The gate reads the task and chooses the department.");
        string task = run.Source switch
        {
            FactorySource.Jira => $"The task is the Jira issue {run.Task}; read it with the tools you have on this machine.",
            FactorySource.Spec => $"The task is the spec at {run.Task} (a file or a link); read it with the tools you have on this machine.",
            _ => "The task:\n" + run.Task,
        };
        string prompt = $"""
            A software factory has three departments. Decide which one handles this new item.
            - Development: the item asks to change the code - build a feature, fix a known bug, refactor, add an endpoint,
              change a page. It ends in a pull request.
            - Troubleshooting: something is reported broken or wrong for a customer or a site - an error, a page that
              loops or is blank, wrong data or prices, slowness - and the cause is not known yet. It is diagnosed for the
              call center (reproduce, compare with what works, root cause); no code is changed.
            - Research: a question about the system - how something works, where it lives, what it stores, give me a curl
              or an example, suggest a design. It is answered and checked; no code is changed.
            - Management: a vision, a new product or business idea, a strategy, a priority or a decision that matters company-wide
              (what to build next, whether to adopt something, how to grow, cost or risk at company level). It is thought
              through into a decision brief - options, value, effort, risk, a recommendation; no code is changed.
            When the item both reports a problem and asks to fix it, choose Troubleshooting if the cause is not known,
            Development if it is. Do not change anything.
            {task}

            Reply with exactly one line: DEPARTMENT: <Development|Troubleshooting|Research|Management> - <one-line reason>
            """;
        string[] tools = run.Source == FactorySource.Typed
            ? ["--tools", "", "--strict-mcp-config", "--setting-sources", ""]
            : ["--permission-mode", "bypassPermissions", "--disallowedTools", "Edit", "Write", "NotebookEdit", .. NoPublish[1..]];
        string answer = await ClaudeOnceAsync(run, run.Folder, prompt, tools, "Gate", cancel);
        string line = Line(answer, "DEPARTMENT:") ?? "";
        string name = line.Split(' ', '-', ':')[0].Trim();
        if (!Enum.TryParse(name, ignoreCase: true, out FactoryDepartment department))
        {
            return Wait(run, "The gate could not choose a department:\n" + answer);
        }
        int dash = line.IndexOf('-');
        run.Department = department;
        run.Routing = dash >= 0 ? line[(dash + 1)..].Trim() : "";
        run.AutoDepartment = false;
        // troubleshooting and research end in a report: it is what they hand over
        if (department != FactoryDepartment.Development) run.Report = true;
        Log(run, $"Gate: {department}" + (run.Routing.Length > 0 ? " - " + run.Routing : ""));
        return true;
    }

    // the plan of a troubleshooting or research run: an investigation, not a change - it always ends in the checked
    // answer of the no-change path (AnswerAsync) and its report
    private static async Task<bool> InvestigateAsync(FactoryRun run, string where, string task, CancellationToken cancel)
    {
        string how = run.Department == FactoryDepartment.Management ? ManagementBrief
            : run.Department == FactoryDepartment.Troubleshooting
            ? """
              This is a TROUBLESHOOTING case for the call center: a customer or a site has a problem. Diagnose it - do not
              change any file. Use the skills, MCP servers and tools on this machine (a browser, the running services,
              their logs, the data stores, the git host, the cloud tools):
              1. Reproduce it exactly as reported - the site, the URL, the page, the user's steps. Say what the customer sees.
              2. Find a control that works - another site, another page, another environment or server, an earlier state -
                 and compare the two: what differs (which server answers, config, data, code path, version).
              3. Find the root cause: the file:line, the data row or the setting, and how it leads to what the customer
                 sees. Keep apart what you proved and what you only suspect; check a suspicion rather than report it.
              4. Say what fixes it, and where - the change, or the setting or data to correct - and who would make it.
              """
            : """
              This is a RESEARCH question about the system. Answer it - do not change any file. Use the code, the running
              app and its data with the skills, MCP servers and tools on this machine, and check what you find rather than
              reason it out: each claim backed by a file:line, a call and its response, or a data row.
              """;
        string prompt = $"""
            {where}
            {task}

            {how}
            A memory note marked "Checked on <date> - re-check" tells a state that can change: when the answer depends on
            it, re-check it with one quick call instead of trusting it. If you cannot read the task, reply with one line:
            CANNOT_READ: <why>. Otherwise reply with a short title on the first line (TITLE: <title>), then what you found
            so far and how the next step will check it against the running system. If something is the developer's to
            decide, add a line QUESTIONS: and the numbered questions, each with the answer you suggest. End with one
            line: EVIDENCE: <comma-separated kinds from: {string.Join(", ", EvidenceKinds)}>.
            """;
        string step = run.Department switch { FactoryDepartment.Troubleshooting => "Diagnose", FactoryDepartment.Management => "Brief", _ => "Research" };
        (string answer, string? error) = await TurnAsync(run, isNew: true, prompt, step, cancel);
        if (error != null) return Wait(run, "The investigation turn failed: " + error);
        if (Line(answer, "CANNOT_READ:") is { } why) return Wait(run, "Claude could not read the task: " + why);
        if (Line(answer, "TITLE:") is { Length: > 0 } title && run.Source != FactorySource.Typed) run.Title = title;
        run.Plan = answer;
        run.EvidencePlan = (Line(answer, "EVIDENCE:") ?? "").Split(',', ';').Select(k => k.Trim().Trim('`', '*', '.').ToLowerInvariant())
            .Where(k => EvidenceKinds.Contains(k)).Distinct().ToList();
        Log(run, step + ":\n" + answer);
        run.NoChange = true;
        string questions = Questions(answer);
        if (questions.Length > 0 && run.Autonomous)
        {
            run.Decisions = questions;
            Log(run, "Decisions taken on my own (the suggested answers):\n" + questions);
        }
        else if (questions.Length > 0)
        {
            throw new NeedsYouException("The investigation has questions for you. Answer them in the box below and press Send to the run - " +
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
            and do not reinstall dependencies that are already installed. Do not start or restart the app or its
            services and do not call it to try the change: the Prove step right after runs the app once with the
            change and checks it live.
            Do not commit, push or open a pull request - that comes after the checks. Reply with a short summary of
            what you changed.
            """, "Build", cancel);
        if (error != null) return Wait(run, "The build turn failed: " + error);
        Log(run, "Build:\n" + answer);
        return true;
    }

    // verify, fixing up to MaxFixes times; review the code, fixing what the reviewer finds, up to MaxReviews rounds;
    // only then prove it in the running app - once, not after every review fix (proving was the costliest step:
    // 47 minutes over 8 attempts in one run, re-proven after each fix). A fix the app check needed gets reviewed too.
    private static async Task<bool> CheckAsync(FactoryRun run, CancellationToken cancel)
    {
        if (!await ReviewLoopAsync(run, cancel, beforeProof: true)) return false;
        int fixesBefore = run.Trace.Count(s => s.Title.StartsWith("Fix app check", StringComparison.Ordinal));
        if (!await ProveAsync(run, cancel)) return false;
        if (run.Trace.Count(s => s.Title.StartsWith("Fix app check", StringComparison.Ordinal)) == fixesBefore) return true;
        Log(run, "The app check needed a fix: the reviewer looks at the change again, with the evidence.");
        if (!await ReviewLoopAsync(run, cancel, beforeProof: false)) return false;
        return true;
    }

    private static async Task<bool> ReviewLoopAsync(FactoryRun run, CancellationToken cancel, bool beforeProof)
    {
        for (int review = 1; ; review++)
        {
            if (!await VerifyAsync(run, cancel)) return false;
            Enter(run, FactoryStage.Review, $"A reviewer that can only read checks the change (round {review}).");
            (bool pass, string findings) = await ReviewAsync(run, cancel, beforeProof);
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
                {NamedSources}
                {ProofReuse(run)}

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

    // a management item: a vision, an idea or a company-wide matter, thought through into a decision brief
    private const string ManagementBrief = """
        This is a MANAGEMENT item: a vision, an idea, a strategy or a matter that is important company-wide. Think it
        through as a trusted advisor to the company's management would - do not change any file:
        1. What is being asked, and why it matters to the company (customers, revenue, cost, risk, speed).
        2. Where the company stands on it today - from the code, the running systems, their data and documents on this
           machine, with the skills, MCP servers and tools here. Facts only, each with its source (file:line, a call, a
           row, a document); and what the market or the technology offers, from the web, with links.
        3. Two to four options, each with its value, the effort (who, how long, roughly), the risks, and what it needs.
        4. A recommendation and the first concrete steps - which department does what: Development (builds),
           Troubleshooting (diagnoses), Research (answers) - and the open questions only management can decide.
        Keep what you found apart from what you estimate, and say how sure each estimate is.
        """;

    // its check, and the shape of the brief
    private const string ManagementCheck = """
        This is a management item: check the brief's facts now - each one against its source with a fresh look (the code, a
        call to the running system, a data row, a page) - and drop or mark what does not hold. Change no file in the
        repo. After the line RESULT: give the decision brief in this order: the recommendation in one paragraph; why;
        the options compared (value, effort, risk, needs); the risks of doing nothing; the first steps by department;
        the open questions for management; what is estimated and not verified.
        """;

    // the top of a management report: what to decide, before the detail
    private const string ExecutivePart = """
        - FIRST an "Executive summary" in plain language, no jargon: the recommendation, the decision asked for, the
          value and the cost in a line each, and the top risk. Then the options as a comparison table, the first steps
          by department, and the open questions for management; the facts and sources after that.

        """;

    // the top of a troubleshooting report: for the support agent on the phone, before any technical detail
    private const string CallCenterPart = """
        - FIRST a "For the call center" part in plain language, no code and no jargon, each as a short heading and one
          or two sentences: what the customer sees; why it happens; what to tell the customer now; a workaround, if
          there is one; who fixes it and how urgent it looks. Then a clearly separated "Technical" part for the
          developer with everything below.

        """;

    // the check of a troubleshooting diagnosis, and the shape of its answer: the call center reads it first
    private const string TroubleshootingCheck = """
        This is a troubleshooting case: check the diagnosis against the running system now - reproduce the problem
        again as the customer meets it, and show the control working - with fresh evidence (screenshots, the HTTP
        calls and their status, console and server log lines, the data rows). Change no file in the repo. After the
        line RESULT: give the diagnosis in this order: what the customer sees; why, in two plain sentences; what to
        tell the customer now, and a workaround if there is one; the root cause (file:line, the data or the setting)
        and how it leads to what the customer sees; the fix - what, where, and who makes it; what is not verified.
        """;

    // proofs that last: the scripts a check writes are kept per repo and reused next time (one run rewrote its browser
    // script 8 times, 47 minutes, outside any known place); and the evidence stays lean (one run kept 186 files)
    private static string ProofReuse(FactoryRun run)
    {
        string dir = Path.Combine(KnowledgeDir(run.Home), "proof-scripts");
        string[] kept = Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().ToArray() : [];
        return $"""
            Proof scripts kept from earlier runs in this repo are in {dir}{(kept.Length > 0 ? ": " + string.Join(", ", kept) : " (none yet)")}.
            Reuse and adapt them before writing a new one; save a script you write there (parameterised - the site, the
            URL, the ids as arguments), never elsewhere outside the repo. Keep the evidence lean: the manifest and at most
            about 15 files that prove the change - no whole logs, no repeated screenshots.
            """;
    }

    // a check uses what the task offered to check with - a run once skipped a bucket and a client the task named
    private const string NamedSources = """
        When the task names sources to check with - a database, a bucket, a running client or site, a URL, an MCP
        server, a service - use each one in the check with fresh calls (not an earlier run's logs), and put one
        evidence entry per source; one that cannot be used gets its entry with result "skipped" and the reason.
        """;

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

    private static async Task<(bool Pass, string Findings)> ReviewAsync(FactoryRun run, CancellationToken cancel, bool beforeProof = false)
    {
        // the change against the base - the run's files only: new files are marked "intent to add" so the diff shows
        // them, and the developer's local changes are left out (their files and their index are not touched)
        List<string> mine = await RunChangesAsync(run, run.Worktree);
        string[] untracked = (await GitAsync(run.Worktree, "ls-files", "--others", "--exclude-standard", "-z")).Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => mine.Contains(p.Replace('\\', '/'))).ToArray();
        // literal pathspecs: a file name with glob characters ("*", "?", "[") names only itself
        if (untracked.Length > 0) await GitAsync(run.Worktree, ["add", "--intent-to-add", "--", .. untracked.Select(p => ":(literal)" + p)]);
        string[] exclude = run.LocalChanges.Keys.Where(k => !mine.Contains(k)).Select(k => ":(exclude,literal)" + k).ToArray();
        (_, string diff) = await GitAsync(run.Worktree, ["diff", run.BaseCommit, "--", ".", .. exclude]);
        string prompt = $"""
            You review a change before it becomes a pull request. Be strict and concrete; you can read the repo.
            THE TASK ({run.Source}):
            {run.Task}

            THE PLAN:
            {run.Plan}
            {(run.Feedback.Count > 0 ? $"""

            WHAT THE DEVELOPER ASKED FOR SINCE (it overrides the plan and its decisions - judge the change against this,
            and never flag as out of scope what the developer asked for):
            {string.Join("\n", run.Feedback.Select(f => $"- {(f.Revision > 0 ? $"revision {f.Revision}" : "feedback")}{(f.Kind.Length > 0 ? $" ({f.Kind})" : "")}: {f.Text}" + (f.Understood.Length > 0 ? $" - understood as: {f.Understood}" : "")))}
            """ : "")}

            THE DIFF (against {run.BaseBranch}):
            {(diff.Length > 60000 ? diff[..60000] + "\n... (cut)" : diff)}

            THE EVIDENCE - you can only read, so this is what was run for you:
            The verify command, run by the tool itself on this exact worktree: {run.VerifyResult}
            The proof the plan asked for: {(run.EvidencePlan.Count > 0 ? string.Join(", ", run.EvidencePlan) : "(none chosen)")}
            The evidence produced (its manifest):
            {EvidenceSummary(run)}
            {(beforeProof
                ? "The app check in the running app comes AFTER your review, so there is none yet - do not ask for it and do not count its absence. Judge the code, the tests and the verify output: tests missing for changed logic, or changed lines no test runs, are a real problem."
                : $"The check in the running app, by the coding agent:\n{(run.Proof.Length > 0 ? Tail(run.Proof, 4000) : "(none)")}\nWeak or missing proof for what the task changes (a planned kind not produced, a mutation score that leaves the changed logic untested, uncovered changed lines) is a real problem.")}

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
        if (run.Decisions.Length > 0) checks += $"\nDecisions the run took on its own (list them in the PR description under 'Decisions taken'):\n{run.Decisions}";
        if (run.OutsideChanges.Count > 0) checks += "\nChanges made outside the repo on this machine (list them in the PR description under 'Outside the repo'):\n" + string.Join("\n", run.OutsideChanges.Select(c => "- " + c));
        checks += $"\nThe evidence:\n{EvidenceSummary(run)}\nIts files are in {ProofDir(run)} - list it in the PR description (attach the images or video to the PR if the host's tools can), do not commit it. The app was already run after the last change; do not run it again unless a check on this machine requires it.";
        string link = run.Source == FactorySource.Jira ? $" Link it to the Jira issue {run.Task} the way this repo does." : "";
        // the git host's own MCP server first (Atlassian's for Bitbucket, GitHub's for GitHub), then its CLI or tools
        link += " For the pull request, prefer the git host's official MCP server when one is connected and signed in" +
            " (for example the Atlassian MCP for Bitbucket, the GitHub MCP for GitHub); otherwise use the host's CLI or" +
            " the tools on this machine. Say in your reply which one you used.";
        // 1. Claude commits - it cannot push yet
        (string committed, string? commitError) = await TurnAsync(run, isNew: false, $"""
            {checks}

            Now commit this change's files on branch {run.Branch} with a clear message. Do not push and do not open a
            pull request yet - Dev break checks the commit first.
            """, "Commit", cancel);
        if (commitError != null) return Wait(run, "The commit turn failed: " + commitError);
        Log(run, "Commit:\n" + committed);
        // 2. what the commits hold, checked in code: the developer's local files never leave the machine
        if (await HeadAsync(run.Worktree) != run.Branch) return Wait(run, $"After the commit the folder is not on {run.Branch} - nothing was pushed.");
        (int listed, string names) = await GitAsync(run.Worktree, "diff", "--name-only", run.BaseCommit + "..HEAD");
        if (listed != 0) return Wait(run, "Could not read what the commits hold - nothing was pushed: " + names.Trim());
        var inCommit = names.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> leaked = run.LocalChanges.Keys.Where(k => inCommit.Contains(k.Replace('\\', '/'))).ToList();
        if (leaked.Count > 0)
        {
            return Wait(run, $"The commit holds your own local file{(leaked.Count == 1 ? "" : "s")} {string.Join(", ", leaked)} - nothing was pushed. " +
                $"Take {(leaked.Count == 1 ? "it" : "them")} out of the commit (git reset --soft {run.BaseCommit[..Math.Min(12, run.BaseCommit.Length)]} and commit only the change's files), then Continue.");
        }
        if (inCommit.Count == 0) return Wait(run, "The commit holds no files of the change - nothing was pushed.");
        // no secret leaves the machine: the lines the commit adds are scanned before the push (as Copilot's agent does)
        (_, string added) = await GitAsync(run.Worktree, "diff", "--unified=0", "--no-color", run.BaseCommit + "..HEAD");
        List<string> secrets = SecretsIn(added);
        string secretsKey = string.Join("|", secrets);
        if (secrets.Count > 0 && secretsKey == run.SecretsAccepted)
        {
            Log(run, "The secret scan's findings were accepted by you as not secrets: " + string.Join("; ", secrets));
        }
        else if (secrets.Count > 0)
        {
            run.SecretsFound = secretsKey;
            return Wait(run, "The commit looks like it holds a secret - nothing was pushed:\n" + string.Join("\n", secrets.Take(5).Select(s => "- " + s)) +
                $"\nTake it out of the commit (git reset --soft {run.BaseCommit[..Math.Min(12, run.BaseCommit.Length)]}), or Continue if it is not a secret.");
        }
        string head = (await GitAsync(run.Worktree, "rev-parse", "HEAD")).Output.Trim();
        Log(run, $"Checked the commit {head[..Math.Min(12, head.Length)]}: {inCommit.Count} file{(inCommit.Count == 1 ? "" : "s")} of the change, none of your local files.");
        // 3. Claude pushes exactly that and opens the PR - it can no longer change what is committed
        string todo = update
            ? $"""
              The draft pull request {run.PrUrl} already exists for branch {run.Branch}. Push exactly the checked commit
              with: git push origin {head}:refs/heads/{run.Branch} - so the PR updates. If the description no longer
              fits (what changed, how it was verified), update it with the tools on this machine. Do not merge.
              Reply with the pull request URL on the last line.
              """
            : $"""
              Push exactly the checked commit with: git push origin {head}:refs/heads/{run.Branch} - then open a DRAFT pull request into
              {run.BaseBranch} with the tools on this machine.{link} The description: what changed and why, how it was
              verified (the command and its result), and the review notes. Do not merge.
              Reply with the pull request URL on the last line.
              """;
        (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
            {checks}

            The commit is made and checked. Do not commit, amend, reset or rebase - what was checked is what goes out.
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
        // 4. what reached the remote is what was checked
        (int remoteCode, string remote) = await GitAsync(run.Worktree, "ls-remote", "origin", "refs/heads/" + run.Branch);
        string pushed = remoteCode == 0 ? remote.Trim().Split('\t')[0] : "";
        if (pushed != head)
        {
            // fail closed: what is on the remote is not what was checked (or nothing is) - not a finished PR
            return Wait(run, pushed.Length == 0
                ? $"The branch {run.Branch} is not on the remote after the PR turn - check the push.\n" + answer
                : $"The remote branch is at {pushed[..Math.Min(12, pushed.Length)]}, not the checked commit {head[..Math.Min(12, head.Length)]} - " +
                  "something unchecked was pushed. Look at the branch before you use the PR.\n" + answer);
        }
        End(run, FactoryStage.Done, (update ? "Draft PR updated" : "Draft PR opened") + (anyway ? " - with failed checks." : "."));
        return true;
    }

    private const string MermaidInit = "mermaid.initialize({startOnLoad:true});";

    // the report is written by Claude from run data that may hold text from anywhere (an issue, a page, a log): it
    // runs only Dev break's own two scripts - Claude's scripts are removed, and a Content-Security-Policy lets nothing
    // else run (no other script, no inline handler) and loads nothing but the pinned Mermaid file and images
    private static string SafeReport(string html)
    {
        html = Regex.Replace(html, @"<script\b[^>]*>.*?</script\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<script\b[^>]*>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<meta\b[^>]*http-equiv\s*=\s*[""']?content-security-policy[^>]*>", "", RegexOptions.IgnoreCase);
        const string mermaid = "https://cdn.jsdelivr.net/npm/mermaid@10.9.1/dist/mermaid.min.js";
        string initHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(MermaidInit)));
        string head =
            $"<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data: https: file:; " +
            $"font-src data:; script-src {mermaid} 'sha256-{initHash}'\">\n" +
            $"<script src=\"{mermaid}\" integrity=\"sha384-WmdflGW9aGfoBdHc4rRyWzYuAjEmDwMdGdiPNacbwfGKxBW/SO6guzuQ76qjnSlr\" crossorigin=\"anonymous\"></script>\n";
        string init = $"<script>{MermaidInit}</script>\n";
        // the policy and the library first in <head>; the start call last in <body>, after the diagrams
        Match headTag = Regex.Match(html, @"<head\b[^>]*>", RegexOptions.IgnoreCase);
        html = headTag.Success ? html.Insert(headTag.Index + headTag.Length, "\n" + head)
            : Regex.Replace(html, @"<html\b[^>]*>", m => m.Value + "\n<head>\n" + head + "</head>", RegexOptions.IgnoreCase);
        int bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyEnd >= 0 ? html.Insert(bodyEnd, init) : html.Insert(html.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase), init);
    }

    // the designed report the developer asked for: the run's result as one readable HTML page, from its own facts only
    private static async Task ReportAsync(FactoryRun run)
    {
        Log(run, "Claude writes the designed report.");
        try
        {
            string prompt = $"""
                Turn the result of a coding agent's run into a report {(run.Department switch { FactoryDepartment.Troubleshooting => "the call center and a developer can both act on", FactoryDepartment.Management => "management decides from", _ => "a developer enjoys reading" })}. Use ONLY the facts
                below - add nothing, guess nothing; keep every file:line, command, number and "not verified" as it is.

                Reply with one complete, self-contained HTML document and nothing else (no code fence):
                {(run.Department switch { FactoryDepartment.Troubleshooting => CallCenterPart, FactoryDepartment.Management => ExecutivePart, _ => "" })}- a clear title and, first, a short summary box with the direct answer or outcome;
                - sections with headings for the main points; tables where the facts are tabular (steps, calls and
                  results, what is where); code, commands and curls in <pre> blocks;
                - a diagram where a flow or structure is easier to see than to read: Mermaid source in a
                  <pre class="mermaid"> (Dev break adds the Mermaid script itself - write no <script> and no event
                  handler attributes; any you write is removed);
                - "Decisions taken", "Not verified" and "Evidence" sections when the run has them; evidence files as
                  file:/// links;
                - clean inline CSS: readable sans-serif, max-width about 920px, generous spacing, light background,
                  subtle borders; no other external files.

                THE TASK: {run.Task}
                HOW IT ENDED: {run.Note}
                THE RESULT:
                {Tail(run.Result, 20000)}
                THE PLAN:
                {Tail(run.Plan, 8000)}
                DECISIONS TAKEN ON ITS OWN:
                {(run.Decisions.Length > 0 ? run.Decisions : "(none)")}
                THE EVIDENCE ({ProofDir(run)}):
                {EvidenceSummary(run)}
                {(run.PrUrl.Length > 0 ? "THE DRAFT PR: " + run.PrUrl : "")}
                """;
            string answer = await ClaudeOnceAsync(run, Path.GetTempPath(), prompt,
                ["--tools", "", "--strict-mcp-config", "--setting-sources", ""], "Report", CancellationToken.None);
            int start = answer.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
            if (start < 0) start = answer.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            int end = answer.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
            if (start < 0 || end < start) throw new InvalidOperationException("Claude did not reply with an HTML page");
            string file = Path.Combine(AppContext.BaseDirectory, "traces", run.Id + "-report.html");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, SafeReport(answer[start..(end + "</html>".Length)]));
            run.ReportFile = file;
            Log(run, "Report: " + file);
        }
        catch (Exception ex)
        {
            Log(run, "Could not write the report: " + ex.Message, ok: false);
        }
        Save();
        Notify(run);
    }

    private const int MaxManagerResumes = 2;
    private static readonly TimeSpan ManagerBudget = TimeSpan.FromMinutes(5);
    private const string ManagerLimits = """

        Decide within about five minutes. Look at the run's folder, its logs and the services it uses - never search
        whole drives, the Gradle home, the Ivy or Maven caches or node_modules.
        """;

    /// <summary>
    /// The department manager: an autonomous run stopped. It finds out why - the log, the run's folder, the machine -
    /// and either removes the cause and resumes the run (at most MaxManagerResumes times), or escalates to a human with
    /// the case: what is blocked, what it tried, exactly what it needs. It may also leave a rule for later runs, and
    /// propose changes to the factory itself, which only a human applies. True when the run is to be resumed.
    /// </summary>
    private static async Task<bool> ManageAsync(FactoryRun run)
    {
        string stopped = run.Note;
        Log(run, "The department manager looks at why the run stopped.");
        string log = string.Join("\n\n", run.Log.TakeLast(14).Select(s => $"[{s.Stage}{(s.Ok ? "" : ", FAILED")}] {Tail(s.Text, 1500)}"));
        string folder = Directory.Exists(run.Worktree) ? run.Worktree : run.Repo.Length > 0 && Directory.Exists(run.Repo) ? run.Repo : run.Folder;
        bool mayResume = run.ManagerResumes < MaxManagerResumes;
        string prompt = $"""
            You are the manager of a software-factory department ({run.Department}). One of its runs - an AI agent working
            on a task with no human in the loop - has stopped. Find out why, and decide whether the AI can get it going
            again or a human has to act. You are in the run's folder ({folder}); look at it, at this machine and at the
            running services with the tools here. Do not commit, push or open pull requests.

            THE TASK: {run.Task}
            WHY IT STOPPED: {stopped}
            THE LAST STEPS OF THE RUN:
            {Tail(log, 16000)}

            {(mayResume ? """
            RESUME when the cause is gone or you removed it: a false alarm, a passing error (a service that was down and is
            up now, a timeout, a lock), something you fixed in the run's folder or on this machine, or a wrong turn the run
            will not take again because of a RULE you add. Resuming continues the run from where it stopped - it hits the
            same stop again unless the cause is really gone, so check that it is.
            """ : $"The run was already resumed {run.ManagerResumes} times - do not resume it again; escalate.")}
            ESCALATE when it needs what no tool can do - a login, a credential, an access grant, a business decision - or
            a fix you could not make or could not verify. Say exactly what the human must do.
            When the cause is the factory's own behaviour (a step or a check that misjudged), describe the change to the
            factory that would have prevented it, for the people who build it to approve.

            Reply with what you found and did, then, each only when it applies:
            RULE: <one imperative line every later run in this repo gets, so it does not stop this way again>
            FACTORY_FIX: <a change to the factory itself - what and why>
            OUTSIDE: <a file you changed outside the run's folder> - <what> - <its backup>
            End with exactly one line:
            DECISION: RESUME <what you did and why the run can go on now>
            DECISION: ESCALATE <exactly what the human must do, in one or two sentences>
            """;
        string answer;
        // a manager decides in minutes or hands it on - one spent 12 minutes on a find over the whole build cache
        using var budget = new CancellationTokenSource(ManagerBudget);
        try
        {
            answer = await ClaudeOnceAsync(run, folder, prompt + ManagerLimits, ["--permission-mode", "bypassPermissions", .. NoPublish, .. KnowledgeArgs(run, "nopublish")],
                "Manager", budget.Token);
        }
        catch (OperationCanceledException)
        {
            answer = $"DECISION: ESCALATE The manager could not decide within {ManagerBudget.TotalMinutes:0} minutes. The run stopped with: {stopped}";
        }
        catch (Exception ex)
        {
            answer = "DECISION: ESCALATE The manager could not look at the run (" + ex.Message + "). " + stopped;
        }
        string[] lines = answer.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('*', '-', ' ')).ToArray();
        foreach (string rule in lines.Where(l => l.StartsWith("RULE:", StringComparison.OrdinalIgnoreCase)).Select(l => l[5..].Trim()).Where(r => r.Length > 0))
        {
            var lesson = new FactoryLesson { Kind = "claude-md", Name = Slug("manager " + rule), Description = rule, Content = rule };
            run.Lessons.Add(lesson);
            try
            {
                if (run.Home.Length > 0) Apply(run, lesson);
            }
            catch (Exception ex)
            {
                Log(run, $"Could not apply the manager's rule: {ex.Message}", ok: false);
            }
        }
        foreach (string fix in lines.Where(l => l.StartsWith("FACTORY_FIX:", StringComparison.OrdinalIgnoreCase)).Select(l => l[12..].Trim()).Where(f => f.Length > 0))
        {
            if (!run.FactoryFixes.Contains(fix)) run.FactoryFixes.Add(fix);
        }
        foreach (string outside in lines.Where(l => l.StartsWith("OUTSIDE:", StringComparison.OrdinalIgnoreCase)).Select(l => l[8..].Trim()))
        {
            if (!run.OutsideChanges.Contains(outside)) run.OutsideChanges.Add(outside);
        }
        string decision = lines.LastOrDefault(l => l.StartsWith("DECISION:", StringComparison.OrdinalIgnoreCase)) ?? "";
        string what = decision.Length > 9 ? decision[9..].Trim() : "";
        if (mayResume && what.StartsWith("RESUME", StringComparison.OrdinalIgnoreCase))
        {
            run.ManagerResumes++;
            run.ManagerCase = "";
            Log(run, $"Manager: resumes the run ({run.ManagerResumes} of {MaxManagerResumes}) - {what[6..].Trim()}\n\n{answer}");
            return true;
        }
        string need = what.StartsWith("ESCALATE", StringComparison.OrdinalIgnoreCase) ? what[8..].Trim()
            : "The manager gave no decision. The run stopped with: " + stopped;
        run.ManagerCase = $"{need}\n\nWhy it stopped: {stopped.Split('\n')[0]}" +
            (run.FactoryFixes.Count > 0 ? "\n\nProposed changes to the factory (for you to approve):\n" + string.Join("\n", run.FactoryFixes.Select(f => "- " + f)) : "");
        Log(run, "Manager: needs a human - " + need + "\n\n" + answer, ok: false);
        Wait(run, "Needs a human: " + need);
        return false;
    }

    // what the run taught about this repo: memory notes are saved at once; rules wait for the developer's OK
    private static async Task LearnAsync(FactoryRun run)
    {
        FactoryStage ended = run.Stage;
        string note = run.Note;
        Enter(run, FactoryStage.Learn, "Claude looks back at the run for what to remember.");
        try
        {
            if (run.Repo.Length == 0 && !run.FolderWide) throw new InvalidOperationException("the run never found its repo");
            string? memoryDir = await CliSetup.MemoryDirAsync(run.Home);
            string index = memoryDir != null && File.Exists(Path.Combine(memoryDir, "MEMORY.md")) ? File.ReadAllText(Path.Combine(memoryDir, "MEMORY.md")) : "";
            string log = string.Join("\n\n", run.Log.Select(s => $"[{s.Stage}{(s.Ok ? "" : ", FAILED")}] {Tail(s.Text, 1500)}"));
            // what the developer said: their feedback, their rating, and the lessons they turned down for this repo
            string said = string.Join("\n", run.Feedback.Select(f => $"- feedback{(f.Revision > 0 ? $" asking for revision {f.Revision}" : "")}{(f.Kind.Length > 0 ? $" ({f.Kind})" : "")}: {f.Text}" +
                (f.Understood.Length > 0 ? $" - the run understood: {f.Understood}" : "")));
            if (run.Accepted && run.Revision > 1)
            {
                said += $"\n- accepted the work at revision {run.Revision}: the rounds of changes above show what the developer meant " +
                    "the first time - keep that as a lesson (what such a request means in this repo), so the next run gets it right at once";
            }
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

                THE FACTORY'S RULES AND SKILLS FOR THIS REPO ALREADY (do not repeat them):
                {FactoryKnows(run.Home)}

                ALREADY LEARNED FROM THIS RUN (do not repeat it):
                {(already.Length > 0 ? already : "(nothing yet)")}

                LESSONS THE DEVELOPER TURNED DOWN FOR THIS REPO (do not suggest anything like them):
                {(rejected.Length > 0 ? rejected : "(none)")}

                WHAT WAS SLOW (the developer wants every run faster - steps and single tool calls, slowest first):
                {SpeedSummary(run)}
                For what was slow and avoidable here, add a lesson that makes the next run faster: e.g. dependencies are
                already installed (do not reinstall), run only the tests for the change, a faster command for the same
                check, a step that waited for something it did not need.{(run.Autonomous && (ended == FactoryStage.NeedsYou || run.Retried) ? $"""


                THE RUN GOT STUCK{(run.Retried ? " (and needed a fresh attempt)" : "")}: {note.Split('\n')[0]}
                The developer wants the factory to run with no human in the loop, correcting itself: find what would have
                let this run finish on its own, and add it - a memory fact, a rule, a skill or a hook - so the
                next run does not get stuck the same way. Rules and hooks are applied at once.
                """ : "")}

                What the next run here needs most, so it starts fast and knows its way - keep what this run had to find out:
                - running the service: the fastest way to build, start, restart and check it (exact commands, ports,
                  health URL, environment such as JAVA_HOME), and what to skip;
                - the logic: where the code for this kind of task lives (files, classes, endpoints, how they connect,
                  who calls them) and the repo's conventions for it;
                - the language and framework: facts it had to look up (the Java version and the features this code
                  uses, framework versions and their quirks).

                A fact about a state that can change - access, a login or credential, a connector or MCP server, whether a
                service is up, a setting on this machine - is not a standing truth: end it with "Checked on
                {DateTime.Now:yyyy-MM-dd} - re-check before relying on it." (a run once kept "the connectors are not
                authorised", and the next run skipped them after they were signed in).

                Lessons can be:
                - memory: a fact about this repo the run had to discover (a build or test quirk, a command, where things
                  live, a gotcha). Under 100 words. Most lessons.
                - claude-md: one imperative rule line every later factory run in this repo gets - only for a mistake
                  the agent made that a standing rule would prevent.
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
                // autonomous: rules apply at once too (each can be undone)
                if (run.Autonomous && lesson.Status == "pending")
                {
                    try
                    {
                        Apply(run, lesson);
                    }
                    catch (Exception ex)
                    {
                        Log(run, $"Could not apply the {lesson.Kind} rule {lesson.Name}: {ex.Message}", ok: false);
                    }
                }
            }
            List<FactoryLesson> learned = run.Lessons[before..];
            Log(run, learned.Count == 0 ? "Nothing new to remember." :
                $"Learned {learned.Count}: {learned.Count(l => l.Status == "saved")} saved to memory, {learned.Count(l => l.Status == "applied")} rules applied, " +
                $"{learned.Count(l => l.Status == "pending")} rules wait for you.");
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
        if (lesson.Status == "applied")
        {
            UndoRule(run, lesson);
            lesson.Feedback = why.Trim();
            lesson.Status = "undone";
            Save();
            Notify(run);
            return;
        }
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

    // takes an applied rule back out: the CLAUDE.md text appended, the skill's folder, the hook settings as they were
    private static void UndoRule(FactoryRun run, FactoryLesson lesson)
    {
        switch (lesson.Kind)
        {
            case "claude-md" when lesson.File != null && File.Exists(lesson.File) && lesson.UndoData.Length > 0:
                string text = File.ReadAllText(lesson.File);
                int at = text.LastIndexOf(lesson.UndoData, StringComparison.Ordinal);
                if (at >= 0) File.WriteAllText(lesson.File, text.Remove(at, lesson.UndoData.Length));
                break;
            case "skill" when lesson.UndoData.Length > 0 && File.Exists(lesson.UndoData):
                Directory.Delete(Path.GetDirectoryName(lesson.UndoData)!, recursive: true);
                break;
            case "hook" when lesson.File != null:
                if (lesson.UndoData == "new") File.Delete(lesson.File);
                else if (File.Exists(lesson.UndoData)) File.Copy(lesson.UndoData, lesson.File, overwrite: true);
                break;
        }
    }

    /// <summary>Applies a rule the developer approved; returns the file it went into.</summary>
    public static string Apply(FactoryRun run, FactoryLesson lesson)
    {
        string file;
        switch (lesson.Kind)
        {
            case "claude-md":
                Directory.CreateDirectory(KnowledgeDir(run.Home));
                file = Path.Combine(KnowledgeDir(run.Home), "rules.md");
                string existing = File.Exists(file) ? File.ReadAllText(file) : "Rules the Dev break factory learned in this repo - follow them:\n";
                string appended = (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + lesson.Content.Trim() + "\n";
                if (!File.Exists(file)) File.WriteAllText(file, existing);
                File.AppendAllText(file, appended);
                lesson.UndoData = appended;
                break;
            case "skill":
                // a skill of the factory's own plugin for this repo, loaded with --plugin-dir
                string plugin = Path.Combine(KnowledgeDir(run.Home), "plugin");
                string manifest = Path.Combine(plugin, ".claude-plugin", "plugin.json");
                if (!File.Exists(manifest))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                    File.WriteAllText(manifest, """{"name":"devbreak-factory","version":"1.0.0","description":"What the Dev break factory learned in this repo"}""");
                }
                string skillDir = Path.Combine(plugin, "skills", lesson.Name);
                for (int n = 2; Directory.Exists(skillDir); n++) skillDir = Path.Combine(plugin, "skills", $"{lesson.Name}-{n}");
                Directory.CreateDirectory(skillDir);
                file = Path.Combine(skillDir, "SKILL.md");
                File.WriteAllText(file, $"---\nname: {Path.GetFileName(skillDir)}\ndescription: {lesson.Description.Replace("\n", " ")}\n---\n\n{lesson.Content.Trim()}\n");
                lesson.UndoData = file;
                break;
            case "hook":
                file = Path.Combine(KnowledgeDir(run.Home), "settings.json");
                // the settings as they were, for Undo ("new" when there was no file)
                string backup = file + ".bak-devbreak-" + lesson.Name;
                if (File.Exists(file)) File.Copy(file, backup, overwrite: true);
                lesson.UndoData = File.Exists(file) ? backup : "new";
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
        prompt += (run.Autonomous ? AutonomousRule : OutsideRule) + EditRule;
        FactorySpan span = Begin(run, "turn", title, prompt);
        string answer = "";
        string? error = null;
        try
        {
            // troubleshooting, research and management change nothing: git's writing commands are denied too (they
            // may work right in the developer's folder - a folder-wide run has no branch of its own)
            bool investigation = run.Department != FactoryDepartment.Development;
            string guard = investigation ? "all" : publish ? "frozen" : "nopublish";
            string[] args = [.. publish ? Frozen : NoPublish, .. investigation ? Frozen[1..] : [], .. KnowledgeArgs(run, guard)];
            Action<SessionEntry> onEntry = entry =>
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
            };
            // a turn going round in circles is stopped, not waited out (the manager takes it from there)
            using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            string? stuck = null;
            await LiveFor(run, isNew, args).TurnAsync(prompt, onEntry, turn.Token, root =>
            {
                Observe(run, span, root);
                if (stuck == null && Stuck(span) is { } why)
                {
                    stuck = why;
                    turn.Cancel();
                }
            });
            if (stuck != null && !cancel.IsCancellationRequested) error = "The turn was stuck: " + stuck;
        }
        finally
        {
            if (error != null) CloseLive(run);
            span.Error = error ?? (cancel.IsCancellationRequested ? "stopped" : "");
            Finish(run, span, answer);
        }
        cancel.ThrowIfCancellationRequested();
        // what it changed outside its folder, as it reported it
        foreach (string outside in answer.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('*', '-', ' '))
                     .Where(l => l.StartsWith("OUTSIDE:", StringComparison.OrdinalIgnoreCase)).Select(l => l[8..].Trim()))
        {
            if (!run.OutsideChanges.Contains(outside)) run.OutsideChanges.Add(outside);
            Log(run, "Changed outside the repo: " + outside);
        }
        // Claude needs something changed outside the folder: the developer decides
        if (error == null && Line(answer, "NEEDS_YOU:") is { Length: > 0 } need)
        {
            Log(run, $"{title}:\n{answer}");
            throw new NeedsYouException("Claude needs you: " + need + MissingFiles(need, run.Worktree));
        }
        return (answer, error);
    }

    // common secret shapes in the lines a diff adds: "file: what" for each, the value itself never repeated
    private static readonly (string What, Regex Pattern)[] SecretShapes =
    [
        ("a private key", new Regex(@"-----BEGIN (RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY")),
        ("an AWS access key", new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")),
        ("a Google API key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b")),
        ("a GitHub token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b")),
        ("a Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}")),
        ("a password, key or token set to a literal", new Regex(@"(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret)\b\s*[:=]\s*[""'][^""'\s$%{}<>]{8,}[""']")),
    ];

    private static List<string> SecretsIn(string diff)
    {
        var found = new List<string>();
        string file = "";
        foreach (string line in diff.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal)) file = line[6..];
            else if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                foreach ((string what, Regex pattern) in SecretShapes)
                {
                    // a short hash of the matched text, so accepting one finding does not let a new value of the same kind through
                    if (pattern.Match(line) is { Success: true } hit)
                    {
                        found.Add($"{file}: {what} #{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(hit.Value)))[..8]}");
                    }
                }
            }
        }
        return found.Distinct().ToList();
    }

    // the files a request to the developer names that are not there - a run once asked to paste a file it never
    // wrote; checked in code, so the developer is told before acting on it
    private static string MissingFiles(string text, string folder)
    {
        var missing = Regex.Matches(text, @"`([^`\r\n]+\.[A-Za-z0-9]{1,6})`|([A-Za-z]:\\[^\s`'""*?<>|]+\.[A-Za-z0-9]{1,6})")
            .Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim().TrimEnd('.', ',', ')'))
            .Where(p => p.Contains('\\') || p.Contains('/'))
            // never probe a network path: checking \\host\share would send this machine's Windows login to that host
            .Where(p => !p.StartsWith(@"\\", StringComparison.Ordinal) && !p.StartsWith("//", StringComparison.Ordinal))
            .Distinct()
            .Where(p =>
            {
                try
                {
                    string full = Path.IsPathRooted(p) ? p : Path.Combine(folder, p);
                    return !File.Exists(full) && !Directory.Exists(full);
                }
                catch (Exception)
                {
                    return false;
                }
            })
            .ToList();
        return missing.Count > 0 ? $"\n(Not on disk - check before acting on it: {string.Join(", ", missing)})" : "";
    }

    // what the factory learned for a repo, next to the app and never in the repo (the checkout stays clean):
    // rules.md goes into every turn's system prompt, plugin\skills are its skills, settings.json holds its hooks
    public static string KnowledgeDir(string repo) => Path.Combine(AppContext.BaseDirectory, "factory-knowledge",
        Regex.Replace(Path.GetFullPath(repo).TrimEnd('\\', '/'), "[^A-Za-z0-9]+", "-").Trim('-'));

    // the factory's rules and skill names for a repo, for Learn not to repeat them
    private static string FactoryKnows(string repo)
    {
        string dir = KnowledgeDir(repo);
        string rules = Path.Combine(dir, "rules.md");
        string skills = Path.Combine(dir, "plugin", "skills");
        string known = (File.Exists(rules) ? File.ReadAllText(rules).Trim() : "") +
            (Directory.Exists(skills) ? "\nSkills: " + string.Join(", ", Directory.EnumerateDirectories(skills).Select(Path.GetFileName)) : "");
        return known.Trim().Length > 0 ? known.Trim() : "(nothing yet)";
    }

    // the claude arguments that bring that knowledge into a turn
    private static List<string> KnowledgeArgs(FactoryRun run, string guard = "")
    {
        var args = new List<string>();
        string? dir = run.Home.Length > 0 ? KnowledgeDir(run.Home) : null;
        if (dir != null)
        {
            string rules = Path.Combine(dir, "rules.md");
            if (File.Exists(rules)) args.AddRange(["--append-system-prompt-file", rules]);
            string skills = Path.Combine(dir, "plugin", "skills");
            if (Directory.Exists(skills) && Directory.EnumerateDirectories(skills).Any()) args.AddRange(["--plugin-dir", Path.Combine(dir, "plugin")]);
        }
        // one --settings: the hooks the factory learned for the repo, plus the guard for this kind of turn
        string learned = dir != null ? Path.Combine(dir, "settings.json") : "";
        if (GuardSettings(learned, guard) is { } settings) args.AddRange(["--settings", settings]);
        return args;
    }

    // the command guard: --disallowedTools rules match only the start of a command, so `git -c user.name=x commit`
    // got past "Bash(git commit:*)" in a test. This PreToolUse hook reads the whole command (options before the
    // subcommand stripped) and refuses what the turn may not do. Modes: nopublish (push, gh pr create), frozen (git
    // writing commands except push), all (both).
    private const string GuardScript = """
        param([string]$Mode)
        $in = [Console]::In.ReadToEnd()
        try { $j = $in | ConvertFrom-Json } catch { exit 0 }
        $tool = [string]$j.tool_name
        # git tools of MCP servers (e.g. a git_commit or git_push tool) never pass through the shell
        if ($tool -like 'mcp__*') {
            $mcpPublish = '(?i)(git_push|pull_request_create|create_pull_request|createPullRequest)'
            $mcpWrite = '(?i)git_(commit|add|reset|rebase|cherry_pick|merge|stash|checkout|switch|restore|rm|mv)'
            $mcpDeny = switch ($Mode) { 'nopublish' { $mcpPublish } 'frozen' { $mcpWrite } default { "$mcpPublish|$mcpWrite" } }
            if ($tool -match $mcpDeny) { [Console]::Error.WriteLine("Dev break factory: the tool $tool is not allowed in this step."); exit 2 }
            exit 0
        }
        if ($tool -notin 'Bash', 'PowerShell') { exit 0 }
        $cmd = [string]$j.tool_input.command
        # git.exe, "C:\...\git.exe" and /usr/bin/git all read as git; then the options before the subcommand go,
        # including the ones whose value follows after a space
        $flat = [regex]::Replace($cmd, '["'']?(?:[^\s"'']*[\\/])?\bgit(?:\.exe)?["'']?(?=\s)', 'git', 'IgnoreCase')
        $flat = [regex]::Replace($flat, '\bgit((\s+(-[cC]\s+("[^"]*"|''[^'']*''|\S+)|--(git-dir|work-tree|namespace|exec-path|super-prefix|config-env)\s+("[^"]*"|''[^'']*''|\S+)|--?[\w-]+(=\S+)?))+)', 'git')
        # an alias can hide any subcommand (git -c alias.p=push p), so a git alias is refused in every step
        if ($cmd -match '\bgit(\.exe)?\b.*\balias\.') { [Console]::Error.WriteLine("Dev break factory: git aliases are not allowed in this step."); exit 2 }
        $publish = '\bgit\s+push\b|\bgh\s+pr\s+create\b'
        $write = '\bgit\s+(commit|add|reset|rebase|cherry-pick|merge|stash|checkout|switch|restore|rm|mv)\b'
        $deny = switch ($Mode) { 'nopublish' { $publish } 'frozen' { $write } default { "$publish|$write" } }
        if ($flat -match $deny) {
            [Console]::Error.WriteLine("Dev break factory: '$($Matches[0])' is not allowed in this step.")
            exit 2
        }
        exit 0
        """;

    // a settings file for the turn: the learned hooks (if any) with the guard added; null when there is neither
    private static string? GuardSettings(string learned, string guard)
    {
        if (guard.Length == 0) return learned.Length > 0 && File.Exists(learned) ? learned : null;
        string home = Path.Combine(AppContext.BaseDirectory, "factory-guard");
        Directory.CreateDirectory(home);
        string script = Path.Combine(home, "guard.ps1");
        if (!File.Exists(script) || File.ReadAllText(script) != GuardScript) File.WriteAllText(script, GuardScript);
        JsonObject settings = learned.Length > 0 && File.Exists(learned) ? JsonNode.Parse(File.ReadAllText(learned))?.AsObject() ?? [] : [];
        if (settings["hooks"] is not JsonObject hooks) settings["hooks"] = hooks = [];
        if (hooks["PreToolUse"] is not JsonArray pre) hooks["PreToolUse"] = pre = [];
        pre.Add(new JsonObject
        {
            ["matcher"] = "Bash|PowerShell|mcp__.*",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Mode {guard}",
            }),
        });
        string file = Path.Combine(home, $"settings-{guard}-{Math.Abs(learned.GetHashCode()):x}.json");
        File.WriteAllText(file, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    // stuck detection, with OpenHands' thresholds and, like it, only for repeats in a row (a long turn may well run
    // `git status` four times): the same finished call 4 times running, or the same call failing 3 times running
    private static string? Stuck(FactorySpan span)
    {
        List<FactoryToolCall> done = span.Tools.Where(t => t.Result != "running" && t.Detail.Length > 0).ToList();
        bool Same(int n, Func<FactoryToolCall, bool> also) => done.Count >= n &&
            done.TakeLast(n).All(t => t.Name == done[^1].Name && t.Detail == done[^1].Detail && t.Result == done[^1].Result && also(t));
        if (Same(3, t => t.Result == "error")) return $"the same {done[^1].Name} call failed 3 times in a row: {done[^1].Detail}";
        if (Same(4, _ => true)) return $"it ran the same {done[^1].Name} call 4 times in a row: {done[^1].Detail}";
        return null;
    }

    // the run's open claude process when it fits this turn (same folder, session and arguments), else a new one -
    // the one it replaces is ended first, so two processes never hold the same session
    private static ClaudeSessions.LiveSession LiveFor(FactoryRun run, bool isNew, string[] args)
    {
        lock (Live)
        {
            if (Live.TryGetValue(run.Id, out ClaudeSessions.LiveSession? open))
            {
                if (open.Usable && open.Key == ClaudeSessions.LiveSession.KeyOf(run.Worktree, run.SessionId, args)) return open;
                open.Dispose();
                Live.Remove(run.Id);
            }
            ClaudeSessions.LiveSession live = ClaudeSessions.LiveSession.Start(run.Worktree, run.SessionId, isNew, args);
            Live[run.Id] = live;
            return live;
        }
    }

    private static void CloseLive(FactoryRun run)
    {
        lock (Live)
        {
            if (Live.Remove(run.Id, out ClaudeSessions.LiveSession? open)) open.Dispose();
        }
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
