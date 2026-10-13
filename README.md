# human-in-the-loop

A Windows tray app that gives your brain a break from working with AI. In the last 5 minutes of every hour it pops up
a coding / architecture question - heavier in the morning, lighter later in the day. Answer it (optional) and get a
score, see the answer, and discuss it in a follow-up chat.

## Game

Every answer, shown answer and finished lesson earns XP (up to 110 for a 10/10). Climb from Junior to Principal, keep
a daily streak, meet the daily goal (3 questions + 1 lesson, +100 XP) and unlock 13 badges. **Stats** shows your rank,
streak, badges, the last 14 days and how you do per topic - and topics you answer badly come up more often.
Everything is stored locally in `game.json` next to the exe.

## Learn path

Switch the window to **Learn** (top left, or **Learn path** in the tray menu) for a course per topic -
Architecture, GCP, Java & Spring, PostgreSQL, Oracle, Redis, Algorithms, Browser & Next.js, DevOps. Claude writes each
course (12 lessons, from the foundations to what every senior engineer should know) and each lesson the first time you
open it: the theory, real examples, pitfalls and a check question. When the app starts it prepares every course and
your next lesson in the background (about 2 minutes the first time), so they open instantly. Mark lessons as done to track your progress.
Courses, lessons and progress are saved in `learn\` next to the exe.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Claude Code CLI](https://docs.claude.com/en/docs/claude-code) installed and logged in (`claude` on the PATH).
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

The app itself is generic. To set questions in your own stack, pick a source in **Knowledge about me** - the
dropdown at the top of the question window, or the tray menu (saved in `settings.json` next to the exe):

- **From my Claude history** - once a day it reads the prompts you typed into Claude Code in the last 7 days
  (`%USERPROFILE%\.claude\projects`, your own prompts only, max 200) and asks Claude for a short summary of your stack
  and recent work. The summary is saved as `knowledge-from-claude.txt` next to the exe. Nothing leaves your machine
  except to your own Claude account.
- **From profile.txt** (default) - write a few lines about yourself in `profile.txt` next to the exe, e.g.
  "Senior Java backend developer, Spring Boot, PostgreSQL, GCP". It is git-ignored.
- **None** - generic questions.
