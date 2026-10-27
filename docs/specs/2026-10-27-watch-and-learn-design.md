# Sessions: watch the turn, learn from it

Status: draft for approval · 2026-10-27

## Why

The user sits in front of long Claude Code turns without knowing whether the time is well spent, and sees the same
routines worked out again session after session. They want Dev break to work like a senior engineer's habits: know
what a wait is for and how long it usually takes, notice early when work drifts, and keep what a session learned so
the next one is faster and better. "Think of it as an autonomous, self-optimizing system."

Success: less time waiting for nothing, fewer turns the user has to interrupt or correct, routines that take one try
instead of several - and a view that shows whether that is actually happening, week over week.

## What was measured (the design rests on these)

An audit of the author's own sessions over 30 days (73 interactive sessions, 2,681 turns, 119 h of active turn time
from Claude Code's own `turn_duration` lines). The audit script is not part of this change; the numbers are:

- **Waiting, about a third of active time.** 19 h inside commands that only wait (`sleep`, `Start-Sleep`, "until ...
  done" loops) and 26 h of turns that only started because a background task finished (979 turns) - mostly builds,
  deploys, containers and test runs.
- **Model time, about 50 h.** Thinking and writing, at the same effort for "commit and push" as for a design.
- **Failed tool calls, 5%** (1,457 of 29,943): hook refusals (302), crashed throwaway scripts (577 - `python -`
  one-offs in 24 sessions), failing tests/builds (143), timeouts (87), wrong shell dialect (35).
- **Rediscovery.** The same command families (a cloud CLI's deploy and log queries, `curl` checks) were rebuilt from
  scratch in 22-34 different sessions each.
- **Drift.** 210 interruptions and 115 prompts that read as corrections: roughly one turn in ten went off course.
- Stop hooks are not the problem: 0.5 h in all.
- Claude Code writes `{"type":"system","subtype":"turn_duration","durationMs":...}` after each turn of a terminal
  (`entrypoint: cli`) session - but not in `-p` sessions (`sdk-cli`, which is what Dev break's panes and the Factory
  write): a check of the 40 newest files found 0 there. Every tool call is a `tool_use` line followed by a
  `tool_result` line with timestamps, in every kind of session - so a command's past durations can be read from all
  of them.

### The audit's rules (the Time view uses them)

- Sessions counted: top-level `*.jsonl` files (not subagent files) whose first `entrypoint` is `cli`,
  `claude-desktop` or `sdk-cli`, and whose first typed prompt is not an automated one (starts with "You are grading",
  "You are a/an evaluator|judge|classifier", "Reply with ONLY").
- A typed prompt: a `user` line, not `isMeta`, not a sidechain, whose content is text (no `tool_result` block) and
  does not start with `<command-`, `<local-command`, "Caveat:", "This session is being continued", nor contain
  `<task-notification>`.
- Active time of a turn: its `turn_duration` when the file has them; otherwise the sum of the gaps between
  consecutive timestamped lines from the typed prompt to the next one, counting only gaps under 15 minutes.
- Tool time: `tool_use` → its `tool_result`, a call over 1 hour ignored. Waiting: tool time of `Monitor` calls and of
  shell commands matching `\b(sleep|Start-Sleep|until |timeout /t)\b`. The user's time: `AskUserQuestion` and
  `ExitPlanMode` calls (shown apart, not as Claude's). Model time: the gap before each `assistant` line, under 15
  minutes. Turns started by a `<task-notification>` are counted as "after a background task".
- Failed call: a `tool_result` with `is_error`. Interruption: "[Request interrupted" in a prompt or text block.
- Correction: a typed prompt whose first 200 characters match
  `\b(no[, ]|wrong|not what|why did you|you didn'?t|stop|again|still (not|doesn)|doesn'?t work|not working|broke)`
  (case-insensitive). It over-counts a little ("again" in a normal request); it is a trend line, not a verdict.
- Dev break also records each pane turn's own duration from its result line (`duration_ms`) in its data file, so
  its turns are exact rather than estimated.

## Goals

1. **Waits are visible and cheap.** The pane says what Claude is waiting on, how long it has been, and how long the
   same command usually takes. Long waits free the user and call them back.
2. **Drift is caught early.** A watcher looks at every running turn and flags loops, error runs and drift, with a
   one-click way to steer or stop.
3. **Sessions learn.** After a turn that needed retries or a correction, Dev break drafts the lesson; the user keeps
   it as memory, a skill or a rule, in their own Claude Code setup, where the CLI loads it natively.
4. **Recurring routines become skills.** On demand, Dev break finds the user's most repeated procedures across
   sessions and drafts a skill for each.
5. **The effect is measurable.** A Time view shows where the time went, week over week.
6. **Lighter effort for light asks** - as a suggestion only.

## Non-goals

- Making builds or deploys themselves faster. That is per project; the skills of goal 4 are where it happens.
- Moving Sessions into the Factory. Sessions stays the interactive place; the Factory stays the pipeline.
- Changing what a turn does without the user's say: no automatic effort or model changes, no automatic lessons.

## Anti-goals (a failure even if it works)

- Personal data in the repo. Everything Dev break learns goes to the user's own `~/.claude` files or to data files
  next to the exe (git-ignored); the repo stays generic. No project names, commands or numbers from the user's data in
  code, prompts or docs beyond this audit summary.
- A watcher that cries wolf. A chip that fires on normal work trains the user to ignore it.
- A cost or time tax: the watcher must not slow the turn, and its AI check must stay cheap.
- Lessons written anywhere without the user clicking Save, or that cannot be undone.

## The design

### 1. Waits (footer + tray)

- While a tool call is running, the pane knows what it is (the streamed `tool_use`). For a shell command (Bash,
  PowerShell) it shows a chip in the footer: `⧗ waiting on <short command> · 3:20 · usually ~6 min`.
  - **"Usually"** comes from a command-timing index: Dev break reads the user's session files in the background,
    pairs `tool_use` → `tool_result` timestamps for shell calls, and keeps the median duration per command key (the
    first two words after any leading `cd ... &&`, as the audit did). Only keys seen 3+ times get a "usually". The
    index is a data file next to the exe, rebuilt incrementally by file modification time, never in the UI thread.
  - Background tasks already show as chips (task_started / task_notification); they get the same elapsed time.
- **Free the user.** When the only thing running is a wait whose usual time is over 2 minutes (or a background task
  with nothing else running), Dev break shows a tray balloon once - "Claude is waiting on <cmd>, about N min; I'll
  tell you" - and a second one when the turn needs the user (ended, asks a question, a permission card, a watcher
  flag). No balloon when the Dev break window is in the foreground.
- **Stop sleep-polling.** The answer-shape note added to every message gets one more line: run long commands in the
  background and do independent work meanwhile, never sleep in a loop to wait.

### 2. The watcher (every running turn)

Two layers, both outside the turn - nothing is sent into the session unless the user clicks.

- **Rules, on every streamed step and on a 30-second tick** (free). The Factory's stuck rules (`Factory.Stuck`) move
  to a shared place and gain:
  - the same tool call failing 3 times in a row; the same call 4 times in a row (existing);
  - 5 failed tool calls within the last 10;
  - a wrong-shell-dialect error (e.g. "is not recognized as", "unexpected EOF", a PowerShell `ParserError`);
  - no new step for 5 minutes while no command or background task is running.
- **AI check, on long turns** (cheap). Once a turn has run 2 minutes, and then every 60 seconds while it keeps
  running and has new steps since the last check, Dev break asks a small model (Haiku, no tools, the user's own CLI)
  with: the user's message, the last ~15 steps (tool name + one-line summary + ok/error), and the elapsed time. It
  answers one line: `on track` or `drifting: <why>`. At most 10 checks per turn. A footer toggle turns AI checks off
  for the pane; the setting is remembered.
- **A no-progress counter** (from published loop-detection practice: exact repeats miss the slow loop where each try
  is a different command with a different error). It counts steps since the last progress - a successful file write
  (Edit/Write), a tool result not seen before in this turn, or a new kind of error - and flags at 12.

### 2b. The coach: diagnose, suggest, let the human think with it

The user's words: "a watcher that suggests improvement, diagnostics, or I give input - human in the loop helping the
agent think out of the box". So a flag is not only a warning; it comes with a diagnosis and a next move.

- **The card.** A flag opens a small card above the box (not a modal): what is going wrong (the rule or the AI check's
  reason), the evidence (the last 3-5 steps that show it), and **one suggested nudge** - a short message to Claude
  drafted by the AI check (e.g. "the last 3 builds failed on the same missing package; check the lock file before
  building again"). Buttons, in the escalation order practitioners use (nudge, then fresh start, then hand-off):
  - **Send nudge** - sends the (editable) nudge into the running turn (the existing send-now path);
  - **Think differently** - asks the small model for 2-3 other approaches to the user's goal given what failed, shows
    them, and the one the user picks is sent as the nudge;
  - **Fresh start** - stops the turn, rewinds the conversation to before it (the existing rewind), and puts the
    original message back in the box with a short "what did not work" summary appended, for the user to edit and send;
  - **Dismiss** - and the same flag is not raised again this turn.
- **The user's own idea.** The card's nudge box is editable, so the user's input goes in the same way.
- **After the turn: a diagnosis line.** Every turn over 2 minutes gets one line under its done line: where its time
  went (waiting / tools / model, from the stream) and, when there is one, the single biggest thing that would have
  saved time ("4 min waiting on the build in the foreground - run it in the background next time"). Rule-based, free.
  The lessons of section 3 are where such a line becomes a kept improvement.
- A flag also triggers the tray balloon of section 1 when the window is not in front.

### What competitors do (2026, from public write-ups)

- Loop detection by fingerprinting (tool, arguments) catches loops within 3 repeats; a no-progress counter catches
  the rest; escalation goes nudge → fresh context → park with the trace for a human.
- Steering mid-run without stopping (a steering file re-read each iteration, or a message joining the turn).
- Self-improvement lives in the harness, not the model: memory files plus a skill library that evolves; research
  (CODESKILL) reports a gain from keeping a compact, pruned skill bank; unreviewed skills are a known risk.
- Observability tools time every step, but a drifting agent still looks healthy on timing alone - hence the AI check.

### 2c. Quality: is the finished work good?

The user: "what have you done to improve the quality" - sections 2 and 3 avoid repeated mistakes; these check the
result. All three are reported in the pane; none blocks a turn.

- **"Done" without proof (rule, free, instant).** At the end of a turn that changed files (Edit, Write, NotebookEdit,
  or a shell command that writes files) and whose answer claims completion ("done", "fixed", "works", "passes",
  "implemented", "ready", "all set" - a generic word list), the pane checks whether any step after the last change
  ran something that checks it: a test runner, a build, the app, an HTTP call to localhost, or a command that runs a
  changed file (`dotnet build|test|run`, `npm|pnpm|yarn test|run|build`, `npx jest|vitest|playwright`, `pytest`,
  `python <changed file>`, `gradlew`, `mvn`, `go test|build`, `cargo test|build`, `curl` to localhost, a browser
  tool). None: a coach card "Claims done, not verified after the last change" with the suggested nudge "Run <the
  check that fits the changed files> and show the output", and Send nudge / Dismiss.
- **Review before accepting (on demand, and suggested).** A **Review** button on the done line of any turn that
  changed files. It starts a fresh, separate review - a new `claude -p` in the session's folder with no memory of the
  writer's reasoning, read-only tools (Read, Grep, Glob, and git diff/log/show) - given the user's message and the
  list of files the turn changed, asked for real defects only (correctness, security, the user's request not met),
  each with file:line and why, ranked. Its findings appear as a card under the turn with **Send to Claude** (sends
  them as the next message) and **Dismiss**. The coach suggests a review (a chip on the done line, never automatic)
  when a turn changed more than 3 files or more than 150 lines.
- **A grade for every substantial turn (the judge).** After a turn ends that did 5+ steps, Dev break asks Haiku (no
  tools) to grade the turn's ending against a rubric: the final answer (what it claims) next to the record of tool
  calls (what it did). The verdict is a line under the done line - `✓ graded: clean` or `⚠ 2 issues` that opens them
  (criterion + one sentence of evidence). It runs after the turn, in the background, at most one at a time per pane,
  and never delays the next message.
  - The rubric is a plain-English file, one criterion per line. Dev break ships a generic one (claims checked against
    the record; work handed back that the agent could have done; unknowns presented as facts; unsupported
    superlatives; a request part silently skipped). A setting adds the user's own rubric file (any path), appended to
    the built-in one - so a team's existing rubric works without copying it into the repo.
  - Measured by a team that runs this kind of judge: 27-44 s and about $0.03 per call with Haiku. A per-pane toggle
    turns grading off; the setting is remembered.
  - Issues the grader finds repeatedly (the same criterion in 3+ turns of a folder) become a lesson card (section 3)
    of kind rule, for the user to Save or Dismiss.

### 3. Lessons after a turn

- **When.** A turn ends after at least 2 failed tool calls that were later followed by a success of the same tool, or
  the user's next message reads as a correction (the audit's pattern: "no", "wrong", "not what", "why did you", "still
  doesn't", "doesn't work"...), or the user stopped the turn and re-asked.
- **How.** Dev break asks a model (the user's default, no tools) with the turn's steps and messages to propose 0-2
  lessons, each of a kind: **memory** (a fact about this project), **skill** (a procedure that worked, with the exact
  commands), or **rule** (an instruction the user gave). It may propose none - that is the common case.
- **The card.** Under the turn: the lesson's text, editable, and **Save**, **Dismiss**. Save writes it:
  - memory → the CLI's project memory folder for the session's folder (`~/.claude/projects/<encoded folder>/memory/`,
    one file per memory with frontmatter, plus its line in `MEMORY.md`);
  - skill → `~/.claude/skills/<name>/SKILL.md` (the CLI loads user skills natively);
  - rule → appended to `~/.claude/CLAUDE.md`, as the CLI's user memory.
  Every save records how to undo it (the Factory's `UndoData` pattern), and the saved lesson can be undone from the
  card or from Insights.
- **Shared with the Factory.** The Factory's lesson model (`FactoryLesson`: kind, name, description, content, status,
  file, undo data, feedback) and its save/undo code move to a shared place that both use. Insights lists Sessions'
  lessons next to the Factory's. A dismissed lesson's reason (optional, one line) is kept, and later lesson prompts
  for the same folder include the dismissed ones so they are not proposed again.

### 4. Learn from my sessions

- A button in Insights. Dev break groups the user's shell commands across sessions by command family (the same key
  as the timing index), keeps families seen in 3+ sessions, and for each takes a few example commands, how often they
  failed, and the folders they ran in.
- It asks a model, once, for a skill draft per family worth one (name, description, steps with the exact commands,
  pitfalls from the failures) - and skips families already covered by an installed skill (the CLI's `initialize`
  reply lists the skills).
- Each draft is a lesson card (section 3) of kind skill: Save, Dismiss, edit.

### 5. The Time view

- A section of Insights: for the last 4 weeks, per week, active hours, and their split into waiting, tools, model,
  failed calls (count and %), interruptions and corrections - read from the session files with the audit's rules.
  A line under it: the top 5 waits and the top 5 failing command families of the latest week.
- Read in the background and cached by file modification time, like the timing index (they share the reader).

### 6. Effort hint

- When the message is short and operational (commit, push, merge, restart, rebuild, deploy, run, status - a word
  list, generic) and effort is not already low, the hint line under the box says: "A quick step - Effort low would
  answer faster." Its link sets Effort low and it stays low (the footer shows it) until the user changes it - effort
  can only change by restarting the process (about 12 s), so "for one message" would cost two restarts. Nothing
  changes without the click.

## Constraints

- C# WPF on .NET 10, the existing app. The user's own `claude` CLI for every model call; nothing sent anywhere new.
- Model calls for the watcher and lessons use the cheap setup the app already has for one-shot calls (no tools;
  Haiku for the watcher; the user's default model for lessons and skills).
- Reading session files never blocks the UI and never writes to them.
- All tests run in a throwaway folder's session, never one of the user's real sessions.

## Left to the builder

- Exact thresholds beyond those above, chip wording, layout of the cards.
- The class split for the shared watcher and lesson code.
- How the timing index stores its data, as long as it is a git-ignored file next to the exe.

## The Factory is unchanged in behaviour

- Its turns keep the two stuck rules it has and keep being cancelled on them; the new rules (5 of 10 failed, dialect,
  5 minutes silent) and the AI check only raise flags in Sessions panes.
- Its lessons keep their flow: memory saved at once, rules applied at once in autonomous runs, skills in its own
  plugin, rules in its own `rules.md`, the `hook` kind. What becomes shared is the lesson model and the save/undo
  code, with the target folder chosen by the caller.

## Calls I made (the user has not decided these)

- The AI check is on by default, Haiku, at most once a minute, at most 10 per turn, only after 2 minutes.
- Balloons only when the Dev break window is not in front.
- Rules go to `~/.claude/CLAUDE.md` (user-wide), memories to the folder's project memory, skills user-wide.
- "Learn from my sessions" is a button, not automatic.
- The effort hint is a suggestion only; its link sets low until changed.
- Dev break grades turns with its own judge and rubric (plus an optional user rubric file), instead of reading
  another tool's output, so the repo stays generic.
- Review is a button plus a suggestion chip, never automatic; the grade is automatic but can be turned off.
