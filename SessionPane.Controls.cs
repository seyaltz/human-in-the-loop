using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// The live controls of a pane, as the terminal has them: the footer bar (permission mode, model, effort, context,
/// cost, background tasks) and the cards the CLI's questions become - a tool to allow, a plan to approve, Claude's own
/// questions. All of it goes to the session's open process (<see cref="SessionProcess"/>).
/// </summary>
public partial class SessionPane
{
    // the bar follows the session: its settings, how full its context is, what it cost, what runs in the background
    private void UpdateBar()
    {
        SessionState? state = _state;
        StatusBar.Visibility = state == null ? Visibility.Collapsed : Visibility.Visible;
        if (state == null) return;
        SessionSetting setting = SessionSettings.Of(state.Info.Id);
        ModeButton.Content = "⏵ " + SessionSettings.LabelOf(setting.Mode);
        bool risky = setting.Mode == SessionSettings.Bypass;
        ModeButton.Foreground = risky ? (Brush)FindResource("CoralInkBrush") : (Brush)FindResource("VioletDarkBrush");
        CliModel? model = SlashCommands.ModelsOf(state.Info.Folder).FirstOrDefault(m => m.Value == (setting.Model ?? "default"));
        ModelButton.Content = "Model: " + (setting.Model == null ? "default" : model?.Name ?? setting.Model);
        EffortButton.Content = "Effort: " + (setting.Effort ?? "auto");
        ContextButton.Visibility = state.ContextPercent >= 0 ? Visibility.Visible : Visibility.Collapsed;
        ContextButton.Content = $"{state.ContextPercent}% context";
        ContextButton.Foreground = state.ContextPercent >= 80 ? (Brush)FindResource("CoralInkBrush") : (Brush)FindResource("InkBrush");
        ContextButton.ToolTip = state.ContextDetail.Length > 0 ? state.ContextDetail : null;
        CostText.Text = state.Cost > 0 ? $"${state.Cost:0.00} this run" : "";
        TaskChips.Children.Clear();
        foreach ((string id, string what) in state.Tasks)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("BarButton"), Content = "⧗ " + Shorten(what, 40) + "  ×",
                ToolTip = "Running in the background - click to stop it (" + what + ")",
            };
            chip.Click += async (_, _) =>
            {
                try { await state.Process!.ControlAsync("stop_task", new() { ["task_id"] = id }); } catch (Exception) { }
            };
            TaskChips.Children.Add(chip);
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        SessionSetting setting = SessionSettings.Of(_state.Info.Id);
        var menu = new ContextMenu();
        foreach ((string mode, string label) in SessionSettings.Modes)
        {
            var item = new MenuItem { Header = label + "  -  " + ModeHint(mode), IsCheckable = true, IsChecked = setting.Mode == mode };
            item.Click += async (_, _) => await SetMode(mode);
            menu.Items.Add(item);
        }
        Open(menu, ModeButton);
    }

    private static string ModeHint(string mode) => mode switch
    {
        SessionSettings.Bypass => "runs everything without asking",
        "default" => "asks before tools that change things",
        "acceptEdits" => "edits files without asking, asks for the rest",
        "plan" => "plans only, changes nothing until you approve",
        _ => "",
    };

    private static void Open(ContextMenu menu, Button under)
    {
        menu.PlacementTarget = under;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>Shift+Tab: the next mode, in the CLI's order.</summary>
    private async Task CycleMode()
    {
        if (_state == null) return;
        string mode = SessionSettings.Of(_state.Info.Id).Mode;
        int at = Array.FindIndex(SessionSettings.Modes, m => m.Mode == mode);
        await SetMode(SessionSettings.Modes[(at + 1) % SessionSettings.Modes.Length].Mode);
    }

    // kept for the session; an open process switches at once (measured: set_permission_mode answers {"mode": ...})
    private async Task SetMode(string mode)
    {
        SessionState? state = _state;
        if (state == null) return;
        SessionSetting setting = SessionSettings.Of(state.Info.Id);
        if (mode == "plan" && setting.Mode != "plan") state.ModeBeforePlan = setting.Mode;
        setting.Mode = mode;
        SessionSettings.Save();
        UpdateBar();
        if (state.Process is { Usable: true } process)
        {
            try { await process.ControlAsync("set_permission_mode", new() { ["mode"] = mode }); }
            catch (Exception ex) { Add(state, new SessionEntry(SessionEntryKind.Error, "Could not switch the mode: " + ex.Message)); }
        }
    }

    private void ModelButton_Click(object sender, RoutedEventArgs e) => OpenModels();

    private void OpenModels()
    {
        if (_state == null) return;
        SessionState state = _state;
        SessionSetting setting = SessionSettings.Of(state.Info.Id);
        IReadOnlyList<CliModel> models = SlashCommands.ModelsOf(state.Info.Folder);
        var menu = new ContextMenu();
        if (models.Count == 0) menu.Items.Add(new MenuItem { Header = "Reading the CLI's models...", IsEnabled = false });
        foreach (CliModel model in models)
        {
            string? value = model.Value == "default" ? null : model.Value;
            var item = new MenuItem
            {
                Header = model.Name + (model.Description.Length > 0 ? "  -  " + model.Description : ""),
                IsCheckable = true, IsChecked = setting.Model == value,
            };
            item.Click += async (_, _) =>
            {
                setting.Model = value;
                SessionSettings.Save();
                UpdateBar();
                // an open process switches now (set_model; measured), so it is not restarted for it
                if (state.Process is { Usable: true } process)
                {
                    try
                    {
                        await process.ControlAsync("set_model", new() { ["model"] = model.Value });
                        process.Model = value;
                    }
                    catch (Exception ex) { Add(state, new SessionEntry(SessionEntryKind.Error, "Could not switch the model: " + ex.Message)); }
                }
            };
            menu.Items.Add(item);
        }
        Open(menu, ModelButton);
    }

    // effort has no live switch: the next message restarts the process with --effort (resuming the session)
    private void EffortButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        SessionSetting setting = SessionSettings.Of(_state.Info.Id);
        CliModel? model = SlashCommands.ModelsOf(_state.Info.Folder).FirstOrDefault(m => m.Value == (setting.Model ?? "default"));
        IEnumerable<string?> levels = new string?[] { null }.Concat(model?.Efforts ?? SessionSettings.Efforts.Skip(1));
        var menu = new ContextMenu();
        foreach (string? level in levels)
        {
            var item = new MenuItem { Header = level ?? "auto (the CLI's own)", IsCheckable = true, IsChecked = setting.Effort == level };
            item.Click += (_, _) =>
            {
                setting.Effort = level;
                SessionSettings.Save();
                UpdateBar();
            };
            menu.Items.Add(item);
        }
        Open(menu, EffortButton);
    }

    private void ContextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        var menu = new ContextMenu();
        foreach (string line in _state.ContextDetail.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            menu.Items.Add(new MenuItem { Header = line, IsEnabled = false });
        }
        var compact = new MenuItem { Header = "Compact the conversation (/compact) - add what to keep after it" };
        compact.Click += (_, _) => PutInBox("/compact ");
        menu.Items.Add(compact);
        Open(menu, ContextButton);
    }

    private void PutInBox(string text)
    {
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        InputBox.Focus();
    }

    // after a turn: how full the context is now (get_context_usage: totalTokens, maxTokens, percentage, categories)
    private static async Task RefreshContext(SessionState state)
    {
        if (state.Process is not { Usable: true } process) return;
        try
        {
            JsonElement usage = await process.ControlAsync("get_context_usage");
            state.ContextPercent = usage.TryGetProperty("percentage", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? (int)p.GetDouble() : -1;
            var lines = new List<string>();
            if (usage.TryGetProperty("totalTokens", out JsonElement t) && usage.TryGetProperty("maxTokens", out JsonElement m))
            {
                lines.Add($"{t.GetDouble() / 1000:0.#}k of {m.GetDouble() / 1000:0.#}k tokens");
            }
            if (usage.TryGetProperty("categories", out JsonElement cats) && cats.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in cats.EnumerateArray())
                {
                    if (c.TryGetProperty("name", out JsonElement n) && c.TryGetProperty("tokens", out JsonElement k) && k.GetDouble() > 0)
                    {
                        lines.Add($"{n.GetString()}: {k.GetDouble() / 1000:0.#}k");
                    }
                }
            }
            state.ContextDetail = string.Join("\n", lines);
            state.Notify();
        }
        catch (Exception)
        {
            // not shown this time
        }
    }

    // the questions and events the process brings between steps
    private void WireControls(SessionState state, SessionProcess process)
    {
        process.Ask += ask => Dispatcher.Invoke(() => ShowAsk(state, process, ask));
        process.SystemEvent += (subtype, line) => Dispatcher.Invoke(() =>
        {
            string id = line.TryGetProperty("task_id", out JsonElement t) ? t.GetString() ?? "" : "";
            if (subtype == "task_started" && id.Length > 0)
            {
                state.Tasks[id] = line.TryGetProperty("description", out JsonElement d) && d.GetString() is { Length: > 0 } what ? what : "a task";
                state.Notify();
            }
            else if (subtype is "task_notification" or "task_updated" && id.Length > 0
                     && line.TryGetProperty("status", out JsonElement s) && s.GetString() is "completed" or "failed" or "stopped" or "killed")
            {
                state.Tasks.Remove(id);
                state.Notify();
            }
        });
    }

    /// <summary>Dev break's own "/" commands, listed with the CLI's: /btw is the terminal's aside, which -p does not have.</summary>
    internal static readonly SlashCommand[] OwnCommands =
    [
        new("btw", "Ask a quick aside: answered outside the conversation, not kept in it - works while Claude works", "<question>", [], false),
    ];

    // /btw <question>: measured, -p answers "/btw isn't available in this environment", while the open process's
    // side_question answers outside the conversation - so it goes there, and the answer shows above the box
    private async Task Aside(SessionState state, string question)
    {
        AsideTitle.Text = "Aside: " + question;
        AsideText.Document = CodeRenderer.Render("_Asking..._");
        AsideBox.Visibility = Visibility.Visible;
        try
        {
            SessionProcess process = await ProcessFor(state);
            JsonElement reply = await process.ControlAsync("side_question", new() { ["question"] = question }, TimeSpan.FromMinutes(3));
            string answer = reply.TryGetProperty("response", out JsonElement r) ? r.GetString() ?? "" : "";
            if (_state == state) AsideText.Document = CodeRenderer.Render(answer.Length > 0 ? answer : "_No answer._");
        }
        catch (Exception ex)
        {
            if (_state == state) AsideText.Document = CodeRenderer.Render("Could not ask: " + ex.Message);
        }
        AsideText.Document.FontSize = 13.5;
        AsideText.Document.Foreground = (Brush)FindResource("InkBrush");
    }

    private void CloseAside_Click(object sender, RoutedEventArgs e) => AsideBox.Visibility = Visibility.Collapsed;

    // a turn ended: the questions it left are no longer asked
    private static void CloseCards(SessionState state, string why)
    {
        foreach ((PermissionAsk _, Action<string> close) in state.OpenCards.ToList()) close(why);
        state.OpenCards.Clear();
    }

    private void ShowAsk(SessionState state, SessionProcess process, PermissionAsk ask)
    {
        Brush Res(string key) => (Brush)FindResource(key);
        var body = new StackPanel();
        var card = new Border
        {
            Background = Res("VioletTintBrush"), BorderBrush = Res("VioletBrush"), BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 12), Child = body,
        };
        var block = new BlockUIContainer(card) { Margin = new Thickness(0, 6, 0, 8) };
        state.Document.Blocks.Add(block);
        state.ShownIn?.Transcript.ScrollToEnd();

        void Close(string line)
        {
            state.OpenCards.RemoveAll(c => c.Ask == ask);
            body.Children.Clear();
            card.Background = Res("PaperBrush");
            card.BorderBrush = Res("RuleBrush");
            card.Padding = new Thickness(12, 6, 12, 6);
            body.Children.Add(new TextBlock { Text = line, FontSize = 12.5, Foreground = Res("InkSoftBrush"), TextWrapping = TextWrapping.Wrap });
            state.LastNote = "";
            state.Notify();
        }

        void Reply(object response, string line)
        {
            if (!process.IsOpen(ask))
            {
                Close("No longer asked - the turn moved on.");
                return;
            }
            process.Answer(ask, response);
            Close(line);
        }

        state.OpenCards.Add((ask, Close));
        state.LastNote = "Waiting for you: " + (ask.Tool == "ExitPlanMode" ? "approve the plan" : ask.Tool == "AskUserQuestion" ? "Claude asks you" : "allow " + ask.Tool + "?");
        state.LastNoteAt = DateTime.Now;
        state.Notify();
        switch (ask.Tool)
        {
            case "ExitPlanMode":
                PlanCard(body, ask, Reply, state);
                break;
            case "AskUserQuestion":
                QuestionCard(body, ask, Reply);
                break;
            default:
                ToolCard(body, ask, Reply);
                break;
        }
    }

    private TextBlock CardTitle(string text) => new()
    {
        Text = text, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = (Brush)FindResource("VioletDarkBrush"),
        Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
    };

    private Button CardButton(string text, bool primary, Action click)
    {
        var button = new Button { Content = text, Style = (Style)FindResource(primary ? "PrimaryButton" : "CardButton") };
        if (primary)
        {
            button.Height = 32;
            button.Padding = new Thickness(14, 0, 14, 0);
            button.FontSize = 13;
            button.Margin = new Thickness(0, 0, 8, 0);
        }
        button.Click += (_, _) => click();
        return button;
    }

    // "Claude wants to run Bash: ping ..." - Allow / Always allow / Deny, as the terminal asks
    private void ToolCard(StackPanel body, PermissionAsk ask, Action<object, string> reply)
    {
        string detail = ClaudeSessions.ToolSummary(JsonSerializer.SerializeToElement(new { input = ask.Input }));
        body.Children.Add(CardTitle("Claude wants to use " + ask.Tool));
        // all of what would run, never the shortened step line: a long or many-line command must not hide its tail
        // behind Allow - a Bash command as it is, anything else as its whole input
        string full = ask.Tool == "Bash" && ask.Input.TryGetProperty("command", out JsonElement command) && command.ValueKind == JsonValueKind.String
            ? command.GetString()!
            : JsonSerializer.Serialize(ask.Input, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        body.Children.Add(new TextBox
        {
            Text = full, IsReadOnly = true, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 10),
            ToolTip = "Everything the tool would run with",
        });
        var buttons = new WrapPanel();
        string what = ask.Tool + (detail.Length > 0 ? " " + Shorten(detail, 80) : "");
        buttons.Children.Add(CardButton("Allow", true, () => reply(SessionProcess.Allow(ask.Input), "✓ Allowed " + what)));
        string always = Always(ask.Suggestions);
        if (always.Length > 0)
        {
            Button button = CardButton("Always allow", false, () => reply(SessionProcess.Allow(ask.Input, ask.Suggestions), "✓ Always allowed: " + always));
            button.ToolTip = always;
            buttons.Children.Add(button);
        }
        buttons.Children.Add(CardButton("Deny", false, () => reply(SessionProcess.Deny("The user denied this."), "✕ Denied " + what)));
        body.Children.Add(buttons);
    }

    // what "always allow" saves, from the CLI's own suggestions: addRules (a rule, where) or setMode (a mode for the session)
    private static string Always(JsonElement? suggestions)
    {
        if (suggestions is not { ValueKind: JsonValueKind.Array } list) return "";
        var parts = new List<string>();
        foreach (JsonElement s in list.EnumerateArray())
        {
            string type = s.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
            string where = s.TryGetProperty("destination", out JsonElement d) ? d.GetString() switch
            {
                "localSettings" => "this project's local settings",
                "projectSettings" => "this project's settings",
                "userSettings" => "your settings",
                "session" => "this session",
                var other => other ?? "",
            } : "";
            if (type == "addRules" && s.TryGetProperty("rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in rules.EnumerateArray())
                {
                    string tool = r.TryGetProperty("toolName", out JsonElement n) ? n.GetString() ?? "" : "";
                    string content = r.TryGetProperty("ruleContent", out JsonElement c) ? c.GetString() ?? "" : "";
                    parts.Add($"{tool}{(content.Length > 0 ? "(" + content + ")" : "")} in {where}");
                }
            }
            else if (type == "setMode" && s.TryGetProperty("mode", out JsonElement mode))
            {
                parts.Add($"{SessionSettings.LabelOf(mode.GetString() ?? "")} mode for {where}");
            }
            else if (type.Length > 0)
            {
                parts.Add(type + (where.Length > 0 ? " in " + where : ""));
            }
        }
        return string.Join("; ", parts);
    }

    // a plan to approve: approving runs it in the mode before Plan; "keep planning" sends what to change
    private void PlanCard(StackPanel body, PermissionAsk ask, Action<object, string> reply, SessionState state)
    {
        body.Children.Add(CardTitle("Plan ready - approve it to start"));
        string plan = ask.Input.TryGetProperty("plan", out JsonElement p) ? p.GetString() ?? "" : "";
        var viewer = new FlowDocumentScrollViewer
        {
            Document = CodeRenderer.Render(plan), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsToolBarVisible = false, Margin = new Thickness(0, 0, 0, 8),
        };
        viewer.Document.FontSize = 13.5;
        viewer.Document.Foreground = (Brush)FindResource("InkBrush");
        body.Children.Add(viewer);
        var change = new TextBox
        {
            Style = (Style)FindResource("NotebookBox"), MinHeight = 40, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            Margin = new Thickness(0, 0, 0, 8), ToolTip = "What to change in the plan (for Keep planning)",
        };
        body.Children.Add(change);
        string back = state.ModeBeforePlan is { } m && m != "plan" ? m : "default";
        var buttons = new WrapPanel();
        buttons.Children.Add(CardButton("Approve and run (" + SessionSettings.LabelOf(back) + ")", true, async () =>
        {
            reply(SessionProcess.Allow(ask.Input), "✓ Plan approved - running in " + SessionSettings.LabelOf(back) + " mode");
            await SetModeOf(state, back);
        }));
        buttons.Children.Add(CardButton("Keep planning", false, () =>
        {
            string what = change.Text.Trim();
            reply(SessionProcess.Deny(what.Length > 0 ? what : "Keep planning."), "↻ Keep planning" + (what.Length > 0 ? ": " + Shorten(what, 100) : ""));
        }));
        body.Children.Add(buttons);
    }

    // a mode change for a session that may not be the one on screen now (a card answered after switching)
    private async Task SetModeOf(SessionState state, string mode)
    {
        if (_state == state)
        {
            await SetMode(mode);
            return;
        }
        SessionSettings.Of(state.Info.Id).Mode = mode;
        SessionSettings.Save();
        if (state.Process is { Usable: true } process)
        {
            try { await process.ControlAsync("set_permission_mode", new() { ["mode"] = mode }); } catch (Exception) { }
        }
    }

    // Claude's questions (AskUserQuestion): options as buttons, more than one when it allows it, or your own answer;
    // the answers go back as answers: {question: label} (measured)
    private void QuestionCard(StackPanel body, PermissionAsk ask, Action<object, string> reply)
    {
        body.Children.Add(CardTitle("Claude asks you"));
        var picks = new Dictionary<string, HashSet<string>>();
        var others = new Dictionary<string, TextBox>();
        JsonElement questions = ask.Input.TryGetProperty("questions", out JsonElement q) ? q : default;
        if (questions.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement question in questions.EnumerateArray())
        {
            string text = question.TryGetProperty("question", out JsonElement t) ? t.GetString() ?? "" : "";
            bool multi = question.TryGetProperty("multiSelect", out JsonElement ms) && ms.ValueKind == JsonValueKind.True;
            picks[text] = [];
            body.Children.Add(new TextBlock
            {
                Text = text + (multi ? "  (pick any)" : ""), FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("InkBrush"), Margin = new Thickness(0, 4, 0, 6),
            });
            var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            var buttons = new List<(Button Button, string Label)>();
            if (question.TryGetProperty("options", out JsonElement opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement option in opts.EnumerateArray())
                {
                    string label = option.TryGetProperty("label", out JsonElement l) ? l.GetString() ?? "" : "";
                    string about = option.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? "" : "";
                    Button button = CardButton(label, false, () => { });
                    button.Margin = new Thickness(0, 0, 8, 6);
                    button.ToolTip = about.Length > 0 ? about : null;
                    button.Click += (_, _) =>
                    {
                        HashSet<string> chosen = picks[text];
                        if (!multi) chosen.Clear();
                        if (!chosen.Remove(label)) chosen.Add(label);
                        foreach ((Button b, string bl) in buttons) b.Background = chosen.Contains(bl) ? (Brush)FindResource("VioletRingBrush") : (Brush)FindResource("CardBrush");
                    };
                    buttons.Add((button, label));
                    options.Children.Add(button);
                }
            }
            body.Children.Add(options);
            var other = new TextBox
            {
                Style = (Style)FindResource("NotebookBox"), MinHeight = 34, Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "Or your own answer (used instead of the options)",
            };
            others[text] = other;
            body.Children.Add(other);
        }
        body.Children.Add(CardButton("Send answers", true, () =>
        {
            var answers = new Dictionary<string, string>();
            foreach ((string question, HashSet<string> chosen) in picks)
            {
                string own = others[question].Text.Trim();
                string answer = own.Length > 0 ? own : string.Join(", ", chosen);
                if (answer.Length > 0) answers[question] = answer;
            }
            Dictionary<string, JsonElement> input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ask.Input.GetRawText()) ?? [];
            input["answers"] = JsonSerializer.SerializeToElement(answers);
            reply(SessionProcess.Allow(JsonSerializer.SerializeToElement(input)),
                "✓ Answered: " + string.Join(" · ", answers.Select(a => Shorten(a.Value, 60))));
        }));
    }
}
