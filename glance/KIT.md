<p align="center"><img src="docs/kit/banner.svg" alt="Make your own memory meter, free, on your Windows PC" width="100%"></p>

A small number by the clock tells you how much memory your computer is using. Rest your mouse on it and a card slides up with the ten apps using the most, and the one that grew the most in the last ten minutes. Two clicks end a greedy app, without opening Task Manager. You don't write any code. You paste one message to the AI, try what it builds, and tell it what happened. There's no AI inside the app and nothing to pay for beyond your AI plan.

| ⏱️ 1½ hours | 💬 About 6 | 💵 $0 | 🧑‍💻 No code |
|:-:|:-:|:-:|:-:|
| step by step, mostly waiting | short messages to send | beyond your AI plan | you write none of it |

> [!IMPORTANT]
> **Made with Claude Code, works with any AI app:** We built ours with Claude Code. You can use any AI coding app you like, for example Antigravity, Codex or Cursor: the messages and steps are the same. Section 3 shows where to paste the message in each.

**Contents**

1. [🎯 What you'll end up with](#1--what-youll-end-up-with)
2. [🧰 What you need](#2--what-you-need)
3. [🛠️ Get set up (10 minutes)](#3--get-set-up-10-minutes)
4. [🎨 Choose your options](#4--choose-your-options)
5. [💬 The one message that builds it](#5--the-one-message-that-builds-it)
6. [🧪 Try each step](#6--try-each-step)
7. [✨ Make it your own](#7--make-it-your-own)
8. [🩹 When something goes wrong](#8--when-something-goes-wrong)
9. [🛡️ Stay safe](#9--stay-safe)
10. [🎁 Give it to a friend](#10--give-it-to-a-friend)
11. [📖 Words you'll hear](#11--words-youll-hear)

## 1. 🎯 What you'll end up with

- **A number by the clock:** your memory use in %, updated every second. Green when things are calm, amber when busy, red when nearly full. When memory gets very full, the tile pulses red once, and that's all the nagging it does. A second tile for CPU is one click away.
- **A hover card:** rest your mouse on a tile to see memory and CPU meters, the app that grew the most in the last ten minutes, and the top ten apps. Each app is one row, the way Task Manager groups them, so your web browser is one line, not forty.
- **Two-click End:** the first click turns the button red and asks "End?", the second ends the app. Windows' own parts are locked so you can't break anything by accident.
- **Small comforts:** click the tile to keep the card open, right-click for a menu, light and dark looks that follow Windows. It starts when you sign in, it's in your Start menu, and opening it again restarts it.
- **One file:** the finished app is a single file you double-click. It needs nothing else installed.

<p align="center"><img src="docs/kit/hero.png" alt="The memory number on the taskbar with its card open: memory and CPU meters, and the top ten apps with End buttons" width="380"><br><sub>Ours, with the card open. Yours will look much like it, with your own name.</sub></p>

Building it takes about an hour and a half, mostly waiting. You'll send about half a dozen short messages, or four if you let the AI build it all in one go.

## 2. 🧰 What you need

- **A Windows 11 PC** with about 1.1 GB of free space. That's Microsoft's free building tools (about 784 MB) plus the project. The finished app itself is one file of about 49 MB.
- **An AI app that can build on your computer:** one that can create files and run programs for you, not just chat, for example Antigravity, Claude Code, Codex or Cursor. Any of them works; nothing here is tied to one. You'll need a plan for it, if it needs one. The AI does the building. It's the only thing you pay for.
- No graphics card, no microphone, and no AI models to download.

| Part of the app | What runs it | Cost |
|---|---|---|
| The memory and CPU numbers | Windows' own counters, the same numbers Task Manager shows | $0 |
| The hover card, the top ten and End | Code the AI writes, using features built into Windows | $0 |
| Building it | Microsoft's free .NET SDK, which the AI installs if it's missing | $0 |
| AI models | None. The AI is only the builder | $0 |

> [!TIP]
> You never need a credit card, an API key or a cloud account. The app reads numbers from your own computer and sends nothing anywhere.

## 3. 🛠️ Get set up (10 minutes)

1. **Install your AI app** and sign in. It must be one that can create files and run programs on your computer, not just chat.
2. **Open the part of your AI app that can work on your computer** (its agent). It's like the chat you know, but the AI can create files and install things on your computer.
3. **Make an empty folder** for your app, for example `C:\MemoryMeter`, and pick it when your AI app asks which folder to work in.
4. **When the AI asks permission** to run something or install Microsoft's building tools, read the one-line description and allow it. Each app asks in its own way: an Allow button, or a setting for how much it may do without asking. Allowing it for this folder is how it installs things for you.

Where to paste the message in some popular AI apps. Their menus change often, so look for the same idea if yours looks different.

| If you use | Open your folder | Paste the message into |
|---|---|---|
| Antigravity | Open the empty folder as your workspace | A new conversation or task with the agent |
| Claude Code | In the Claude desktop app, open the Code tab and pick the folder; or type `claude` in a terminal inside the folder | The chat box |
| Codex | In the Codex app, add the folder as a project; or type `codex` in a terminal inside the folder | The chat box |
| Cursor | File › Open Folder | The Agent chat in that same window |
| Something else | Any AI app that can create files and run programs in a folder on your PC | Wherever you talk to its agent |

## 4. 🎨 Choose your options

Decide these before you start. Changing them later works, but it means rebuilding parts. Write your answers into the [brackets] of the message in the next section.

| Choice | Suggested | Notes |
|---|---|---|
| Your app's name | MemoryMeter | Anything you like. It's what you'll see in the menu and in Task Manager. |
| Tile colors | Green under 60, amber to 85, red above | The numbers are percentages. Lower them if you want earlier warnings. |
| When the tile pulses | 90% | One red pulse when memory crosses this line. Say "never" to turn it off. |
| How many apps the card lists | Ten | Five makes a smaller card. The card always sizes itself to the list. |

<table><tr><td align="center" valign="top"><img src="docs/kit/tile-green.png" alt="A green memory tile showing 28" width="230"><br><sub>Under 60: green</sub></td><td align="center" valign="top"><img src="docs/kit/tile-amber.png" alt="An amber memory tile showing 66" width="230"><br><sub>60 to 85: amber</sub></td><td align="center" valign="top"><img src="docs/kit/tile-red.png" alt="A red memory tile showing 90" width="230"><br><sub>Above 85: red (pulses once at 90)</sub></td></tr></table>

> [!NOTE]
> Your display scaling (how much Windows enlarges things) isn't a choice: the message asks the AI to check it and fit the card to it.

## 5. 💬 The one message that builds it

Change the [brackets] and paste it into your AI app. It answers every question up front, so the AI can just build.

```text
I want to build a small Windows app called [MemoryMeter]. It lives by the clock, shows how much memory and CPU my computer is using, and has a hover card that lists the top ten apps and lets me end one without opening Task Manager. I'm not technical and I've never coded. Build it step by step, show me something working after each step, and test what you can yourself before asking me.

FREE, ONE FILE, ON MY COMPUTER
- Everything is free and runs on this computer. No paid services, no accounts, no API keys, never ask me for payment details. It doesn't need any AI model: it just reads the numbers Windows already has (the same ones Task Manager shows).
- First check my computer: the Windows version, my display scaling, and which free building tools are already installed. Install anything missing from Microsoft or other well-known, trusted sources, and tell me in one line what you picked and how big it is.
- The finished app is one file I double-click, that runs without installing anything else. Keep it in one place and always run that copy.

WHAT IT DOES
- A small tile by the clock showing memory % as a number I can read. A second tile for CPU % is off at first; I can turn it on from the right-click menu. Colors: [green under 60, amber to 85, red above]. When memory goes over [90%], the memory tile pulses red once. No other nagging.
- Hover a tile and a card slides up: memory and CPU meters, "biggest grower in the last 10 minutes", and the top [ten] apps by memory, grouped the way Task Manager groups them (one row per app, not forty). Hovering the CPU tile sorts the list by CPU.
- Each row has an End button. Ending takes two clicks: the first turns the button into a red "End?", the second ends the app. Moving away or waiting a few seconds cancels.
- Click a tile to pin the card open; Esc or a click elsewhere closes it. Right-click a tile for a menu: Show CPU tile, Start with Windows, Open Task Manager, Remove from this PC, Quit.
- Easy to find and restart: put it in my Start menu, turn on Start with Windows the first time it runs, and make opening it again restart it (the old copy quits and a fresh one starts). The first time it runs, show a short Windows notice saying where it is.
- It follows the Windows light or dark setting, and uses almost nothing while the card is closed.

SAFETY
- Never end anything without my two clicks. Windows' own system parts show a lock instead of an End button. Windows Explorer (my taskbar and desktop) gets "Restart" instead of "End".
- End must never close the wrong program. Windows reuses process numbers, so check each process is still the one I picked right before ending it. Only end programs that belong to my own account in my own sign-in, never another user's, and never sweep in Windows' own parts or Explorer. "As administrator" makes the same checks.
- If Windows refuses to end something, say so on the card and offer to try again as administrator, with the normal Windows permission prompt. Never fail silently: show problems on the card and write them to a log.
- It never deletes my documents or photos and never sends anything anywhere. Choosing "Remove from this PC" in the menu deletes only the app's own settings, log and preview pictures and its Start menu shortcut.
- Tests only ever end a test program you make for the purpose, never my real apps. Tests never take pictures of my real screen, move my mouse or press real keys: use the app's own pictures of itself instead.

FIT MY SCREEN
- The card sizes itself to what's in it, with no empty space, at whatever display scaling I use. Check it at my scaling and show me a picture.
- The card sits next to the tile wherever my taskbar is: bottom, top or side, even when it auto-hides.
- Windows hides new tray icons under the little up arrow (^) by the clock. Tell me in one line how to bring the tile out.

BUILD ORDER (all of it by the end)
1. The live memory tile with its colors (and the optional CPU tile), starting when I sign in.
2. The hover card: the meters, the biggest grower and the top ten.
3. Two-click End, with the lock, Restart for Explorer, and "as administrator".
4. Pin, the right-click menu, light and dark, the Start menu entry and restart, and the finished one-file app.

HOW TO WORK WITH ME
- Plain English, short answers. Pick sensible defaults and tell me what you picked; don't ask me technical questions.
- Short plan first, then build. No long research reports.
- If I mention a new idea, add it to the list instead of building it, unless I say "now".
- Build each new version in a separate folder and swap it in only after your tests pass. Quit the old copy first and clean up anything old test copies leave behind, so there's only ever one copy and one set of tiles.
- After each step, show me a picture, and tell me plainly what you tested and what you couldn't.
- When everything works, review the whole app as if it were going on a stranger's PC, fix what you find, and test it once more in Windows Sandbox (a clean, throwaway copy of Windows) if my Windows has it.

Start with step 1.
```

> [!IMPORTANT]
> **Want it all in one go?** Change the last line to: "Build all the steps without stopping. Test each one yourself, and only stop when everything works or you need a decision only I can make." Then come back in about an hour.

## 6. 🧪 Try each step

After each step, try it yourself. If it works, just say **"Works. Next step."** Every decision is already in your first message, so that's all the AI needs. If something's off, say what you did and what you saw.

### Step 1 · The memory tile

The first time it runs, a short notice tells you where it is. Windows tucks new icons under the little up arrow (^) by the clock: click it and drag the tile onto the taskbar. Then open a few big browser tabs and watch the number climb and change color.

<p align="center"><img src="docs/kit/welcome.png" alt="A Windows notice: the app is running, and to drag it out from under the up arrow by the clock" width="430"><br><sub>The first-run notice. The tile starts out hidden under the ^ arrow.</sub></p>

<table><tr><td align="center" valign="top"><img src="docs/kit/tile-green.png" alt="The taskbar with a green memory tile" width="230"><br><sub>Calm: green</sub></td><td align="center" valign="top"><img src="docs/kit/tile-amber.png" alt="The taskbar with an amber memory tile" width="230"><br><sub>Busy: amber</sub></td><td align="center" valign="top"><img src="docs/kit/tile-red.png" alt="The taskbar with a red memory tile" width="230"><br><sub>Nearly full: red</sub></td></tr></table>

### Step 2 · The hover card

Rest your mouse on the number. Check the top ten and the "biggest grower" line. Then click **By CPU**: the same list, sorted by CPU.

<p align="center"><img src="docs/kit/card.png" alt="The card: memory and CPU meters, then the top apps with End buttons, and locks on Windows&#x27; own parts" width="380"><br><sub>One row per app. Windows' own parts show a lock instead of End.</sub></p>

### Step 3 · Two-click End

Start the test program the AI made. Find it in the card and click End once: it turns into a red "End?". Wait a few seconds and it cancels. Click twice, and it's gone and the number drops.

<p align="center"><img src="docs/kit/end-armed.png" alt="A row in the card with a red End? button, waiting for the second click" width="420"><br><sub>The first click only asks. The second click ends it.</sub></p>

### Step 4 · Finishing touches

Click the tile to pin the card; press Esc to close it. Right-click the tile and try the menu. Switch Windows to dark mode and hover again. Then open the app again from the Start menu: it restarts.

<table><tr><td align="center" valign="top"><img src="docs/kit/menu.png" alt="The right-click menu: Show CPU tile, Start with Windows, Open Task Manager, About, Remove from this PC, Quit" width="300"><br><sub>Right-click for the menu</sub></td><td align="center" valign="top"><img src="docs/kit/restarted.png" alt="A Windows notice saying the app restarted" width="330"><br><sub>Open it again: it restarts</sub></td></tr></table>

<p align="center"><img src="docs/kit/start-menu.png" alt="Searching the Start menu finds the app" width="600"><br><sub>It's in your Start menu: type its name</sub></p>

<p align="center"><img src="docs/kit/dark.png" alt="The card in dark mode" width="340"><br><sub>It follows Windows' dark mode</sub></p>

## 7. ✨ Make it your own

Once it works, change anything by asking for it. Some starting points:

| You want | Say to the AI |
|---|---|
| Both numbers by the clock | "Show the CPU tile by default too." |
| Earlier warnings | "Turn the tile amber at 50 and red at 75, and pulse at 80." |
| A shorter list | "Show the top five apps instead of ten." |
| More to watch | "Add a third tile for disk space," or "add network speed to the card." |
| A warning you can't miss | "When memory stays above 90% for a minute, show a Windows notification naming the biggest app." |
| Some apps left alone | "Never show an End button for these apps: [names]." |
| Click instead of hover | "Only open the card when I click a tile, not when I hover." |
| A new look | "Make the card bigger, with larger text," or "use round tiles." |
| To know where things are saved | "Tell me where the settings and the log are saved, and add 'Open log folder' to the menu." |

> [!TIP]
> Ask for one change at a time, try it, then ask for the next. Say "build it in a separate folder and swap it in when the tests pass" for anything big, so the copy you're using never breaks.

## 8. 🩹 When something goes wrong

Always tell the AI what you did and what you saw, and ask it to read the log. It fixes most things in one message.

| What happens | Say to the AI |
|---|---|
| A box says "You must install .NET" | "Make it one file that runs without installing anything else." |
| You can't see the tile | "I can't see the tile by the clock. Tell me how to bring it out from under the up arrow." |
| The number is gone and you don't know how to start the app | Open it from the Start menu. If it isn't there: "Put the app in my Start menu, turn on Start with Windows, and make opening it again restart it." |
| The card is too big, with empty space | "The card's too big and there's empty space at the bottom. Make it fit what's in it, at my display scaling." |
| It looks like two copies are running | "It looks like there are two of them. Make sure only one copy runs, and remove anything old test copies left behind." |
| Hovering doesn't open the card | "I rested my mouse on the tile and nothing opened. Clicking does open it. Read the log and fix hovering." |
| End doesn't close an app | "I clicked End twice on [app] and it's still running. Read the log. If Windows refused, offer 'as administrator'." |
| Its numbers don't match Task Manager | "Task Manager says my web browser uses 2 GB, but the card says 3 GB. Use the same number Task Manager shows." |
| A rebuild fails, saying the file is in use | "Quit the running copy first, build in a separate folder, then swap it in." |

## 9. 🛡️ Stay safe

- **End is like Task Manager's "End task".** Anything unsaved in that app is lost. Save your work first, and only end apps you recognize.
- **Keep the lock on Windows' own parts.** Ending them can freeze or restart your computer. Don't ask the AI to remove the lock.
- **"As administrator" is for when you mean it.** Windows asks first; say yes only for an app you know.
- **Tests use a test program, never your apps.** The message tells the AI to test End on a program it makes for the purpose.
- **Keep a backup of the working copy.** Before you ask for a big change, say "save a copy of the working app first," so you can always go back to the version that worked.
- **Nothing leaves your computer.** The app only reads your PC's own numbers. It never deletes your documents or photos and never sends anything. If you choose **Remove from this PC** in the menu, it deletes only its own settings, log and preview pictures and its Start menu shortcut.
- **Only download from trusted sources.** The message tells the AI to get its building tools from Microsoft or other well-known sources.

## 10. 🎁 Give it to a friend

When yours works, the AI can package a clean copy that a friend starts with one double-click. It contains none of your settings or logs.

```text
Make a friend version of my app that I can send as one zip. Build it in a separate copy and don't touch the app I use.
- None of my data: no settings, logs, my name, or my folder paths anywhere. Search the whole copy to prove it's clean.
- One file that runs without installing anything else, plus a one-page "How to start it" in plain words: where the tile appears, how to bring it out from under the up arrow (^), that it starts with Windows and how to turn that off, how to restart it (open it again), and how to remove it (Remove from this PC), and what to do if Windows shows "Windows protected your PC" for a file from a friend.
- The first time my friend opens it, the welcome notice says where it is and that it starts with Windows.
- Before you give it to me, run it from scratch in Windows Sandbox and check that the tile, the card, restart and two-click End work there. End only a test program, never a real app.
```

> [!NOTE]
> **What's Windows Sandbox?** Windows Sandbox is a clean, throwaway copy of Windows inside your PC: a perfect stand-in for your friend's computer. It comes with Windows 11 Pro. Turn it on under Start › "Turn Windows features on or off" › Windows Sandbox, then restart. Share the zip from Google Drive or OneDrive with just your friend's email.

## 11. 📖 Words you'll hear

- **Tray:** The icons by the clock, at the bottom right of Windows, where small background apps live. New ones start hidden under the little up arrow (^).
- **Memory (RAM):** Your computer's short-term workspace. When it's nearly full, everything slows down.
- **CPU:** The computer's main chip. Its percentage is how busy it is right now.
- **Display scaling:** How much Windows enlarges everything on screen (Settings › System › Display › Scale).
- **Self-contained file:** An app that carries everything it needs inside one file, so it runs without installing anything else.
- **.NET SDK:** Microsoft's free kit for building Windows apps. The AI uses it to build; the finished app doesn't need it.
- **Log:** A diary file the app writes as it works. When something goes wrong, the AI reads it to find out why.
- **AI coding app:** An AI app that can create files and run programs in a folder on your computer, for example Antigravity, Claude Code, Codex or Cursor.
- **Prompt:** A message you type to the AI.
