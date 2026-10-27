# Sessions: CLI capabilities - Implementation Plan

> For agentic workers: execute task by task (superpowers:executing-plans); steps use `- [ ]`.

**Goal:** a Sessions pane does what the terminal CLI does - its keys, prompt syntax, live controls, rewind and tips.

**Architecture:** a new kept-open runner per session (`SessionProcess`) speaks the stream-json control protocol
(measured below); the pane's input logic moves to partial-class files by concern; transcript reading learns command
and shell turns. The Factory's `LiveSession` is untouched.

**Tech stack:** C# / WPF / .NET 10 (net10.0-windows), System.Text.Json. No new packages.

**Spec:** `docs/specs/2026-10-27-cli-capabilities-design.md` (approved 2026-10-27).

## Global constraints

- Repo is public and generic: no user paths, names, companies or data; grep the diff before each commit.
- Local only: commit on `feature/slash-commands`, no push / PR.
- PROMPT.md is the app's full spec: every task updates its Sessions section (and Verify).
- Comments say why and name measured facts, as the existing files do.
- Tests drive a throwaway copy of the exe (`scratchpad\devbreak-build`) against a session in a throwaway folder;
  never press Enter on anything in one of the user's real sessions. Proof = window screenshots + stream logs.
- No unit-test project exists; verification is build (0 warnings, 0 errors) + the scripted UI run + protocol logs.

## Measured protocol (CLI 2.1.311) - the plan relies on exactly these

Process: `claude -p --input-format stream-json --output-format stream-json --verbose --permission-prompt-tool stdio
--replay-user-messages --permission-mode <mode> [--model m] [--effort e] (--session-id id | --resume id
[--resume-session-at uuid] [--fork-session])`, env `CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING=1`. First write
`{"type":"control_request","request_id":..,"request":{"subtype":"initialize"}}`.

| Need | Message | Reply / event |
|---|---|---|
| user message | `{"type":"user","message":{"role":"user","content":[{"type":"text","text":..}]}}` | replayed `type:user` with `uuid`, `isReplay:true` |
| message while a turn runs | same | folded into the running turn (one result) |
| permission ask | CLI sends `control_request` `can_use_tool` {tool_name, input, permission_suggestions?} | we send `control_response` {subtype:success, request_id, response:{behavior:"allow", updatedInput, updatedPermissions?} or {behavior:"deny", message}} |
| plan approval | `can_use_tool` tool_name `ExitPlanMode`, input.plan | allow = run; deny+message = keep planning |
| Claude asks | `can_use_tool` tool_name `AskUserQuestion`, input.questions[] | allow with updatedInput = input + `answers:{question: label}` |
| stop | `interrupt` | result `error_during_execution`, process stays |
| mode | `set_permission_mode` {mode} | {mode} |
| model | `set_model` {model} | {model} |
| context | `get_context_usage` | {totalTokens, maxTokens, percentage, categories[]} |
| aside | `side_question` {question} | {response} (undocumented, measured) |
| rename | `rename_session` {title, source:"host"} | success + `session_title_changed` |
| rewind code | `rewind_files` {user_message_id, dry_run?} | {canRewind, filesChanged[], insertions, deletions} |
| rewind talk | restart with `--resume id --resume-session-at <last uuid before that message>` | later history gone |
| tasks | events `system/task_started`, `task_notification`; `stop_task` {task_id} | |
| commands | `initialize` reply `commands`, event `system/commands_changed` | |

## Review focus

1. A permission card left unanswered when the user presses Stop or closes the pane - the request must be answered
   (deny) so the process does not hang. Test in Task 3.
2. The session used in a terminal meanwhile - next message must restart the process (file size/time changed). Task 2.
3. A process that dies mid-turn (killed) - the pane shows the error and the next message starts a new one. Task 2.
4. `!` command that never ends - Stop kills it; output capped at 4,000 chars. Task 5.
5. Rewind on a turn without checkpoint data (`canRewind:false`) - shown as not available, nothing changed. Task 7.

---

### Task 1: kept-open runner `SessionProcess`

**Files:** Create `SessionProcess.cs`.

**Produces:**
- `sealed class SessionProcess : IDisposable` - `static SessionProcess Start(SessionLaunch launch)`;
  `record SessionLaunch(string Folder, string Id, bool IsNew, string Mode, string? Model, string? Effort, string? ResumeAt, bool Fork)`.
- Events (raised on the reader thread): `Action<SessionEntry> Entry`, `Action<string> UserMessageId` (replayed
  uuid), `Action<string> LastUuid` (every uuid seen), `Action<JsonElement> Result`, `Action<PermissionAsk> Ask`,
  `Action<string, JsonElement> SystemEvent` (subtype, line), `Action<string> Exited` (stderr tail).
- `record PermissionAsk(string RequestId, string Tool, JsonElement Input, JsonElement? Suggestions)`;
  `void Answer(PermissionAsk ask, object response)`.
- `Task SendAsync(string text)`; `Task<JsonElement> ControlAsync(string subtype, object? fields = null, TimeSpan? timeout = null)`.
- `bool Usable`; `DateTime LastActivity`; `void Dispose()` (kill tree, deny every open ask first).
- Reader keeps running between turns; entries parsed with the existing `ClaudeSessions` block logic
  (make `AddBlock` / result parsing `internal` and reuse).

- [ ] Write it; build: 0 warnings / 0 errors.
- [ ] Probe from a scratch console harness is not needed: Task 2 exercises it in the app.

### Task 2: Sessions turn through `SessionProcess`

**Files:** Modify `SessionPane.xaml.cs` (Send, Stop), `SessionState.cs` (add `Process`, `TurnUserIds` list of
(turn index, uuid), `LastUuid`, `FileStamp` (length, write time), `Cost`).

- Send: get or start the state's process (start when null / not usable / `FileStamp` differs from the session file
  now / launch settings changed); `SendAsync`; the turn ends on `Result` (Answer + Done entries as today).
- Idle close: a 1-minute timer in `SessionsView` disposes processes idle > 10 min and not working.
- Stop: `ControlAsync("interrupt")`; if it throws or no result in 10 s, kill (today's behaviour).
- After each result: store `FileStamp`; add `total_cost_usd` to `Cost`.
- [ ] Build. Run the test copy on a throwaway folder session: send "say A", "say B": second turn < 5 s (proof: done
  line seconds). Stop during `ping -n 40` → "Stopped." and a next "say C" answers without a new process (same pid).
- [ ] Commit "Sessions: one kept-open CLI process per session, interrupt".

### Task 3: permission, plan and question cards; mode pill

**Files:** Create `SessionCards.cs` (card builders); Modify `SessionPane.xaml(.cs)` (footer bar), `SessionSettings.cs`
(new: per-session Mode/Model/Effort in `session-settings.json`, git-ignored; defaults Bypass / null / null),
`.gitignore`.

- Footer bar (one line under the caption): `Mode` pill · `Model` · `Effort` · `Context` · `Cost`.
- Mode values and labels: `bypassPermissions` "Bypass", `default` "Ask", `acceptEdits` "Accept edits", `plan` "Plan".
  Shift+Tab in the box cycles in that order; live process gets `set_permission_mode`; saved.
- `Ask` card in the transcript: tool name, `ClaudeSessions.ToolSummary(input)`, buttons "Allow", "Always allow" (sends
  `updatedPermissions = permission_suggestions`), "Deny". Answered card collapses to "✓ allowed Bash ping ..." /
  "✕ denied".
- `ExitPlanMode` card: the plan rendered (CodeRenderer), "Approve and run" (allow; then mode := previous non-plan mode,
  `set_permission_mode`), "Keep planning" + a text box (deny with that text).
- `AskUserQuestion` card: each question with its options as buttons (multiSelect: toggles + "Send"), an "Other" text
  box; allow with `answers`.
- Stop / pane close / process exit: every open card answered deny and greyed.
- [ ] Build; throwaway session in Ask: "create a.txt with HI" → card, Allow → file exists. Plan: card → Approve → runs.
  AskUserQuestion: "ask me Red or Blue" → card → Blue → answer "Blue". Stop while a card is open → card greyed,
  process alive. Screenshots.
- [ ] Commit.

### Task 4: model, effort, context, cost, /btw, background tasks

**Files:** Modify `SessionPane.*`, `SessionSettings.cs`.

- Model pill: menu from the `initialize` reply `models` (name + description) + "Default"; `set_model`; saved. Alt+P
  opens it.
- Effort pill: low / medium / high / xhigh / auto; saved; differs from the process's → the next message restarts it
  with `--effort` (auto = no flag).
- Context: after each result `get_context_usage` → "N% context"; tooltip = categories with tokens; ≥ 80% coral +
  "/compact" button that puts "/compact " in the box.
- Cost: "$x.xx this run".
- "/btw <q>" typed (also listed in the "/" menu as source "dev break"): `side_question`; answer in a dismissible box
  above the input; works while a turn runs.
- Tasks: `task_started` adds a chip "⧗ <description>" with ×  → `stop_task`; `task_notification` removes it.
- [ ] Build; throwaway session: switch model to haiku → next answer's init model haiku (stream log); context shows a
  %; /btw answers while idle; screenshots. Commit.

### Task 5: prompt syntax - `@`, `!`, history, Esc Esc, Ctrl+J, ultrathink, `?`

**Files:** Create `FileMentions.cs` (`static IReadOnlyList<string> List(string folder)`: `git ls-files` when
`.git` exists, else a walk skipping `bin obj node_modules .git .vs`, max 5,000), `ShellCommand.cs`
(`static Task<(int Code, string Output)> RunAsync(string folder, string command, CancellationToken)`: Git Bash
`bash.exe -lc` if found under Program Files\Git, else `powershell -NoProfile -Command`; output last 4,000 chars).
Modify `SessionPane.*` - generalise the "/" menu into one completion box with a source (`/`, `@`, Ctrl+R).

- `@`: word starting with `@` at the caret → file list narrowed (path contains, filename-prefix first); pick inserts
  `@rel/path `.
- `!`: message starting with `!` → not sent; shell block in transcript (mono, "$ cmd", output, exit code); kept in
  `state.PendingShell`; the next message is prefixed "[I ran: <cmd>]\n<output>\n[/I ran]\n\n". A "shell" tag shows
  left of the box while the text starts with `!`. Stop cancels it.
- ↑/↓ on first/last line: walks `state` You texts (newest first), draft restored after the newest; Ctrl+R: completion
  box over earlier messages (contains).
- Esc Esc (within 600 ms): text → clear; empty → rewind list (Task 7). Ctrl+J: new line.
- `ultrathink` in the text: hint line "ultrathink - Claude thinks harder this turn".
- `?` on empty box or the "?" caption link: the sheet (Task 8).
- [ ] Build; UI run: `@Ses` lists SessionPane files; `!echo hi` shows block with "hi" and exit 0 and the next sent
  message carries it (stream log); ↑ recalls; Esc Esc clears. Commit.

### Task 6: transcript read-back of command and shell turns

**Files:** Modify `ClaudeSessions.cs` (`Transcript`), `SessionPane.xaml.cs` (`Add`).

- A user line whose content holds `<command-name>/x</command-name>` (+ `<command-args>`) → You "/x args"; a
  following `<local-command-stdout>...</local-command-stdout>` → Answer with that text; `<local-command-caveat>` lines
  skipped.
- A You text starting with "[I ran: " → shell block(s) + the rest as the message (strict parse like files).
- [ ] Build; open a session that has a /command turn (throwaway one made with `/context`) → "/context" + its table.
  Commit.

### Task 7: rewind, branch, rename, /clear

**Files:** Modify `SessionPane.*`, `SessionsView.xaml.cs` (open a session in a new pane), `SessionTitles.cs`
(`Set(id, title)`).

- Each folded turn line and the open turn get "↶ Rewind" (only turns with a recorded user uuid from this app run;
  others show it disabled with tooltip "made before this run - rewind it in the terminal").
- Rewind dialog (in-pane card): "Restore code and conversation" / "Conversation only" / "Code only" / Cancel; code
  options do `rewind_files` dry run first and list files (+N −M), then apply; conversation options restart the
  process with `--resume-session-at <LastUuid before that turn>` (first turn: start a fresh session in the folder),
  remove that turn and later ones from the document, put the turn's message back in the box (as the CLI does).
- Header "⋯" menu: "Branch from here" (new id via `--resume id --fork-session` → the new session's id is read from the
  init line; opens in a new pane), "Rename" (title becomes a text box; Enter → `rename_session` + `SessionTitles.Set`).
- "/clear" typed → new session in the same folder in this pane (old one stays in the list). "/rename x" typed →
  same as Rename.
- [ ] Build; throwaway: write a.txt turn → Rewind code only → file gone; conversation only → "did I ask…" answers no;
  Branch → new pane with history; Rename → list shows the new title. Commit.

### Task 8: the "?" sheet, PROMPT.md, final verify

**Files:** Create `ShortcutSheet.cs`; Modify `SessionPane.xaml` (overlay), caption text, `PROMPT.md`.

- Keys (two columns): every key from Tasks 2-7 plus Shift+Enter, Ctrl+V, drop, "/", "@", "!", "?".
- Tips (each: line, optional action button, docs link opened in the browser): Plan before editing (→ Plan mode) ·
  Give Claude a check to run · /compact <focus> when context fills (→ puts "/compact ") · /clear between tasks ·
  Esc early when it drifts; after two corrections, /clear and say it better · @file instead of "read the file" ·
  Paste a screenshot of the error · Use subagents for research · A second session as reviewer (→ + Pane) · Rename and
  resume · Branch to try another way (→ Branch) · ultrathink for hard problems · /btw for a quick aside. Links:
  code.claude.com/docs/en/best-practices, /common-workflows, /interactive-mode, /checkpointing, /permission-modes.
- Caption: "... / for commands and skills. ? for keys and tips. ..."
- [ ] PROMPT.md: Sessions section rewritten for the runner, bar, cards, syntax, rewind, sheet; Verify updated.
- [ ] Full UI run of every Task check on one fresh test copy; clean-clone build 0/0; diff grep; commit.
