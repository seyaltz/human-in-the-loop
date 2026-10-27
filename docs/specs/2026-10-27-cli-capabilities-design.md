# Sessions: everything the Claude Code CLI can do, in Dev break

Status: draft for approval · 2026-10-27

## Why

The Sessions tab is "your Claude Code, here". Today it sends a message and shows the answer, and since the last change
it has the CLI's "/" menu. The user wants the rest of the CLI there too: its keys, its hints, its modes and the tricks
the Claude Code docs teach. Then a Sessions pane is a full replacement for the terminal, not a reduced one.

Success: anything the user does in the terminal CLI on a normal day, they can do in a pane, the way the CLI does it
(same key, same word), and the pane tells them it exists (hints, a "?" sheet), like the CLI does.

## Goals

1. The CLI's keys and prompt syntax in the message box.
2. The CLI's live controls - permission mode and plan mode, model, effort, stop without losing the session, queued
   messages, side questions, context usage - in the pane.
3. Undo: rewind the files a turn changed.
4. The docs' recommended habits (plan before editing, /compact with a focus, /clear between tasks, rename and resume,
   fork, screenshots, @file) one key or one click away, and taught in the "?" sheet.

## Non-goals

- Terminal-only features with no meaning in a window: vim mode, voice dictation, Ctrl+G external editor, spellcheck,
  the status-line command, terminal colours/themes, `/doctor`, `/color`, `/focus`, `/reload-plugins`.
- Ctrl+O (transcript view) and Ctrl+T (to-do list): the pane already shows every step in its fold.
- Changing the Factory tab. It keeps its own runner.
- Anything that sends data anywhere new. Everything stays the user's own CLI on this machine.

## Anti-goals (a failure even if it works)

- A turn that does something different from what the same words do in the terminal.
- Losing a session's process or its history because of a UI action (Esc must stop, not kill).
- Personal data in the repo (the repo is public and generic).
- A test that runs a command in one of the user's real sessions (it happened once with `/model`; tests use a throwaway
  folder's session).

## What was measured (this design rests on these)

CLI 2.1.311, from Dev break's folder and a throwaway folder:

| Fact | How it was checked | Result |
|---|---|---|
| The command list with descriptions/hints comes from the `initialize` reply | ran it, read the reply | 247 commands, 1.8 s, no model call, no session file |
| A "/command" in `-p` runs as in the terminal | `/context` piped to `claude -p` | worked, $0 |
| Text after "/name" becomes its arguments | session file | `<command-args>` held the shape note, so "/" messages go without it |
| `@file` in `-p` | "secret word in @note.txt", no tools allowed | answered from the file: works natively |
| `!command` in `-p` | `!echo BANGOUT-7` | NOT native: Claude ran Bash itself (a model turn). Dev break must run it |
| `/btw` in `-p` | piped | "isn't available in this environment" |
| `side_question` control request (kept-open process) | sent it | answered "2+2 = 4." outside the history: this is /btw |
| `set_permission_mode` control request | sent `plan` | `{"mode":"plan"}` |
| `get_context_usage` control request | sent it | categories with token counts |
| `file_suggestions` control request | query "no" in a non-git folder | empty list - not usable as is; Dev break lists files itself |
| A kept-open process is faster | measured earlier (Factory) | first turn 11.7-13.7 s, later turns 2.9-3.0 s |

Documented SDK controls (code.claude.com/docs/en/agent-sdk/typescript): interrupt, setPermissionMode, setModel,
rewindFiles (with file checkpointing on), streamInput, backgroundTasks, stopTask, getContextUsage, supportedCommands,
supportedModels, mcpServerStatus, toggleMcpServer, reloadSkills. Undocumented but present: side_question. Each control
is measured once more before it is built on; one that does not answer as expected is left out and reported.

## The design

### Part 1 - the message box (no change to how turns run)

| In the CLI | In a pane |
|---|---|
| `/` menu | done (commit 22dbcce) |
| `@` + path, autocomplete | typing `@` opens the same kind of list with the folder's files and folders (git-tracked files when the folder is a git repo, else a walk that skips bin/obj/node_modules/.git, capped), narrowed as you type; Tab/Enter inserts `@relative/path`. Sent as typed: the CLI reads it natively |
| `! command` (bash mode) | a message starting with `!` is not sent to Claude: Dev break runs it in the folder (the user's shell: Git Bash if found, else PowerShell), shows the command and its output in the transcript as a "shell" block, and puts it in front of the next message as "[I ran: <cmd>\n<output, last 4,000 chars>]", as the CLI puts it into context. The box shows a "shell" tag while the text starts with `!` |
| ↑ / ↓ | on an empty box (or with the caret on the first/last line) walks the session's earlier messages; the draft comes back after the newest |
| Ctrl+R | a search over the session's earlier messages (a small list, like "/") |
| Esc Esc | empty box: open the rewind list (Part 3); with text: clear the draft |
| Shift+Enter, Ctrl+J | new line (Ctrl+J added) |
| Ctrl+V image / drag-drop | done (files) |
| `?` on an empty box | the shortcut and tips sheet (Part 4) |
| `ultrathink` | sent as typed (the CLI raises that turn's effort); the word gets a violet highlight hint "thinks harder this turn" |
| A `/command` turn read back from a saved session | shown as "/name args" in the You box and its `<local-command-stdout>` as the answer, not raw tags |

### Part 2 - the kept-open process and the live controls

Sessions moves from one `claude -p` per turn to one kept-open process per session (`ClaudeSessions.LiveSession`, as
the Factory uses): `--input-format stream-json`, the session resumed once, every message written to it. A failed or
killed process is replaced on the next message by one that resumes the session (as today).

A bar under the message box (one line, small, like the CLI's footer), each item clickable:

- **Mode** pill: Bypass (today's behaviour, the default) · Ask · Accept edits · Plan. Shift+Tab in the box cycles them.
  Sent with `set_permission_mode`; shown on the pill; kept per session.
  - **Ask** needs Dev break to answer the CLI's permission questions: started with `--permission-prompt-tool stdio`,
    each `can_use_tool` request shows a card in the transcript - the tool, its command/file, Allow once / Always allow
    (this session) / Deny - and the turn waits. That is how the CLI asks in the terminal.
  - **Plan**: Claude plans without editing; its plan (the ExitPlanMode tool) shows as a card with "Approve and run"
    (switches to the previous mode and continues) and "Keep planning".
- **Model** (Alt+P): the models from the `initialize` reply's `models`; `set_model`.
- **Effort**: low / medium / high / xhigh / auto, sent as `/effort <level>` (a CLI command that works in a turn).
- **Context**: "41% context" from `get_context_usage` after each turn; a click shows the categories; at 80% it turns
  coral and suggests "/compact".
- **Cost**: the session's total so far (sum of the turns' `total_cost_usd`).

While a turn runs:
- **Esc** (or Stop) sends `interrupt`: the turn stops, the process and the session stay. (Today Stop kills the process.)
- **Enter queues** the message: it shows as "queued" under the live box and is written when the turn ends; ↑ takes it
  back into the box. Ctrl+Enter sends it now (written immediately - the CLI takes it into the running turn).
- **Side question**: "/btw <question>" (also in the "/" menu, marked as Dev break's) asks with `side_question` and shows
  the answer in a dismissible box above the input - not in the transcript, not in the history.
- Background tasks: a "⧗ N running" chip from `backgroundTasks` events, with Stop per task (`stopTask`).

### Part 3 - undo, branch, rename

- **Rewind**: the process starts with file checkpointing on. Each finished turn's line gets "↶ Rewind"; Esc Esc on an
  empty box lists the turns. Choosing one offers, as the CLI does: restore code and conversation · conversation only ·
  code only. Code: `rewindFiles(<that turn's user message id>)` with a dry run first showing the files it would
  restore. Conversation: the session continues from before that message (later turns fold away as "rewound"). The
  docs' limits are shown with it: shell-made changes and most subagent edits are not restored.
- **Branch from here**: on a pane's header ⋯, starts a new session with `--resume <id> --fork-session`; it opens in a
  new pane.
- **Rename**: clicking the title edits it; saved with `/rename <title>` (a CLI command), so `claude --resume <name>`
  works in a terminal.
- **/clear** in a pane starts a fresh session in the same folder in that pane (the old one stays in the list), as the
  CLI does.

### Part 4 - the "?" sheet: keys and tips

`?` on an empty box, or a "?" button by the caption, opens a sheet over the pane:

- **Keys**: every key above, two columns, as the CLI's `?` lists them.
- **Tips from the docs**, each a line with the action as a button where there is one, and its docs link:
  plan before editing (Plan mode) · give Claude a check to run · /compact <focus> when the context fills · /clear
  between tasks · Esc early when it drifts; after two corrections, /clear and say it better · @file instead of "read
  the file" · paste a screenshot of the error · subagents for research · a second session as reviewer (+ Pane) ·
  rename and resume · branch to try another way · ultrathink for hard problems · /btw for a quick aside.
- The caption under the box keeps its one line and adds "? for keys and tips".

## Constraints

- C# WPF, .NET 10, the existing files and style (comments that say why, measured facts named in comments, PROMPT.md
  kept in step with every change - it is the app's full spec).
- Only documented CLI interfaces, except `side_question` (measured working) - marked as such in the code.
- Repo generic: no paths, names or data of the user; diff grepped before each commit.
- Develop locally: commit on a local branch, no push or PR until asked.
- Tests on a throwaway copy of the exe and a throwaway folder's session; window screenshots as proof.

## Decided by the user

- "/" menu like the CLI, with descriptions, keys and hints (done).
- Bring all of the CLI's capabilities, hotkeys and tips into Dev break (this document).
- Bypass stays possible (it is today's behaviour, the user's earlier choice).
- **Kept-open process lifetime**: a session's process closes after 10 minutes idle, and is restarted (resuming the
  session) before the next message if the session file changed from outside since the process last wrote it - e.g.
  the session was used in a terminal. Avoids two writers splitting the history.
- **Undo offers what the CLI's rewind offers**: restore code, conversation, or both. Conversation rewind resumes the
  session at that message (the SDK's `resumeSessionAt`, measured before it is built on). Turns made before
  checkpointing was on (or in a terminal) show code restore as not available.

## Calls made in the design (not the user's - check them)

- **Ask mode follows the user's own rules**: the CLI asks Dev break only what its allow/deny rules and mode don't
  settle (that is how `can_use_tool` works). "Always allow" sends back the CLI's own suggested rule, so the CLI saves it
  where the terminal would (the project's `.claude/settings.local.json`) - same as the terminal, Dev break writes no
  settings itself.
- **Sessions get their own kept-open runner** (a new class next to `LiveSession`); the Factory's runner is untouched.
  It keeps reading between turns (for interrupt, permission requests, background-task events).
- **If a control does not answer as expected when re-measured**, that feature is left out and today's behaviour stays
  (e.g. Stop keeps killing the process if `interrupt` fails). Reported, not worked around.
- **Mode, model and effort are kept per session** in `session-settings.json` next to the app (user data, git-ignored,
  like `session-titles.json`); a new session starts in Bypass with the CLI's default model and effort. Cost shows what
  was spent since the app started (saved sessions do not store cost).
- **`!` results**: shown as a shell block at once; the "[I ran: ...]" text sent with the next message is read back as a
  shell block when a saved session opens (like the files' chips). A result never followed by a message is not kept.
- **Pill clicks make no turns**: effort is passed as `--effort` when the process (re)starts - the next message
  restarts it if the effort changed; rename uses the `rename_session` control if it measures working, else `/rename`
  shown as one small line, not a turn. A rename also replaces the written title in `session-titles.json`.

## Left to the builder

- Exact layout and styling of the footer bar, cards and sheet, within the app's look (Paper/Ink/Violet/Coral brushes).
- The file-list cap and skip list for `@`.
- The order the parts are built in, as long as each part is shipped working and tested on its own.
