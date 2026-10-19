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
    public string Prompt { get; set; } = "";
    public string Answer { get; set; } = "";
    public string Error { get; set; } = "";
    public List<FactoryToolCall> Tools { get; set; } = [];
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
    /// <summary>The last verify run: its exit code and the end of its output (the reviewer reads it).</summary>
    public string VerifyResult { get; set; } = "";
    /// <summary>What Claude saw when it ran the app with the change, ending PROOF: PASS / FAIL / SKIPPED.</summary>
    public string Proof { get; set; } = "";
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
    private const string HookExample = """{"PostToolUse":[{"matcher":"Edit|Write","hooks":[{"type":"command","command":"..."}]}]}""";
    private static readonly string StorePath = Path.Combine(AppContext.BaseDirectory, "factory.json");
    private static readonly string WorktreesDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBreak", "worktrees");

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

    /// <summary>After a run waited on failed checks: opens the draft PR anyway (it says what failed).</summary>
    public static Task PullRequestAnywayAsync(FactoryRun run) => DriveAsync(run, learnAfter: false, async cancel =>
    {
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
        Enter(run, FactoryStage.Build, "Your feedback: " + text.Trim());
        (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
            The developer looked at your change and says:
            {text.Trim()}

            Apply it in this worktree (if part of it is wrong, say why instead of doing it). Do not commit or push.
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
        catch (Exception ex)
        {
            Wait(run, "Something went wrong: " + ex.Message);
        }
        finally
        {
            if (cancel.IsCancellationRequested) End(run, FactoryStage.Stopped, "Stopped.");
            else if (learn) await LearnAsync(run);
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
        if (run.Verify.Length == 0) run.Verify = AppSettings.Get("factoryVerify:" + repo.ToLowerInvariant()) ?? "";
        run.Branch = $"devbreak/{Slug(run.Title)}-{run.Id}";
        run.Worktree = Path.Combine(WorktreesDir, $"{Path.GetFileName(repo)}-{run.Id}");
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

    private static async Task<bool> WorktreeAsync(FactoryRun run, CancellationToken cancel)
    {
        Enter(run, FactoryStage.Worktree, $"New worktree {run.Worktree} on branch {run.Branch}, from {run.BaseBranch}.");
        (int exit, string output) = await GitSpanAsync(run, run.Repo, "rev-parse", run.BaseBranch);
        if (exit != 0) return Wait(run, $"Can't find branch {run.BaseBranch} in {run.Repo}: {output.Trim()}");
        run.BaseCommit = output.Trim();
        Directory.CreateDirectory(WorktreesDir);
        cancel.ThrowIfCancellationRequested();
        (exit, output) = await GitSpanAsync(run, run.Repo, "worktree", "add", run.Worktree, "-b", run.Branch, run.BaseCommit);
        if (exit != 0) return Wait(run, "Could not create the worktree: " + output.Trim());
        return true;
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
            : """
              End with one line: VERIFY: <one shell command, run from this folder in PowerShell, that builds this repo and
              runs its tests - the proof the change works>.
              """;
        string prompt = $"""
            You are working in a fresh git worktree of {run.Repo}, on branch {run.Branch} (from {run.BaseBranch}).
            {task}

            First, understand the mission and plan - do not change any file yet. Use the skills, MCP servers and tools on
            this machine that fit the task (ones that run the app or its services, read logs or data, inspect or debug a
            running service) to see the current behaviour and where it lives, not only the code. Learn how this repo is
            built, run and tested (its README, build files and scripts), so the plan and the verify command fit it, and
            say how the change can be seen in the running app. If you cannot read the task, reply with one line:
            CANNOT_READ: <why>. Otherwise reply with a short title for the work on the first line (TITLE: <title>), then
            the plan as short numbered steps, naming the files you will change and the tests you will add or run.
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
        Log(run, "Plan:\n" + answer);
        return true;
    }

    private static async Task<bool> BuildAsync(FactoryRun run, CancellationToken cancel)
    {
        Enter(run, FactoryStage.Build, "Claude implements the plan.");
        (string answer, string? error) = await TurnAsync(run, isNew: false, """
            Now implement the plan in this worktree. Change only what the task needs, and add or update tests for it.
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
            (int changed, string status) = await GitAsync(run.Worktree, "status", "--porcelain");
            if (changed == 0 && status.Trim().Length == 0) return Wait(run, "Claude changed no files.");
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
                Do not commit or push. Reply with what you changed.
                """, $"Fix verify failure {attempt}", cancel);
            if (error != null) return Wait(run, "The fix turn failed: " + error);
            Log(run, "Fix:\n" + answer);
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
            (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
                Now prove the change works in the running application, the way a developer would before opening a PR.
                Start what it needs - the app and the services it depends on - with the skills, MCP servers and tools on
                this machine, from this worktree. Exercise exactly what the task changed: in a browser for a UI change
                (the sizes it matters at), by calling the API for a service, by reading its logs or attaching a debugger
                for back-end behaviour. Stop what you started when you are done.
                Save the evidence - screenshots, responses, log excerpts - into {dir}, not into the repo.
                Do not commit or push. Reply with what you ran and what you saw, and end with exactly one line:
                PROOF: PASS - it works as the task asks
                PROOF: FAIL <what is wrong>
                PROOF: SKIPPED <why it cannot be run on this machine>
                """, $"Prove (attempt {attempt})", cancel);
            if (error != null) return Wait(run, "The prove turn failed: " + error);
            run.Proof = answer;
            string verdict = Line(answer, "PROOF:") ?? "";
            bool pass = verdict.StartsWith("PASS", StringComparison.OrdinalIgnoreCase);
            bool skipped = verdict.StartsWith("SKIPPED", StringComparison.OrdinalIgnoreCase);
            int files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
            Log(run, $"Proof: {(verdict.Length > 0 ? verdict : "no verdict")} ({files} evidence file{(files == 1 ? "" : "s")})\n{answer}", ok: pass || skipped);
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

    private static async Task<(bool Pass, string Findings)> ReviewAsync(FactoryRun run, CancellationToken cancel)
    {
        await GitAsync(run.Worktree, "add", "-A");
        (_, string diff) = await GitAsync(run.Worktree, "diff", "--cached", run.BaseCommit);
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
            The check in the running app, by the coding agent:
            {(run.Proof.Length > 0 ? Tail(run.Proof, 4000) : "(none)")}

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
        checks += $"\nThe app check's evidence is in {ProofDir(run)} - describe it in the PR, do not commit it. The app was already run after the last change; do not run it again unless a check on this machine requires it.";
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
            md.AppendLine("## Evidence from the app").AppendLine();
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
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start PowerShell");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
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
