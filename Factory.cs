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
public enum FactoryStage { Queued, Worktree, Plan, Build, Verify, Review, PullRequest, Learn, Done, NeedsYou, Stopped }

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
}

/// <summary>A task taken from its source to a draft pull request, in its own worktree and branch.</summary>
public sealed class FactoryRun
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public FactorySource Source { get; set; }
    public string Task { get; set; } = "";
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
    public string Review { get; set; } = "";
    public string PrUrl { get; set; } = "";
    public double Cost { get; set; }
    public int Steps { get; set; }
    public DateTime Created { get; set; }
    public List<FactoryStep> Log { get; set; } = [];
    public List<FactoryLesson> Lessons { get; set; } = [];

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

    public static FactoryRun Create(FactorySource source, string task, string repo, string baseBranch, string verify)
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
            Id = id, Title = title, Source = source, Task = task.Trim(), Repo = repo.TrimEnd('\\'), BaseBranch = baseBranch.Trim(),
            Verify = verify.Trim(), Branch = $"devbreak/{Slug(title)}-{id}", SessionId = Guid.NewGuid().ToString(),
            Worktree = Path.Combine(WorktreesDir, $"{Path.GetFileName(repo.TrimEnd('\\'))}-{id}"), Created = DateTime.Now,
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
    public static async Task RunAsync(FactoryRun run)
    {
        if (run.Working) return;
        run.Running = new CancellationTokenSource();
        CancellationToken cancel = run.Running.Token;
        bool learn = false;
        try
        {
            if (!await WorktreeAsync(run)) return;
            learn = true;
            if (!await PlanAsync(run, cancel)) return;
            if (!await BuildAsync(run, cancel)) return;
            if (!await CheckAsync(run, cancel)) return;
            await PullRequestAsync(run, cancel);
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

    /// <summary>After a run waited on failed checks: opens the draft PR anyway (it says what failed), then learns.</summary>
    public static async Task PullRequestAnywayAsync(FactoryRun run)
    {
        if (run.Working) return;
        run.Running = new CancellationTokenSource();
        try
        {
            await PullRequestAsync(run, run.Running.Token, anyway: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Wait(run, "Something went wrong: " + ex.Message);
        }
        finally
        {
            if (run.Running.IsCancellationRequested) End(run, FactoryStage.Stopped, "Stopped.");
            run.Running.Dispose();
            run.Running = null;
            Notify(run);
        }
    }

    public static void Stop(FactoryRun run) => run.Running?.Cancel();

    // --- the stages ---

    private static async Task<bool> WorktreeAsync(FactoryRun run)
    {
        Enter(run, FactoryStage.Worktree, $"New worktree {run.Worktree} on branch {run.Branch}, from {run.BaseBranch}.");
        (int exit, string output) = await GitAsync(run.Repo, "rev-parse", run.BaseBranch);
        if (exit != 0) return Wait(run, $"Can't find branch {run.BaseBranch} in {run.Repo}: {output.Trim()}");
        run.BaseCommit = output.Trim();
        Directory.CreateDirectory(WorktreesDir);
        (exit, output) = await GitAsync(run.Repo, "worktree", "add", run.Worktree, "-b", run.Branch, run.BaseCommit);
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

            First, plan only - do not change any file yet. If you cannot read the task, reply with one line:
            CANNOT_READ: <why>. Otherwise reply with a short title for the work on the first line (TITLE: <title>), then
            the plan as short numbered steps, naming the files you will change and the tests you will add or run.
            {verify}
            """;
        (string answer, string? error) = await TurnAsync(run, isNew: true, prompt, cancel);
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
            """, cancel);
        if (error != null) return Wait(run, "The build turn failed: " + error);
        Log(run, "Build:\n" + answer);
        return true;
    }

    // verify, fixing up to MaxFixes times; then review, fixing what the reviewer finds, up to MaxReviews rounds
    private static async Task<bool> CheckAsync(FactoryRun run, CancellationToken cancel)
    {
        for (int review = 1; ; review++)
        {
            if (!await VerifyAsync(run, cancel)) return false;
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
                """, cancel);
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
            (int exit, string output) = await ShellAsync(run.Worktree, run.Verify, cancel);
            Log(run, $"Verify exit {exit}:\n```\n{Tail(output, 3000)}\n```", ok: exit == 0);
            if (exit == 0) return true;
            if (attempt > MaxFixes) return Wait(run, $"The verify command still fails after {MaxFixes} fixes.");
            Enter(run, FactoryStage.Build, $"Claude fixes the failure (fix {attempt} of {MaxFixes}).");
            (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
                The verify command `{run.Verify}` failed with exit code {exit}. The end of its output:
                {Tail(output, 6000)}

                Find the cause and fix it. Fix the code, not the test, unless the test itself is wrong (then say why).
                Do not commit or push. Reply with what you changed.
                """, cancel);
            if (error != null) return Wait(run, "The fix turn failed: " + error);
            Log(run, "Fix:\n" + answer);
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

            Report only real problems: bugs, a requirement of the task that is missing, changes the task did not ask
            for, missing or weak tests, security issues. No style nits. Reply with VERDICT: PASS or VERDICT: FIX on the
            first line, then the findings as a short list (file:line - problem - what to do). PASS when nothing real is left.
            """;
        (string answer, double cost) = await ClaudeJsonAsync(run.Worktree, prompt, ["--tools", "Read,Grep,Glob", "--allowedTools", "Read,Grep,Glob"], cancel);
        run.Cost += cost;
        bool pass = Line(answer, "VERDICT:")?.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) == true;
        int newline = answer.IndexOf('\n');
        return (pass, newline > 0 ? answer[(newline + 1)..].Trim() : answer.Trim());
    }

    private static async Task<bool> PullRequestAsync(FactoryRun run, CancellationToken cancel, bool anyway = false)
    {
        Enter(run, FactoryStage.PullRequest, "Claude commits, pushes the branch and opens a draft PR.");
        string checks = anyway
            ? $"""
              The checks did NOT all pass: {run.Note} The developer asked for the draft PR anyway - say clearly in its
              description what failed. Last review:
              {run.Review}
              """
            : $"""
              The checks passed: `{run.Verify}` exited 0, and the reviewer passed the change. Review notes:
              {run.Review}
              """;
        string link = run.Source == FactorySource.Jira ? $" Link it to the Jira issue {run.Task} the way this repo does." : "";
        (string answer, string? error) = await TurnAsync(run, isNew: false, $"""
            {checks}

            Now commit all the changes on branch {run.Branch} with a clear message, push the branch to the remote, and
            open a DRAFT pull request into {run.BaseBranch} with the tools on this machine.{link} The description: what
            changed and why, how it was verified (the command and its result), and the review notes. Do not merge.
            Reply with the pull request URL on the last line.
            """, cancel, publish: true);
        if (error != null) return Wait(run, "The pull request turn failed: " + error);
        Match url = Regex.Matches(answer, @"https?://[^\s)>\]""'`]+").LastOrDefault() ?? Match.Empty;
        if (!url.Success) return Wait(run, "Claude did not give a pull request URL:\n" + answer);
        run.PrUrl = url.Value.TrimEnd('.', ',');
        Log(run, "Draft PR: " + run.PrUrl);
        End(run, FactoryStage.Done, anyway ? "Draft PR opened - with failed checks." : "Draft PR opened.");
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
            string? memoryDir = await CliSetup.MemoryDirAsync(run.Repo);
            string index = memoryDir != null && File.Exists(Path.Combine(memoryDir, "MEMORY.md")) ? File.ReadAllText(Path.Combine(memoryDir, "MEMORY.md")) : "";
            string log = string.Join("\n\n", run.Log.Select(s => $"[{s.Stage}{(s.Ok ? "" : ", FAILED")}] {Tail(s.Text, 1500)}"));
            string prompt = $"""
                A coding agent just worked on a task in the repo {run.Repo}. Below is the run: the task, its log (plan,
                build, verify output, fixes, review findings) and how it ended. Find what is worth keeping for the next
                agent working in this repo, so the same mistakes and detours do not happen again.

                THE TASK ({run.Source}): {run.Task}
                HOW IT ENDED: {ended} - {note}
                THE LOG:
                {Tail(log, 24000)}

                WHAT THE REPO'S MEMORY ALREADY SAYS (do not repeat it):
                {(index.Length > 0 ? index : "(nothing yet)")}

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
            (string answer, double cost) = await ClaudeJsonAsync(Path.GetTempPath(), prompt,
                ["--tools", "", "--strict-mcp-config", "--setting-sources", "", "--no-session-persistence"], CancellationToken.None);
            run.Cost += cost;
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
            Log(run, run.Lessons.Count == 0 ? "Nothing new to remember." :
                $"Learned {run.Lessons.Count}: {run.Lessons.Count(l => l.Status == "saved")} saved to memory, {run.Lessons.Count(l => l.Status == "pending")} rules wait for you.");
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
    public static void Undo(FactoryRun run, FactoryLesson lesson)
    {
        if (lesson.Status != "saved" || lesson.File == null) return;
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

    public static void Dismiss(FactoryRun run, FactoryLesson lesson)
    {
        lesson.Status = "dismissed";
        Save();
        Notify(run);
    }

    // --- running things ---

    // a turn of the run's own Claude Code session, in its worktree
    private static async Task<(string Answer, string? Error)> TurnAsync(FactoryRun run, bool isNew, string prompt, CancellationToken cancel, bool publish = false)
    {
        string answer = "";
        string? error = null;
        await ClaudeSessions.RunTurnAsync(run.Worktree, run.SessionId, isNew, prompt, entry =>
        {
            switch (entry.Kind)
            {
                case SessionEntryKind.Tool:
                    run.Steps++;
                    Notify(run);
                    break;
                case SessionEntryKind.Answer:
                    answer = entry.Text;
                    break;
                case SessionEntryKind.Done:
                    Match cost = Regex.Match(entry.Text, @"\$(\d+(\.\d+)?)");
                    if (cost.Success) run.Cost += double.Parse(cost.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SessionEntryKind.Error:
                    error = entry.Text;
                    break;
            }
        }, cancel, publish ? null : NoPublish);
        cancel.ThrowIfCancellationRequested();
        return (answer, error);
    }

    // a one-off claude -p with json output: its answer and what it cost
    private static async Task<(string Answer, double Cost)> ClaudeJsonAsync(string folder, string prompt, IEnumerable<string> args, CancellationToken cancel)
    {
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = folder, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-p", "--output-format", "json", "--no-session-persistence" }.Concat(args)) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start claude");
        using CancellationTokenRegistration stop = cancel.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        cancel.ThrowIfCancellationRequested();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            JsonElement root = doc.RootElement;
            double cost = root.TryGetProperty("total_cost_usd", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
            string text = root.TryGetProperty("result", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
            if (root.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True) throw new InvalidOperationException("claude: " + text);
            return (text, cost);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"claude exited with {process.ExitCode}: {(await stderr).Trim()} {Tail(output, 500)}");
        }
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
