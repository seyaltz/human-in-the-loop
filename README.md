# human-in-the-loop

A Windows tray app that gives your brain a break from working with AI. In the last 5 minutes of every hour it pops up
a coding / architecture question - heavier in the morning, lighter later in the day. Answer it (optional) and get a
score, see the answer, and discuss it in a follow-up chat.

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

## Your profile (optional)

Create `profile.txt` next to the project (or the exe) describing your role and stack, e.g.
"Senior Java backend developer, Spring Boot, PostgreSQL, GCP". Questions will be set in your world.
It is git-ignored.
