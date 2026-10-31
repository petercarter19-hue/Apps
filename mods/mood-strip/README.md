# mood-strip

A Claude Code mod that puts one full-width colored strip right above the prompt box. At a glance it tells you how the session is going: what the AI is doing or thinking right now, a trail of its last few steps, and how far through its plan it is. The color changes with the mood (working, waiting on you, a win, bumpy, stuck, done) and always fades slowly from one to the next.

> [!NOTE]
> **Made with Claude Code, works with any AI app.** We built ours with Claude Code. You can use any AI coding app you like to build or change it, for example Antigravity, Codex or Cursor: the messages and steps are the same. The strip itself is a Claude Code mod, so it runs inside Claude Code (the desktop app or the terminal).

![The strip above the prompt box: "Editing index.html" on the left, a helper and the last steps in the middle, plan dots and "Step 2 of 5: Build the page" on the right](docs/mood-strip.png)

## What it shows

**Three parts, edge to edge:**
- **Left, right now:** "Reading app.js", "Running npm", "Thinking…", "Waiting for your answer". Whole commands never show: only a well-known program's name ("Running npm"), otherwise "a command"; builds show as "Building…", "✓ Build passed" or "✗ Build failed, fixing…"; tools read like "Using read file". File, helper, skill, tool or step names that hold a path, a web address, a secret, a health, money or identity subject, a letter outside the Latin alphabet, or any word you've typed in this session, answers, the reasons you give for turning a step down and what you type after a slash command included (everyday words like "page" or "test" aside, but never names, nicknames, months, days or place codes) show as "a file", "a helper", "a skill", "a tool" or "Step 2 of 5". A command with several parts (&&, ||, ;, a pipe) only says "Ran a command" unless every part is known to have run; tests piped into another program end as "Tests finished", with no pass or fail. Work started in the background shows as "Tests started" or "Helper started".
- **Middle, the story so far:** the last few steps ("Read app.js › Edited app.js › Tests ✓"), how it's going ("2 retries so far", "9 min, past your usual 3 min"), helpers at work, and **a short sentence of the AI's thinking, live**, in quotes at the start of the middle part (complete sentences only, up to 60 characters). It's on by default and filtered like everything else, and you can turn it off (see Settings).
- **Right, the plan:** one dot per step, filled when done, and the current step ("Step 2 of 5: Build the page"), from the AI's plan, task list or to-do list.

Each part has its own room (about 48, 75 and 48 characters); anything longer is cut at a word, with "…", and fades softly at the part's right edge. After a reload, step, file, helper and tool names stay hidden until the mod has read back what you typed earlier in the session, or until your next message.

**The colors:**

| Mood | Color | When |
|---|---|---|
| Working | Purple | The AI is at work. Warms toward amber when a task runs well past your usual length |
| Waiting | Pink | It asked you a question or wants a plan approved |
| Win | Green | Tests passed, something installed, saved, pushed, a pull request opened |
| Bumpy | Orange | A step failed, or 2 or more retries in 10 minutes. Steps you declined or interrupted, and searches or tests that simply find nothing, don't count |
| Stuck | Red | The same step failed 3 times in a row: "Might need a new approach" |
| Done | Teal | "Done in 4m 10s" with a one-line recap ("tests passing", "build passing", files changed). Stays teal for 3 minutes, then fades to the time of day in the last few seconds |
| Stopped | Coral | You pressed stop ("Stopped after 1m 12s"), the reply ran into an error ("Ran into an error after 2m 5s"), or Claude declined ("Claude declined after 40s") |
| Idle | Time of day | "Good morning · 9:30 AM", today's tasks and minutes of work, how the last task went. Morning gold, afternoon blue, evening rose, late night gray |

**It never shows your own messages**, only what the AI is doing, and it sends nothing anywhere. There's nothing to click.

**What it keeps:** It keeps only today's counts (tasks, work time, the last task), on your computer, and clears them when the day changes: at midnight, or, if Claude Code wasn't running then, the first time it starts on the new day. It never keeps your messages. The task count and work time (how "4 tasks today" and your usual task length work) sit in one small file of its own, in the `plugins/store` folder inside your Claude Code settings folder (named like `mood-strip_apps-....json`). You can delete that file any time; counting then starts again from zero.

## Commands

- **`/mood`:** what the strip is showing (and whether the demo is playing), what each color means, the last problem it hit ("Last problem: none" when all is well), the screen width it was given ("Screen width reported: 120 columns"), and the installed version.
- **`/mood demo`:** plays every look with made-up examples, a few seconds each, then goes back to now. The best way to see it all. It plays between tasks; during a task it says "A task is running, so the demo would hide what Claude is doing. Try /mood demo between tasks." Run it again any time; a real reply ends it.

In the terminal the strip is a colored line with the same words, painted in each mood's color at once (the terminal can't fade).

## Settings

- **Show Claude's thinking** (`showThinking`, **on** by default): the short thinking line in the middle. It's filtered the same way as everything else the strip shows: your own words, file paths, web addresses, secrets, long numbers, health, money, legal, housing and identity words, anything about "he" or "she", host names, and anything that looks like a person's or company's name are hidden.

  > [!WARNING]
  > It shows a short line of what Claude is thinking. It hides your own words, file paths and secrets, but if you stream or share your screen and want nothing extra shown, turn it off in the plugin's settings.

  The filters can't catch everything: thinking often restates what you gave Claude, and a name or detail Claude read in a file can still show. To turn it off, type `/config` and switch off the "Show Claude's thinking" row, or from a shell run `claude plugin configure mood-strip@apps`. Then type `/reload-plugins`. Switch the row back on the same way.

## Install

At the prompt of a Claude Code terminal session:

```
/plugin install mood-strip --marketplace petercarter19-hue/apps
```

From a shell:

```
claude plugin install mood-strip --marketplace petercarter19-hue/apps
```

In the desktop app: click + next to the prompt box, then Plugins, Add marketplace with `petercarter19-hue/apps`, then Add plugin. Start a new session and type `/mood demo`.

It pairs well with [context-meter](../context-meter/), which keeps the context and 5-hour numbers in the status line below the prompt and leaves the space above it to the strip.

## Build your own

You don't need to write any code. The kit walks you through building your own version with AI, step by step:

| | |
|---|---|
| [KIT.md](KIT.md) | The build-it-yourself kit: what you need, setup, options, each step, making it your own, fixing problems, staying safe |
| [docs/build-it-yourself-kit.pdf](docs/build-it-yourself-kit.pdf) | The same kit, ready to print |
| [prompts/](prompts/) | Every message to paste, in [the order to send them](prompts/README.md) |
| [hooks/](hooks/), [types/](types/), [tests/](tests/) | The finished code and its tests, to compare with yours |
| [AGENTS.md](AGENTS.md) | Notes your AI app reads to check and test the code (`CLAUDE.md` and `GEMINI.md` point to it) |

Every lesson from our own build is already a line in the kickoff message, so your first try should go more smoothly than ours did.

## License

[MIT](../../LICENSE): use it, change it, share it.
