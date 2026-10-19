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
