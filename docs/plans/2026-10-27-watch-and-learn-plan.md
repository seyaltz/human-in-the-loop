# Sessions: watch the turn, learn from it - Implementation Plan

> For agentic workers: execute task by task (superpowers:executing-plans); steps use `- [ ]`.

**Goal:** a Sessions pane shows what a turn waits on, coaches it when it drifts, checks the finished work, and turns
what went wrong into lessons the user keeps - and Insights shows whether the time is getting better.

**Architecture:** the pane's process gets a raw-line event; a per-turn `TurnLog` records every tool call from it. Pure
rule functions (`Watch`) read the log; model calls (AI check, grader, lessons, review, think-differently) go through
one clean one-shot runner. A background reader (`SessionStats`) indexes the user's session files once, cached by
modification time, for "usually" times, the Time view and "Learn from my sessions". Lessons reuse `FactoryLesson`.

**Tech stack:** C# / WPF / .NET 10, System.Text.Json. No new packages.

**Spec:** `docs/specs/2026-10-27-watch-and-learn-design.md` (approved 2026-10-27: "yes add all three and build it").

## Global constraints

- Repo is public and generic: no user paths, names, companies, commands or numbers from the user's data in code,
  prompts or docs; data goes to git-ignored files next to the exe or to the user's own `~/.claude`. Grep the diff
  before each commit.
- Local only: commit on `feature/watch-and-learn`, no push / PR.
- PROMPT.md is the app's full spec: the last task updates its Sessions and Insights sections (and Verify).
- Comments say why and name measured facts, as the existing files do.
- Nothing blocks or slows a turn: every model call runs after or beside it, in the background; failures fail quiet
  (a line saying the check could not run, never a crash).
- The Factory's behaviour does not change.
- Tests drive a throwaway exe copy (`scratchpad\devbreak-build`) against a session in a throwaway folder; never send
  anything in one of the user's real sessions; pick sessions by exact title.
- Verification: build 0 warnings / 0 errors; `DevBreak.exe -selftest` (new, Task 1) for the pure rules; scripted UI
  runs with screenshots for the rest.

## Files

| File | Responsibility |
|---|---|
| `SessionProcess.cs` (modify) | `event Action<JsonElement>? Line` - every parsed stdout line |
| `TurnLog.cs` (new) | `TurnStep` records, `TurnLog` per turn: steps from raw lines, changed files, lines changed, waits |
| `ClaudeOnce.cs` (new) | one clean one-shot `claude -p` call (model, tools, folder, timeout) - wraps `QuestionGenerator.RunAsync` |
| `Watch.cs` (new) | pure rules: stuck / errors / dialect / silent / no-progress, done-without-proof, diagnosis line, correction words, quick-step words |
| `SessionStats.cs` (new) | background reader of `~/.claude/projects`: command-timing medians, weekly Time breakdown, command families; cache `session-stats.json` |
| `SessionPane.Coach.cs` (new) | wait chip, balloons, 30 s tick, AI check, coach card, diagnosis line, review, grader |
| `SessionLessons.cs` (new) | when to propose, the lesson prompt, save/undo to memory/skill/rule, `session-lessons.json` |
| `SessionPane.Lessons.cs` (new) | lesson cards under a turn |
| `Rubric.cs` (new) | the built-in rubric text, the user rubric path setting |
| `InsightsView.xaml(.cs)` (modify) | Sessions lessons, Time view, Learn from my sessions |
| `SessionPane.Input.cs` (modify) | effort hint |
| `SessionAnswers.cs` (modify) | one more line in the shape note (background, no sleep loops) |
| `App.xaml.cs` (modify) | `App.Balloon(title, text)` and `-selftest` |
| `Selftest.cs` (new) | rule checks for `-selftest`, results to `selftest.txt` next to the exe, exit code 0/1 |
| `PROMPT.md` (modify) | the spec of all of it |

## Review focus

1. A turn the user stops while an AI check or grade is running - the result must be dropped, no card for a turn that
   is gone. Test in Task 4.
2. Two panes on two sessions at once - each watcher, grader and lesson stays with its own session. Task 4.
3. `~/.claude/projects` with a truncated or half-written last line (a session writing right now) - the reader skips
   the line. Task 2 selftest.
4. The model answers out of shape (not JSON, extra prose) - lesson / grade parsing returns nothing, no crash. Task 5/6 selftest.
5. A folder that is not a git repo - Review still works from the changed-file list. Task 5.

---

### Task 1: Raw lines, the turn log, the one-shot runner, the selftest

**Files:** modify `SessionProcess.cs`, `QuestionGenerator.cs` (RunAsync private → internal), `App.xaml.cs`,
`SessionState.cs`; create `TurnLog.cs`, `ClaudeOnce.cs`, `Selftest.cs`.

**Interfaces - produces:**
- `SessionProcess.Line : event Action<JsonElement>` - raised in `Handle` for every parsed line, before the switch.
- `record TurnStep(string Id, string Tool, string Summary, string Key, DateTime Start) { DateTime? End; bool Error; string Result; }`
  (`Key` = shell command family: first two words after a leading `cd ... &&|;`, as the audit; "" for non-shell; `Result` ≤ 300 chars).
- `class TurnLog { DateTime Start; string Message; List<TurnStep> Steps; HashSet<string> Changed; int LinesChanged; void Read(JsonElement line); TurnStep? Running; }`
  - `Read`: `assistant` `tool_use` → new step (`Summary` via `ClaudeSessions.ToolSummary`); `user` `tool_result` → its
    step ends, `is_error`, result text; Edit/Write/NotebookEdit `file_path` → `Changed`; lines = new_string/content line count.
- `SessionState.Log : TurnLog?` (new per turn in `RunTurn`); `SessionState.PastLogs : List<TurnLog>` (this run's).
- `ClaudeOnce.AskAsync(string prompt, string? model, string folder, string tools, string allowed, TimeSpan timeout) : Task<string>` -
  args `-p [--model m] --tools <tools> [--allowedTools <allowed>] --strict-mcp-config --setting-sources "" --no-session-persistence`.
- `App.Balloon(string title, string text)`; `DevBreak.exe -selftest` → `Selftest.Run()` writes `selftest.txt`, exits 0 if all pass.

- [ ] Selftest cases (in `Selftest.cs`, one line each, PASS/FAIL): TurnLog fed a tool_use Bash + error tool_result →
  1 step, Error=true, Key="npm test" for `cd x && npm test --a`; Edit tool_use → Changed has its file_path, LinesChanged = new_string lines.
- [ ] Run `DevBreak.exe -selftest` before implementing → FAIL (class missing / cases fail). Implement. Run → all PASS.
- [ ] Build 0/0. Commit "Sessions: raw stream lines, the turn log, one-shot runner, -selftest".

### Task 2: SessionStats - the user's own history

**Files:** create `SessionStats.cs`. **Consumes:** `TurnLog` key rule (same `Key` function, made `internal static TurnLog.KeyOf(string command)`).

**Interfaces - produces:**
- `SessionStats.Ready : bool`, `SessionStats.Changed : event Action`, `SessionStats.StartAsync()` (called at app start, background, low priority).
- `SessionStats.Usual(string key) : TimeSpan?` - median shell duration for the key, only with 3+ samples.
- `SessionStats.Weeks(int n) : List<WeekStats>`; `record WeekStats(DateTime Start, double ActiveH, double WaitH, double ToolH, double ModelH, double UserH, int Calls, int Failed, int Interrupts, int Corrections, List<(string Key, double Hours)> TopWaits, List<(string Key, int Failed)> TopFailing)`.
- `SessionStats.Families(int minSessions) : List<CommandFamily>`; `record CommandFamily(string Key, int Sessions, int Calls, int Failed, List<string> Examples, List<string> Folders)` (≤5 examples, ≤200 chars each).
- Rules = the spec's "The audit's rules" verbatim (sessions counted, typed prompt, active time incl. the 15-minute gap fallback, waiting, user time, correction regex).
- Cache `session-stats.json` next to the exe: per file path → mtime, size, per-file aggregates; files unchanged are not reread.
- Pane turns: `SessionStats.RecordPaneTurn(string sessionId, DateTime start, double seconds)` → `pane-turns.json`; Weeks() uses it instead of the gap estimate for those turns.

- [ ] Selftest: a temp folder with a 3-line jsonl (cli entrypoint, a typed prompt, a turn_duration 60000, a Bash
  tool_use/tool_result 10 s apart with `sleep 5`, a last half-written line) → ActiveH = 60 s, WaitH = 10 s, no exception.
  `KeyOf("cd a && gcloud run deploy x")` = "gcloud run". Correction regex: "no, use the other" true, "add a button" false.
- [ ] FAIL first, implement (reader takes a root path parameter so the selftest can point it at the temp folder), PASS.
- [ ] Run against the real folder once from the test copy; log the time it took and the number of files to `selftest.txt`. Build 0/0. Commit.

### Task 3: Waits - the chip, the balloon, the note

**Files:** create `SessionPane.Coach.cs`; modify `SessionPane.xaml` (WaitChip in StatusBar, CoachBox above the box, same style as ActionBox), `SessionPane.xaml.cs` (`Wire`: `process.Line += ...` feeds `state.Log`), `SessionAnswers.cs`.

- Chip: while `state.Log.Running` is a shell step (or `state.Tasks` non-empty and nothing else running):
  `⧗ waiting on <Shorten(command,40)> · m:ss[ · usually ~N min]`, refreshed by the pane's 1-s clock.
- Balloon (once per wait): usual > 2 min, or a background task with nothing else running; and the app window not
  foreground (`Application.Current.MainWindow.IsActive == false`). Text: "Claude is waiting on <cmd> - about N min. I'll tell you." Second balloon when the turn ends / asks / a coach flag, same foreground rule.
- Shape note: append " Run long commands (builds, deploys, test suites) in the background and keep doing independent work; never wait in a sleep loop." to the existing note text.
- [ ] UI run: a throwaway session asked to run `ping -n 8 127.0.0.1` in the foreground → screenshot shows the chip
  with a running time; with the test copy's window behind another, a balloon (screenshot of the tray area or the
  app's own log line `balloon: ...` written to `devbreak.log` - add that log line). Build 0/0. Commit.

### Task 4: The watcher and the coach card

**Files:** create `Watch.cs`; modify `SessionPane.Coach.cs`, `SessionSettings.cs` (`SessionSetting.AiCheck = true`).

**Interfaces:**
- `Watch.Flag(TurnLog log, DateTime now, bool commandRunning) : string?` - first reason that applies, in order:
  same call failed 3× in a row; same call 4× in a row (the Factory's two, same wording); 5 failed of the last 10;
  a dialect error in the last result (`is not recognized as|unexpected EOF|ParserError|Unexpected token|command not found`);
  no new step for 5 min while `!commandRunning` and no background task; 12 steps since progress (progress = an
  Edit/Write/NotebookEdit that did not fail, a result text not seen before in this turn, or an error text of a new first 60 chars).
- `Watch.Diagnosis(TurnLog log, TimeSpan total) : string?` - only for turns ≥ 2 min: "waiting a:bb · tools c:dd · model e:ff"
  plus the biggest saving when one applies: a foreground shell wait ≥ 2 min ("<cmd> ran m min in the foreground - run it in the background next time"), or ≥ 3 failed calls of one key ("<key> failed n times - a skill for it would save the retries").
- AI check (Coach): turn running ≥ 2 min, then every 60 s, only if steps grew since the last check, ≤ 10 per turn, `AiCheck` on:
  `ClaudeOnce.AskAsync(prompt, "haiku", folder, tools:"", allowed:"", 90 s)`; prompt = user message + last 15 steps
  (`Tool: Summary [ok|error]`) + elapsed; answer must be `on track` or `drifting: <why>` + optional second line `nudge: <text>`.
  Anything else → ignored. The answer is dropped if `state.Log` changed (turn ended) meanwhile.
- Coach card (CoachBox): title "⚠ " + reason; evidence = last 3-5 steps; editable nudge TextBox (from the AI check's
  `nudge:` or a rule's default: "You seem stuck on <x>. Stop and say what you have tried, then try a different approach."); buttons
  Send nudge (→ `SendNowAsync`, the existing "↳ You, during the turn" note), Think differently (→ ClaudeOnce haiku:
  "2-3 different approaches to <goal> given these failed steps, one line each" → three buttons that fill the nudge box),
  Fresh start (→ StopTurn, then the existing conversation rewind of this turn with code=false, then the message back in
  the box + "\n\nWhat did not work last time: <reason>"), Dismiss (reason not raised again this turn).
- Footer toggle "AI check: on/off" (BarButton) saved in SessionSettings.
- [ ] Selftest: each Flag rule from a crafted TurnLog (positive and one negative each); Diagnosis wording for a
  turn with a 3-min foreground step.
- [ ] UI run on a throwaway session: ask Claude to run a failing command 4 times ("run `exit 1` four times, one at a
  time") → coach card with the stuck reason; Send nudge → the note appears in the turn. A 2-pane run: card only in its own pane.
  Stop during an AI check → no card after. Build 0/0. Commit.

### Task 5: Quality - done without proof, Review, the grade

**Files:** create `Rubric.cs`; modify `Watch.cs`, `SessionPane.Coach.cs`, `SessionPane.xaml.cs` (done line), `SessionSettings.cs` (`Grade = true`), `AppSettings` key `rubricFile`.

**Interfaces:**
- `Watch.Unverified(TurnLog log, string answer) : string?` - spec's rule: files changed, completion word in the answer
  (`\b(done|fixed|works|passes|implemented|ready|all set)\b`, case-insensitive), and no step after the last change whose
  command matches the spec's check list (`dotnet (build|test|run)`, `(npm|pnpm|yarn) (test|run|build)`, `npx (jest|vitest|playwright)`,
  `pytest`, `python .*<changed file name>`, `gradlew`, `mvn`, `go (test|build)`, `cargo (test|build)`, `curl .*localhost|127\.0\.0\.1`)
  or a browser tool (`mcp__*playwright*|*chrome*|*browser*`). Returns the nudge "Run <check> and show the output" with <check>
  picked from the changed files' extensions (.cs → dotnet build, .ts/.tsx/.js → npm test, .py → pytest, .java/.kt → gradlew test, else "the app").
- Review: done line gets "Review" (hyperlink) when `Changed.Count > 0`; suggestion chip "Review suggested" when > 3 files or > 150 lines.
  Runs `ClaudeOnce.AskAsync` (session's model or default, tools "Read,Grep,Glob", allowed same, folder, 5 min) with:
  the user's message, the changed files, and `git diff -- <files>` output when the folder is a git repo (capped 60,000
  chars), asking for real defects only, `file:line - problem - why`, ranked, or "no defects found". Card under the turn:
  findings rendered with CodeRenderer + "Send to Claude" (→ next message: "A review found:\n<findings>\nFix what is real.") + Dismiss.
- Grade: after a turn with ≥ 5 steps, `Grade` on, at most one running per pane: haiku, no tools, 120 s, prompt =
  rubric (built-in + user file) + final answer (≤ 4,000 chars) + record (each step `Tool: Summary → ok|error`, ≤ 60 steps)
  → JSON array `[{"criterion","verdict":"ok|violated","evidence"}]`. `Rubric.Parse(string raw) : List<(string Criterion, string Evidence)>?`
  (violations; null when not a JSON array). Line under the done line: "✓ graded: clean" / "⚠ n issues" (click → list); null → "grade unavailable".
- Built-in rubric (Rubric.cs, generic): 1 claims match the record (a claimed test/build/check appears as a step);
  2 work handed back the agent could have done itself; 3 unknowns stated as facts; 4 unsupported superlatives
  ("all", "always", "fully" without evidence); 5 a part of the request silently skipped.
- Settings: a "Grading rubric file" picker in Setup (path stored in AppSettings `rubricFile`), appended after the built-in.
- Repeats: the same violated criterion in 3+ graded turns of a folder (counted in `session-lessons.json`) → a lesson card of kind rule (Task 6).
- [ ] Selftest: Unverified true for Edit then "Done." with no check; false with a later `dotnet build`; Rubric.Parse on
  a valid array, on prose (null), on a fenced array (parsed).
- [ ] UI run: a throwaway session asked to "create a.py printing 1 and say done, do not run it" → coach card "Claims done,
  not verified"; Review on that turn → findings card; the grade line appears within ~60 s. Non-git folder. Build 0/0. Commit.

### Task 6: Lessons

**Files:** create `SessionLessons.cs`, `SessionPane.Lessons.cs`; modify `InsightsView.xaml.cs`.

**Interfaces:**
- `SessionLessons.ShouldPropose(TurnLog log, string? nextUserMessage, bool stoppedAndReasked) : bool` - spec triggers
  (≥ 2 failed calls later followed by a success of the same tool; a correction; stopped and re-asked).
- `SessionLessons.ProposeAsync(SessionState state, TurnLog log, string? correction) : Task<List<FactoryLesson>>` -
  ClaudeOnce with the user's default model, no tools, 120 s; prompt asks for 0-2 lessons as JSON
  `[{"kind":"memory|skill|rule","name","description","content"}]`, includes the dismissed lessons (with feedback) for the
  folder so they are not proposed again; parse failure → empty.
- `SessionLessons.Save(FactoryLesson l, string folder) : string` - memory → `~/.claude/projects/<ClaudeSessions folder key>/memory/<name>.md`
  with frontmatter (name, description, metadata.type) + a `- [name](file) — description` line in that folder's `MEMORY.md`;
  skill → `~/.claude/skills/<name>/SKILL.md` (frontmatter name, description; a `-2` suffix when taken);
  rule → appended to `~/.claude/CLAUDE.md` (UndoData = the appended text). `Undo(FactoryLesson l)` reverses each.
- Storage `session-lessons.json` next to the exe: `{folder, sessionId, created, lesson}` list + grade-criterion counts per folder.
- Card under the turn: kind badge, editable name/description/content, Save / Dismiss (optional one-line reason); saved → "Saved to <file> · Undo".
- Insights: a "From Sessions" group listing saved and pending session lessons, with Undo.
- [ ] Selftest: ShouldPropose cases; Save+Undo of each kind against a temp home (the home root is a parameter) leaves the files as before.
- [ ] UI run: correct Claude in a throwaway session ("no, use the other file") → a lesson card; Save memory → the file
  exists in the throwaway folder's project memory; Undo → gone. Build 0/0. Commit.

### Task 7: Insights - Time view and Learn from my sessions

**Files:** modify `InsightsView.xaml(.cs)`.

- Time section at the top: 4 weeks of `SessionStats.Weeks(4)` - a row per week: active h, waiting / tools / model /
  your time (bar segments with labels), failed % , interruptions, corrections; under it the latest week's top 5 waits
  and top 5 failing families. "Reading your sessions…" until `SessionStats.Ready`.
- "Learn from my sessions" button: `SessionStats.Families(3)` minus families already named in an installed skill
  (`SlashCommands.For(any folder)` commands with Source "yours" or plugin skills - match key words in name/description),
  top 8 → one ClaudeOnce call (default model, no tools, 5 min) for skill drafts as the Task 6 JSON (kind skill) → lesson cards in Insights (Save → `~/.claude/skills`).
- [ ] UI run on the test copy: Insights shows 4 week rows with numbers; Learn shows at least one draft card or "nothing
  repeated enough". Screenshot. Build 0/0. Commit.

### Task 8: Effort hint, PROMPT.md, final check

**Files:** modify `SessionPane.Input.cs` (UpdateHint), `Watch.cs` (`IsQuickStep(string text)`: ≤ 80 chars and a word
of `commit|push|merge|restart|rebuild|deploy|run|status|pull|rebase|tag`), `PROMPT.md`.
- Hint when `IsQuickStep` and effort ≠ low: "A quick step - Effort low would answer faster" with link → sets low (the existing effort setter).
- PROMPT.md: Sessions (waits, coach, quality, lessons, effort hint) and Insights (Time, Learn, session lessons), Verify paragraph.
- [ ] Selftest all PASS; clean-clone Release build 0/0; grep the branch diff for private data; one fresh reviewer
  (most capable model) over the whole branch diff; fix what is real. Commit.
