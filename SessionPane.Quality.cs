using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// Is the finished work good? Three checks, none of which blocks a turn: "done" said with nothing run after the last
/// change (a coach card), an independent review on demand (a fresh claude with read-only tools and none of the writer's
/// reasoning), and a grade of the turn's ending against the rubric (a small model, after the turn, in the background).
/// </summary>
public partial class SessionPane
{
    // the review is suggested (never run on its own) for a turn this big
    private const int ReviewFiles = 3, ReviewLines = 150;
    private const int GradeFrom = 5;

    // the turn ended: did it check what it claims, and how does its ending grade
    private void CheckQuality(SessionState state)
    {
        if (state.Log is not { } log) return;
        if (Watch.Unverified(log, log.Answer) is { } nudge && !state.CoachDismissed.Contains("unverified"))
        {
            Raise(state, new CoachFlag("Claims done, not verified after the last change", Evidence(log), nudge, "unverified"));
        }
        if (SessionSettings.Of(state.Info.Id).Grade && !state.Grading && log.Steps.Count(s => !s.Sub) >= GradeFrom && log.Answer.Length > 0)
        {
            Logged(Grade(state, log), "grade");
        }
    }

    // the done line of a turn that changed files: "Review", and a chip suggesting it when the change is big
    private void AddReview(SessionState state, TurnLog log, Paragraph done)
    {
        if (log.Changed.Count == 0) return;
        bool big = log.Changed.Count > ReviewFiles || log.LinesChanged > ReviewLines;
        var review = new Hyperlink(new Run(big ? "⚑ review suggested" : "review"))
        {
            Foreground = (Brush)FindResource("VioletBrush"),
            ToolTip = $"A fresh, independent review of the {log.Changed.Count} changed file{(log.Changed.Count == 1 ? "" : "s")} - real defects only",
        };
        review.Click += (_, _) => Logged(Review(state, log), "review");
        done.Inlines.Add(new Run("   "));
        done.Inlines.Add(review);
    }

    // a reviewer with fresh eyes: a separate claude in the folder, read-only tools, the ask and the diff - no memory of
    // why the writer did what it did (the docs: a second session reviews better than the one that wrote the code)
    private async Task Review(SessionState state, TurnLog log)
    {
        StackPanel body = Card(state, log, "Review - reading the change...");
        string diff = await Task.Run(() => GitDiff(state.Info.Folder, log.Changed));
        string files = string.Join("\n", log.Changed.Select(f => "- " + f));
        string prompt = $"""
            You are an independent reviewer with fresh eyes. A coding agent just changed files for this request:
            {Shorten(SessionAnswers.WithoutShape(log.Message), 2000)}

            The files it changed:
            {files}
            {(diff.Length > 0 ? "\nThe change (git diff):\n" + diff : "\n(Not a git repository: read the files.)")}

            Read whatever you need. Report only real defects - correctness bugs, security problems, or the request not
            met - each as "path:line - the problem - why it matters", most severe first. No style remarks, no praise. If
            there are none, reply exactly: no defects found.
            """;
        string findings;
        try
        {
            findings = await ClaudeOnce.AskAsync(prompt, SessionSettings.Of(state.Info.Id).Model, state.Info.Folder,
                "Read,Grep,Glob", "Read,Grep,Glob", TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            findings = "The review could not run: " + ex.Message;
        }
        Log($"review ({state.Info.Id[..8]}): {findings.Replace('\n', ' ')[..Math.Min(200, findings.Length)]}");
        body.Children.Clear();
        body.Children.Add(CardTitle("Review"));
        var text = new FlowDocumentScrollViewer
        {
            Document = CodeRenderer.Render(findings), MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsToolBarVisible = false,
        };
        text.Document.FontSize = 13.5;
        body.Children.Add(text);
        bool clean = findings.Trim().StartsWith("no defects found", StringComparison.OrdinalIgnoreCase);
        var buttons = new List<Button>();
        if (!clean)
        {
            buttons.Add(CardButton("Send to Claude", true, () =>
            {
                SendFromCard(state, "An independent review of your change found:\n\n" + findings.Trim() + "\n\nFix what is real; say which findings you think are wrong and why.");
            }));
        }
        buttons.Add(CardButton("Dismiss", false, () => state.Document.Blocks.Remove(CardBlock(body))));
        body.Children.Add(Buttons([.. buttons]));
    }

    // the grade: a small model compares the ending's claims with the record, against the rubric
    private async Task Grade(SessionState state, TurnLog log)
    {
        state.Grading = true;
        List<(string Criterion, string Evidence)>? violations;
        try
        {
            string raw = await ClaudeOnce.AskAsync(Rubric.Prompt(log, log.Answer), "haiku", state.Info.Folder, "", "", TimeSpan.FromSeconds(120));
            violations = Rubric.Parse(raw);
            Log($"grade ({state.Info.Id[..8]}): {(violations == null ? "unreadable: " + raw.Replace('\n', ' ')[..Math.Min(150, raw.Length)] : violations.Count + " issue(s)")}");
        }
        catch (Exception ex)
        {
            Log("grade: could not run - " + ex.Message);
            violations = null;
        }
        finally
        {
            state.Grading = false;
        }
        if (!state.DoneLines.TryGetValue(log, out Block? done) || done.Parent == null) return;
        var line = new Paragraph { FontSize = 11.5, Margin = new Thickness(0, 0, 0, 8), Foreground = (Brush)FindResource("InkSoftBrush") };
        if (violations == null)
        {
            line.Inlines.Add(new Run("grade unavailable - the grader did not answer in shape"));
        }
        else if (violations.Count == 0)
        {
            line.Inlines.Add(new Run("✓ graded: clean against the rubric"));
        }
        else
        {
            var issues = new Hyperlink(new Run($"⚠ graded: {violations.Count} issue{(violations.Count == 1 ? "" : "s")}"))
            {
                Foreground = (Brush)FindResource("CoralInkBrush"), ToolTip = "Show what the grader found",
            };
            var list = new Paragraph { FontSize = 12.5, Margin = new Thickness(12, 0, 0, 8), Foreground = (Brush)FindResource("InkBrush") };
            foreach ((string criterion, string evidence) in violations)
            {
                list.Inlines.Add(new Bold(new Run(criterion + ": ")));
                list.Inlines.Add(new Run(evidence));
                list.Inlines.Add(new LineBreak());
            }
            issues.Click += (_, _) =>
            {
                if (list.Parent == null) state.Document.Blocks.InsertAfter(line, list);
                else state.Document.Blocks.Remove(list);
            };
            line.Inlines.Add(issues);
            foreach ((string criterion, string evidence) in violations)
            {
                int times = SessionLessons.CountViolation(state.Info.Folder, criterion);
                Log($"grade: '{criterion}' violated {times} time(s) in this folder - {evidence}");
                if (times >= 3) RepeatedViolation(state, criterion, evidence, times);
            }
        }
        state.Document.Blocks.InsertAfter(done, line);
    }

    // a card under a turn's done line (or at the end); its content panel
    private StackPanel Card(SessionState state, TurnLog log, string title)
    {
        var body = new StackPanel();
        body.Children.Add(CardTitle(title));
        var card = new Border
        {
            Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("VioletBrush"), BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 12), Child = body,
        };
        var block = new BlockUIContainer(card) { Margin = new Thickness(0, 4, 0, 10) };
        if (state.DoneLines.TryGetValue(log, out Block? done) && done.Parent != null) state.Document.Blocks.InsertAfter(done, block);
        else state.Document.Blocks.Add(block);
        return body;
    }

    private static Block CardBlock(StackPanel body) => (Block)((Border)body.Parent).Parent;

    // the change as git sees it (uncommitted changes of those files), capped; "" outside a repository
    private static string GitDiff(string folder, IEnumerable<string> files)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (string arg in new[] { "-C", folder, "diff", "--" }.Concat(files)) start.ArgumentList.Add(arg);
            using Process? git = Process.Start(start);
            if (git == null) return "";
            Task<string> output = git.StandardOutput.ReadToEndAsync();
            if (!git.WaitForExit(20_000))
            {
                git.Kill(entireProcessTree: true);
                return "";
            }
            string diff = output.Result;
            return git.ExitCode != 0 ? "" : diff.Length > 60_000 ? diff[..60_000] + "\n... (cut)" : diff;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }
}
