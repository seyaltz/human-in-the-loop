# human-in-the-loop

<img src="Assets/logo-256.png" width="72" alt="Dev break logo: a question mark in a speech bubble on a violet tile">

A Windows tray app that gives your brain a break from working with AI. In the last 5 minutes of every hour it pops up
a coding / architecture question - heavier in the morning, lighter later in the day. Answer it (optional) and get a
score, see the answer, and discuss it with Claude right beside it. A **Learn** path turns your topics into courses, and
a daily **TL;DR** keeps you on top of what changed in each of them.

## Look

A notebook: grid paper, ink outlines, a violet pen, a lime highlighter for what you got right and a coral teacher's
pen for the score stamp. The styles live in `App.xaml`; the fonts (Bricolage Grotesque, DM Sans, Caveat, JetBrains
Mono, all under the SIL Open Font License, see `Fonts\`) are built into the exe. The logo is `Assets\logo.svg`;
`Assets\make-icon.ps1` draws the same shapes into `Assets\DevBreak.ico` (the exe, window and tray icon).

## Game

Every answer, shown answer and finished lesson earns XP (up to 110 for a 10/10). Climb from Junior to Principal, keep
a daily streak, meet the daily goal (3 questions + 1 lesson, +100 XP) and unlock 13 badges. **Stats** shows your rank,
streak, badges, the last 14 days and how you do per topic - and topics you answer badly come up more often.
Everything is stored locally in `game.json` next to the exe.

## Your topics

On first start the app checks that Claude Code is installed and logged in (it stops with a clear message if not),
then asks **What do you want to learn?** It reads the prompts you typed into Claude Code in the last 7 days and
suggests up to 10 topics - general tech topics only, never anything personal (health, money, your company, its
customers or products). Tick the ones you want. You can also type your own, e.g. *"I want to learn business
regulation on PII"*, and **Add with AI** turns it into a topic with sub-topics. Change them any time with the gear
menu -> **Topics...**. They are saved in `topics.json` next to the exe.

## Learn path

Switch the window to **Learn** (top left, or **Learn path** in the tray menu) for a course per topic. Claude writes each
course (12 lessons, from the foundations to what every senior engineer should know) and each lesson the first time you
open it: the theory, real examples, pitfalls and a check question. When the app starts it prepares every course and
your next lesson in the background (about 2 minutes the first time), so they open instantly. Mark lessons as done to track your progress.
Courses, lessons and progress are saved in `learn\` next to the exe.

## TL;DR

The **TL;DR** tab gives every topic a daily briefing that Claude writes from the web: **Latest releases**, **Hot
topics**, **Quirks**, **Senior must-know** and **Optimization** - one sub-tab each, plus **Sources** with every page
it used as a link. Each item is explained: the **context**, **why** it happened, what it is **good for**, and **for
you** - what it means for your own stack (from "Knowledge about me" below; it says so when an item does not apply,
rather than invent a link). Every topic is prepared in the background when the app starts and rewritten once a day;
**Refresh** rewrites it now. Saved in `tldr\` next to the exe.

## Discuss

**Discuss** opens a chat with Claude on the right of the same window - on a quiz question (it knows the question, the
model answer, your answer and your score), on a lesson (the lesson and its check question) or on a TL;DR. Each
question, lesson and topic keeps its own conversation while you move around. A new answer opens at its first line,
leads with the direct answer, and long code comes folded with **Show all** and **Copy**. Answers are formatted like a tutorial -
lists, bold, code, links and **diagrams** (flowcharts and sequence diagrams, drawn in the app) when a picture helps -
and you can **paste a screenshot** (Ctrl+V) or drop an image into the message - Claude reads it. Lessons draw a
diagram too where it explains the idea better.

Claude can **look at your machine, read-only**, so answers are about your real code rather than guesses: it can read
and search the folders you work in with Claude Code, search the web, and run only a short list of read-only commands
(`git log/status/diff/show`, Gradle `dependencies`/`dependencyInsight`, `dir`), for the git repos it finds in those
folders. It cannot edit files or run anything else.

Gear menu -> **Claude in discussions**: **Clean** (default; only "Knowledge about me") or **My Claude setup** (your
own `CLAUDE.md`, memory, skills, hooks and MCP servers, as in your CLI - slower).

## Sessions - your Claude Code, here

The **Sessions** tab works like Claude Code itself: **New session...** picks a folder and starts a session there, or
pick one of your recent sessions (the same ones your CLI keeps, with their titles) to see what was said and carry
on. Claude runs with your full setup - `CLAUDE.md`, memory, skills, plugins, hooks, MCP servers - and **full
access** in that folder (it may edit files and run commands without asking, like the CLI in bypass mode), and you
see only its answer - the steps and notes it made on the way are folded into one line you can open. **Stop** ends a turn. **Copy resume command** gives `claude --resume <id>` to continue
the same session in a terminal.

A **light** shows each session's state at a glance, in the list and on its pane: **green** (pulsing) while Claude
is working on it, **red** when its last turn failed or was stopped. **Split** puts two sessions side by side, each
with its own chat and Stop, and both can work at the same time; pick a session in the list to open it in the
outlined (active) pane. **By folder** groups the list under a header per folder; click a header to fold or unfold it. A session Claude Code never gave a topic title (only a clipped line of chat)
gets a short, clear one Claude writes once, in the background, kept next to the app.

**Setup** shows what Claude Code has in that folder, the way the CLI sees it: your **memory** (the `CLAUDE.md`
files that apply and your memory notes), **skills** (with what each does - **Use in this session** puts `/skill`
in the message box), **plugins**, **MCP servers** and whether each is connected or needs sign-in, **agents**, and the
model and Claude Code version. Filter by name, open any file.

## Factory - from task to draft PR

Give the **Factory** a task - typed, a **Jira** issue or a **spec** document - and a folder: the repo, or just the
folder where your repos are; Claude finds the one the task is for and learns how to build and test it. A run works
in **your own checkout**, on a new branch from the base - no copy, your dependencies already installed - and switches
back to your branch when it is done. Your own local changes (temp files, local settings) don't stop it - they are
never committed; if the feature needs one of those files (or they block the branch switch), your changes are stashed
(`git stash list`, "devbreak <id>: ..."). It never makes a worktree. Research and answers read your checkout as it is and
switch nothing; only a change to build takes it. When a Claude session (in Dev break or a terminal) has edited a file or
run a command in that repo in the last 10 minutes, the run waits and goes on by itself once the session leaves the repo
alone - or press **Take it anyway**. When another run is already working in that checkout, it waits for **Continue**. The steps:
**gate → workspace → plan → build → verify → review → prove → ship (draft PR) → learn**. Claude understands the mission with your skills,
MCP servers and tools, plans and builds; Dev break runs your verify command (or one Claude proposes) and Claude
fixes until it passes; then Claude **runs the app** with the change and proves it with the evidence the feature
needs - screenshots, a video, tests that fail before and pass after, coverage of the changed lines, mutation testing,
CRAP scores, API responses, logs, traces - shown in the run's **Evidence** tab; a second Claude that can only read reviews the diff with that evidence; only then may Claude
push and open a **draft** PR - you review and merge. If a check keeps failing, the run stops and says why.

Then it **learns** what the next run there needs most: how to build, start and check the service fastest, where the
logic for that kind of task lives, and the language and framework facts it had to look up (say, the Java version and
its features). Facts go straight into the repo's Claude memory (with Undo), so your own sessions there know them too.
Rules, skills and hooks wait for your **Apply** and are kept by the factory **outside the repo**
(`factory-knowledge\` next to the app) - every later factory step in that repo gets them (the rules in its system
prompt, the skills as a plugin, the hooks as settings), and your checkout never gets a file it did not ask for. The
next run starts smarter.

**Built to hold up**: the app is proven once, after the tests pass and the review is clean; a step that loops is
stopped and handed to the manager, which decides in five minutes; a stopped run continues with exactly what stopped
it; nothing that looks like a secret is pushed; a guard refuses the git commands a step may not run, however they
are written; runs in other repos go in dependency order; two Dev breaks never work in the same checkout at once;
and proof scripts are kept per repo and reused. A step that may not push is told so by git itself and by the guard,
and - since anything inside Claude's shell can be undone by Claude - before its own push the factory checks, in its
own code, that no branch of the remote got a commit made here during the run and that the remote settings are as the
run found them; a safety stop (a secret, an unexpected push, a changed remote) always goes to you, never to the AI manager.

**A fast lane**: pick how much of your Claude setup a task loads - **Fast lane** (none of your plugins or MCP
servers - your own settings.json guard hooks stay; the factory's own guard and rules and the repo's CLAUDE.md - about 7 s per step to start instead of 19),
**Curated** (your settings and connectors with only the plugins you list), **Full** (everything), or **Auto**: the
gate reads the task and picks. A fast-lane step that needs your tools says so and the task moves to Full by itself.
The review always runs lean; the draft PR step always runs on your full setup, so your own PR gate checks it.

**A calm screen, like the best agent products**: on the left **+ New task**, **Floor** and an inbox of your tasks
grouped **Needs you / Working / Done**. A task's page shows its **production line** - Gate, Workspace, Plan, Build,
then the quality gates (◆) Verify, Review, Prove and Ship, then Learn - with the task on a belt under the station
working on it, the time each station took, and ↺ where it came back. Hover a station for what it does, the check it
runs and why. Under the line one card says what is happening or what it needs from you, with only the buttons that
make sense now (Stop; Continue or a reply; Accept or Request changes); the rest is behind **⋯**. No money is shown -
tokens and time instead. The **Floor** shows the whole factory: every department's line and the tasks on it now, and
what the managers are doing; **How the factory works** explains it in plain words.

**Review and revise**: when a task is done, its card asks you to review that revision - the decisions it took on
its own are listed as ticked boxes; untick one and say how it should be. **Accept** closes it; **Request changes**
sends it back to the factory as the next **revision** (pick what kind: wrong behaviour, missing, change design, bug).
The run first says in a line what "done" now means and which earlier decision your request overturns, then changes
it, checks it again and updates the same draft PR (an answer is reworked and checked, with a new report). A part
that belongs in another repo under your folder starts a linked Development run there. Not autonomous, it stops after
saying what it understood and builds on your "ok". History shows the revision; a notification says when one is
ready; and after changes, Accept teaches the factory what you meant.

Tell it what you think: **Accept** is your "good" and the factory learns from it; **Request changes** ("also handle
underscores") takes it round the line again and updates the draft PR; while a task waits for you, **reply** to it;
say **why** when you undo or dismiss a lesson. All of it goes into what the factory learns. Every task has a
**Trace** - each step with its time, tokens, every tool call and the exact prompt and answer - and **Export the
trace** (in ⋯) saves it as JSON and Markdown.

**Departments** - a **gate** reads each new task and chooses its department (**Auto**, the default; it says why in
the log), or you pick one:
- **Development**: build a change, as above, to a draft PR.
- **Troubleshooting** (for the call center): a customer's or a site's problem. Claude reproduces it as reported,
  finds a control that works (another site, page or server) and compares, and finds the root cause - changing no
  code. The report opens with a plain **"For the call center"** part (what the customer sees, why, what to tell them,
  a workaround, who fixes it), then the technical part. **Fix it (Development)** hands the diagnosis to a new
  Development run.
- **Research**: a question about the system, answered from the code, the running app and its data, and checked.
- **Management**: a vision, an idea or a company-wide matter - thought through into a decision brief: where the
  company stands (facts from the code, systems and data, with sources), options with value, effort and risk, a
  recommendation and the first steps by department. The report opens with an **Executive summary**.

The three that change no code may also work across the whole folder when an item belongs to no single repo (a
company-wide question): no branch, and git's writing commands are blocked.

**The department manager**: when an autonomous run stops, a manager looks first. It finds out why; when it can
remove the cause (a false alarm, a passing error, something it can fix on this machine) it resumes the run - at most
twice - and may leave a rule so later runs do not stop that way. When it cannot - a login, an access grant, a
decision, a fix it cannot make - it raises a **"Needs a human"** case on the run with a Windows notification: what is
blocked, what it tried, exactly what you need to do, and any change to the factory itself it proposes for you to approve.

**Autonomous** (on by default): no human in the loop. The run takes the plan's open decisions itself (and lists them
in the PR), fixes what it needs on your machine outside the repo (backed up, listed), applies what it learns at once
(with Undo), and when its checks keep failing it makes one fresh attempt; it stops - with a Windows notification - only
for what no tool can do, like a login, and learns how to get further next time. A task that needs no code change (give
me the curl...) is answered and checked against the running app, with no PR.

Without Autonomous, when the task leaves a decision to you, the run **stops after its plan and asks** - answer in the box and it builds.
It never changes anything outside the repo (shared build settings, your profile); if something there must change, it
stops and asks. What you asked to get back - a curl, an answer, a link - is shown on top of the run, ready to copy.

Every step is timed: a step or tool call that runs **over a minute** turns amber and you get a Windows notification;
what was slow goes into what the factory learns, so the next run in that repo is faster. A run keeps **one Claude
process open** for its steps instead of starting Claude - with your whole setup - for each one, and Build leaves
running the app to Prove, so the app is started once.

**Designed report** (a checkbox under Autonomous, remembered): when the run is done, Claude turns its result into
one readable page - the answer first, sections, tables, a diagram where a flow helps, the decisions it took, what is
not verified and the evidence - from the run's own facts only. **Open report** shows it in your browser.

**Delete…** removes one run for good - but first Claude checks what is worth keeping from it: the run's lessons (so the
Insights tab keeps them), facts for the repo's Claude memory, and - only when it is true in every repo - a line for
your global `CLAUDE.md`. You tick what to keep; then its record, trace, evidence, report, worktree and local branch
go. It warns when commits on no remote would be lost, and offers to decline the PR and delete the remote branch too,
unticked. Your checkout and its files are never touched.

The **History** tab lists every run so far: **Continue** one that stopped or needs you, from where it stopped. **Clean
up** deletes what a run left on disk - its worktree (if it had one) and that worktree's session files, its evidence,
and its local branch once that is pushed - after showing what goes and warning about any work that would be lost (an
unpushed branch keeps its commits); **Clean up all** does every finished run. Your checkout and its files, its
sessions and memory, and the remote branches and PRs are never touched, and each run's record and trace stay.

## Insights

The **Insights** tab collects what the factory learned while building features, per repo, newest first: the memory
notes, rules, skills and hooks its runs kept (how to start the service fast, where the logic lives, the language and
framework facts it had to look up), each with the run it came from. Dismissed and undone lessons are left out.

## Save it to your Claude

What you learn should not vanish: every TL;DR item has a **+ my Claude** button. Claude suggests which of your Claude
Code projects it is useful for - as a **memory** note (a fact or gotcha, in that project's Claude Code memory) or as a
**skill** (a procedure, in `<project>\.claude\skills`) - and drafts it with the source and date. Edit or untick each,
then **Save selected**. Nothing is written before that, and nothing is ever overwritten. (For tests and demos,
`DEVBREAK_CLAUDE_PROJECTS` points it at another projects folder.)

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Claude Code CLI](https://docs.claude.com/en/docs/claude-code) installed and logged in (`claude` on the PATH).
  On a new machine the installer may not add `%USERPROFILE%\.local\bin` to the PATH - it says so; add it and reopen
  PowerShell.
  Questions, scores and chat come from your own Claude account.

## Run

```powershell
dotnet run                 # tray mode: the green ? in the tray; left-click to ask now
dotnet run -- --now        # just one question window
```

Build a single exe:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
.\install-startup.ps1      # start it at Windows login (-Uninstall to remove)
```

## Knowledge about you (optional)

The app itself is generic. To set questions, lessons, TL;DRs and discussions in your own stack, pick a source in
**Knowledge about me** - the gear menu, or the tray menu (saved in `settings.json` next to the exe):

- **From my Claude history** - once a day it reads the prompts you typed into Claude Code in the last 7 days
  (`%USERPROFILE%\.claude\projects`, your own prompts only, max 200) and asks Claude for a short summary of your stack
  and recent work. The summary is saved as `knowledge-from-claude.txt` next to the exe. Nothing leaves your machine
  except to your own Claude account.
- **From profile.txt** (default) - write a few lines about yourself in `profile.txt` next to the exe, e.g.
  "Senior Java backend developer, Spring Boot, PostgreSQL, GCP". It is git-ignored.
- **None** - generic questions.
