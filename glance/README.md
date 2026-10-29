# Build your own memory meter with AI

A free little Windows app that shows your memory use right by the clock. Hover it to see which apps are using the most, and end a greedy one in two clicks, without opening Task Manager.

**You build it yourself, with AI, without writing any code.** This folder has everything you need: the step-by-step kit, every message to send, and the finished code to compare with or build directly. It's all open source.

![The memory number on the taskbar with its card open: memory and CPU meters and the top ten apps with End buttons](docs/glance.png)

## Start here

1. **Read the kit:** [KIT.md](KIT.md), or the printable [PDF](docs/build-it-yourself-kit.pdf). It covers what you need, a 10-minute setup, your options, and every step.
2. **Paste the kickoff message** from [prompts/kickoff.txt](prompts/kickoff.txt) into your AI app. Change the [brackets] first.
3. **After each step, try it.** If it works, say "Works. Next step." That's most of the conversation.

It takes about an hour and a half, mostly waiting. You need a Windows PC and an AI app that can build on your computer: one that can create files and run programs for you, not just chat. **Any of them works**, for example Antigravity, Claude Code, Codex or Cursor. [KIT.md](KIT.md), section 3, shows where to paste the message in each. Nothing else to pay for.

## What's in here

| | |
|---|---|
| [KIT.md](KIT.md) | The build-it-yourself kit: setup, options, the steps, making it your own, fixing problems, staying safe |
| [docs/build-it-yourself-kit.pdf](docs/build-it-yourself-kit.pdf) | The same kit, ready to print |
| [prompts/](prompts/) | Every message to paste, as files you can copy, with [the order to send them](prompts/README.md) |
| [src/](src/) | The finished code of our copy, which we call Glance. Compare it with yours, or build it directly |
| [tools/](tools/) | The tests our copy passed, and the small test program they use |
| [AGENTS.md](AGENTS.md) | Notes your AI app reads to build and test the finished code (Codex reads it automatically; `CLAUDE.md` and `GEMINI.md` point other apps to it) |

Every lesson from our own build is already a line in the kickoff message. That includes what a second, independent review found before we shared it. So your first try should go more smoothly than ours did.

## Build the finished code instead

Want to skip to the end, or see how yours compares? Download it from the [repository's main page](https://github.com/petercarter19-hue/Apps) (the green **Code** button, then **Download ZIP**), unzip it, open the `glance` folder in your AI app (any of them), and say:

```text
Build this app into one file I can double-click, then start it. Install Microsoft's free .NET SDK first if it's missing.
```

Or, if you have the [.NET 10 SDK](https://dotnet.microsoft.com/download), run this in the `glance` folder:

```
dotnet publish Glance.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

The app is `dist\Glance.exe`. Because you built it on your own PC, Windows opens it without any warning.

**Just want to try ours?** A ready-made Glance.exe is on the [Glance 1.1.0 release](https://github.com/petercarter19-hue/Apps/releases/tag/glance-v1.1.0). Windows shows "Windows protected your PC" for downloaded apps that aren't code-signed. Click **More info**, then **Run anyway**.

## Using it

- **The number by the clock** is your memory use. Green under 60%, amber to 85%, red above. When memory crosses 90%, it pulses red once.
- **Hover** the number to open the card. You'll see memory and CPU meters, the app that grew the most in the last 10 minutes, and the top ten apps, one row per app.
- **End takes two clicks.** The first click turns the button into a red "End?"; the second ends the app. Windows' own parts show a lock instead, and Windows Explorer gets "Restart". If Windows refuses, the button offers **As admin**.
- **Click** the number to pin the card open. **Right-click** it for the menu: Show CPU tile, Start with Windows, Open Task Manager, Remove from this PC, Quit.
- **Can't see the number?** Windows tucks new icons under the **^** arrow by the clock. Click it and drag the number onto the taskbar.
- **To restart it,** open it again from the Start menu.

End works like Task Manager's "End task": anything unsaved in that app is lost.

**To remove it,** right-click the number and choose **Remove Glance from this PC**, then delete the app file.

**Privacy:** it only reads numbers Windows already has, makes no network connections, and collects nothing. Its settings live in `%LocalAppData%\Glance`.

## How the finished code works

C# / .NET 10 WinForms, no main window, Windows 10 or 11 (64-bit).

- **`TrayIcon`** talks to `Shell_NotifyIcon` at version 4, so Windows sends `NIN_POPUPOPEN` on hover. That is what lets the card replace the stock tooltip.
- **`ProcessScanner`** reads each process's private working set (Task Manager's "Memory" number) and CPU time, and groups processes by executable.
- **`ProcessKiller`** ends an app plus the processes it started:
  - It stays inside the user's own session.
  - A process only counts as a child if it started after its parent, because Windows reuses process numbers.
  - Each process is checked by its start time right before it's ended.
  - Windows' protected processes and Explorer are never swept in.
  - **As admin** runs a second copy of the app elevated with `--end`, which makes the same checks.
- **`HoverCard`** is a borderless, custom-painted window that follows the Windows light or dark setting and sits next to the tile on any taskbar edge.
- **`Shortcut`** keeps the Start menu entry.
- **Opening the app while it's running** signals the running copy to quit and takes its place.

**Tests** (in `tools\`). They never move your mouse, take pictures of your screen, or end anything except the test program:

- `release-test.ps1` runs 22 checks: first run, restart, quit, a moved file, End with a child process, the start-time check, and a leak check.
- `message-test.ps1` sends Windows' own hover and click messages to the app.
- `smoke-test.ps1` runs End against a test program that holds 3 GB.
- **Before running them:** publish `tools\Hog` into `tools\Hog\dist` first.

**Debug flags:**

- `--preview` writes the card's state and pictures to `%LocalAppData%\Glance` every second.
- `--pinned` opens the card at startup.
- `--quit` closes the running copy.

## License

[MIT](../LICENSE): use it, change it, share it.
