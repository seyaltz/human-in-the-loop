using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// Going back and branching off, as the terminal's /rewind (Esc Esc), --fork-session and /rename do: rewind a turn of
/// this run - its files (rewind_files, from the CLI's own checkpoints), its conversation (the session resumed before
/// it), or both; branch the session into a new pane; give it a title.
/// </summary>
public partial class SessionPane
{
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        SessionState state = _state;
        var menu = new ContextMenu();
        void Item(string header, Action click, bool enabled = true, string? tip = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        Item("Rewind...   Esc Esc", () => OpenRewind(null));
        bool saved = ClaudeSessions.FileOf(state.Info.Id) != null;
        Item("Branch from here - a copy of this session in a new pane", Branch, saved, saved ? null : "The session has no saved turn yet");
        Item("Rename...", () => AskRename(state));
        Item("Keys and tips   ?", () => ShowSheet(true));
        menu.PlacementTarget = MoreButton;
        menu.IsOpen = true;
    }

    // a fork of the session (--resume <id> --fork-session --session-id <new id>), opened in a new pane; this one goes on
    private void Branch()
    {
        if (_state == null) return;
        var info = new SessionInfo(Guid.NewGuid().ToString(), _state.Info.Folder, "Branch of " + _state.Info.Title, DateTime.Now);
        // the branch starts as this session is set
        SessionSetting mine = SessionSettings.Of(_state.Info.Id), branch = SessionSettings.Of(info.Id);
        (branch.Mode, branch.Model, branch.Effort) = (mine.Mode, mine.Model, mine.Effort);
        SessionSettings.Save();
        NewSessionRequested?.Invoke(this, info, _state.Info.Id, true);
    }

    private void ShowAction(string title, IEnumerable<UIElement> content)
    {
        ActionBody.Children.Clear();
        ActionBody.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = (Brush)FindResource("VioletDarkBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (UIElement element in content) ActionBody.Children.Add(element);
        ActionBox.Visibility = Visibility.Visible;
    }

    private void CloseAction() => ActionBox.Visibility = Visibility.Collapsed;

    private TextBlock Say(string text, bool soft = true) => new()
    {
        Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        Foreground = (Brush)FindResource(soft ? "InkSoftBrush" : "InkBrush"),
    };

    private WrapPanel Buttons(params Button[] buttons)
    {
        var panel = new WrapPanel();
        foreach (Button b in buttons) panel.Children.Add(b);
        return panel;
    }

    /// <summary>The rewind panel: with no turn, the turns of this run to pick from (newest first); with one, what to restore.</summary>
    private void OpenRewind(TurnMark? mark)
    {
        SessionState? state = _state;
        if (state == null) return;
        if (mark == null)
        {
            var list = new List<UIElement>();
            if (state.Marks.Count == 0)
            {
                list.Add(Say("Nothing to rewind from this run. Turns from before Dev break started, or made in a terminal, are rewound there with /rewind."));
            }
            foreach (TurnMark m in Enumerable.Reverse(state.Marks))
            {
                Button pick = CardButton("↶ " + Shorten(SessionAnswers.FirstLine(m.Message), 90), false, () => OpenRewind(m));
                pick.Margin = new Thickness(0, 0, 0, 6);
                pick.HorizontalContentAlignment = HorizontalAlignment.Left;
                list.Add(pick);
            }
            list.Add(Buttons(CardButton("Close", false, CloseAction)));
            ShowAction("Rewind - go back to before a message", list);
            return;
        }
        // where the conversation goes on from: the message's parent in the session file
        TurnMark target = mark with { Before = ClaudeSessions.ParentOf(state.Info.Id, mark.Uuid) ?? mark.Before };
        target.Line = mark.Line;
        mark = target;
        bool talk = mark.Before != null && !state.Working;
        string why = state.Working ? "Stop the running turn first." : mark.Before == null ? "This is the session's first message - start a new one with /clear instead." : "";
        Button both = CardButton("Code and conversation", true, () => _ = Rewind(state, mark, code: true, talk: true));
        Button talkOnly = CardButton("Conversation only", false, () => _ = Rewind(state, mark, code: false, talk: true));
        Button codeOnly = CardButton("Code only", false, () => _ = Rewind(state, mark, code: true, talk: false));
        both.IsEnabled = talkOnly.IsEnabled = talk;
        if (!talk) both.ToolTip = talkOnly.ToolTip = why;
        codeOnly.IsEnabled = !state.Working;
        ShowAction("Rewind to before: " + Shorten(SessionAnswers.FirstLine(mark.Message), 100),
        [
            Say("Code puts back the files Claude edited from this message on - not changes made by shell commands, nor most made by subagents. " +
                "Conversation goes on from before this message; the message comes back to the box." + (why.Length > 0 ? "  " + why : "")),
            Buttons(both, talkOnly, codeOnly, CardButton("Cancel", false, CloseAction)),
        ]);
    }

    private async Task Rewind(SessionState state, TurnMark mark, bool code, bool talk)
    {
        if (code)
        {
            // first what it would do (dry_run: filesChanged, insertions, deletions), then do it on a click
            ShowAction("Rewind - checking the files...", []);
            JsonElement plan;
            try
            {
                SessionProcess process = await ProcessFor(state);
                plan = await process.ControlAsync("rewind_files", new() { ["user_message_id"] = mark.Uuid, ["dry_run"] = true });
            }
            catch (Exception ex)
            {
                ShowAction("Rewind - could not check the files", [Say(ex.Message, false), Buttons(CardButton("Close", false, CloseAction))]);
                return;
            }
            bool can = plan.TryGetProperty("canRewind", out JsonElement c) && c.ValueKind == JsonValueKind.True;
            List<string> files = plan.TryGetProperty("filesChanged", out JsonElement f) && f.ValueKind == JsonValueKind.Array
                ? [.. f.EnumerateArray().Select(x => System.IO.Path.GetRelativePath(state.Info.Folder, x.GetString() ?? ""))]
                : [];
            if (!can)
            {
                string error = plan.TryGetProperty("error", out JsonElement er) ? er.GetString() ?? "" : "";
                ShowAction("Rewind - the code cannot be restored for this message",
                    [Say(error.Length > 0 ? error : "The CLI has no checkpoint for it.", false), Buttons(CardButton("Close", false, CloseAction))]);
                return;
            }
            int plus = plan.TryGetProperty("insertions", out JsonElement i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
            int minus = plan.TryGetProperty("deletions", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
            var confirm = new TaskCompletionSource<bool>();
            ShowAction(files.Count == 0 ? "No file changed since this message" : $"Restore {files.Count} file{(files.Count == 1 ? "" : "s")}  (+{plus} −{minus})",
            [
                Say(files.Count == 0 ? "Nothing to put back." : string.Join("\n", files.Take(20)) + (files.Count > 20 ? $"\n… and {files.Count - 20} more" : ""), false),
                Buttons(CardButton(talk ? "Restore and rewind the conversation" : "Restore", true, () => confirm.TrySetResult(true)),
                    CardButton("Cancel", false, () => confirm.TrySetResult(false))),
            ]);
            if (!await confirm.Task)
            {
                CloseAction();
                return;
            }
            if (files.Count > 0)
            {
                try
                {
                    await state.Process!.ControlAsync("rewind_files", new() { ["user_message_id"] = mark.Uuid });
                }
                catch (Exception ex)
                {
                    ShowAction("Rewind - the files were not restored", [Say(ex.Message, false), Buttons(CardButton("Close", false, CloseAction))]);
                    return;
                }
            }
        }
        if (talk)
        {
            // the session goes on from the line before the message: a new process resumes it there; the cut is kept so
            // the cut turns stay out when the session is read back
            SessionSetting setting = SessionSettings.Of(state.Info.Id);
            setting.Rewinds.Add(new RewindCut(mark.Uuid, mark.Before));
            SessionSettings.Save();
            state.Process?.Dispose();
            state.Process = null;
            state.ResumeAt = mark.Before;
            state.FileStamp = null;
            Block? first = mark.Start.Parent == state.Document ? mark.Start : mark.Line?.Parent == state.Document ? mark.Line : null;
            if (first != null)
            {
                for (Block? b = first; b != null;)
                {
                    Block? next = b.NextBlock;
                    state.Document.Blocks.Remove(b);
                    b = next;
                }
            }
            int at = state.Marks.FindIndex(m => m.Uuid == mark.Uuid);
            if (at >= 0) state.Marks.RemoveRange(at, state.Marks.Count - at);
            state.TurnStart = state.ReplyStart = null;
            state.Group = null;
            state.TurnQuestion = state.TurnResult = state.TurnDone = "";
            if (_state == state)
            {
                (string text, List<string> attached) = AttachedFiles.Split(SessionAnswers.WithoutShape(ShellCommand.Split(mark.Message).Said));
                SetText(text);
                foreach (string file in attached) Attach(file);
                InputBox.Focus();
            }
        }
        state.Document.Blocks.Add(new Paragraph(new Run("↶ Rewound" + (code ? " the code" : "") + (code && talk ? " and" : "") + (talk ? " the conversation" : "")
            + " to before: " + Shorten(SessionAnswers.FirstLine(mark.Message), 80)))
        {
            FontSize = 12, Foreground = (Brush)FindResource("VioletDarkBrush"), Margin = new Thickness(0, 4, 0, 8),
        });
        CloseAction();
        state.Notify();
    }

    private void AskRename(SessionState state)
    {
        var box = new TextBox { Style = (Style)FindResource("NotebookBox"), Text = state.Info.Title, MinHeight = 36, Margin = new Thickness(0, 0, 0, 8) };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) _ = Rename(state, box.Text.Trim());
        };
        ShowAction("Rename this session", [Say("Shown in the list, and \"claude --resume <name>\" finds it in a terminal."), box,
            Buttons(CardButton("Rename", true, () => _ = Rename(state, box.Text.Trim())), CardButton("Cancel", false, CloseAction))]);
        box.Focus();
        box.SelectAll();
    }

    // the title shown here (kept in session-titles.json) and the session's own (rename_session, as a host rename)
    private async Task Rename(SessionState state, string title)
    {
        if (title.Length == 0) return;
        CloseAction();
        SessionTitles.Set(state.Info.Id, title);
        state.Info = state.Info with { Title = title };
        Renamed?.Invoke(state);
        state.Notify();
        try
        {
            SessionProcess process = await ProcessFor(state);
            await process.ControlAsync("rename_session", new() { ["title"] = title, ["source"] = "host", ["session_id"] = state.Info.Id });
        }
        catch (Exception ex)
        {
            Add(state, new SessionEntry(SessionEntryKind.Error, "Renamed here, but the CLI did not take the name: " + ex.Message));
        }
    }
}
