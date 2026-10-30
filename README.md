# Apps

Small Windows apps you can build yourself with AI, skills for your AI app, and Claude Code mods.

## Windows apps

| App | What it does |
| --- | --- |
| [glance](glance/) | A memory meter by the clock: hover it to see which apps use the most memory, and end a greedy one in two clicks. Comes with a build-it-yourself kit, every prompt, and the finished code. |
| [phototwins](phototwins/) | A duplicate photo finder: it finds the same photo saved more than once, shows each group side by side with the best copy starred, and clears out the extras safely, to the Recycle Bin with Undo. Comes with a build-it-yourself kit, every prompt, and the finished code. |
| [voicetool](voicetool/) | Talk instead of typing, anywhere: hold Left Alt, talk, and your words land where your cursor is. Hold the Windows key to rewrite, write, fix a word, take meeting notes or read aloud. Runs on free AI models on your own PC. Comes with a build-it-yourself kit, every prompt, and the finished code. |

## Skills

Skills you can add to Claude Code, Codex or any AI coding app that reads Agent Skills. See [skills](skills/) for the list and how to install them.

## Claude Code mods

| Mod | What it does |
| --- | --- |
| [context-meter](mods/context-meter/) | Shows how full the conversation's context window is and your 5-hour limit with the time left in the status line, what fills the window and where tokens go in a side panel (`/meter panel`). `/meter` prints the report. Includes a Forest color theme. |
| [mood-strip](mods/mood-strip/) | A full-width colored strip above the prompt that shows how the session is going: what Claude is doing or thinking right now, a trail of its last steps, and the plan's progress as dots. It changes color for working, waiting on you, wins, bumpy, stuck and done, fading slowly between them, and greets you by time of day when idle. `/mood demo` plays made-up scenes. |
| [done-ping](mods/done-ping/) | A notification and a chime when Claude finishes a long task. `/done-ping` sends a test. |
| [todo-panel](mods/todo-panel/) | A to-do list in a side panel that remembers between sessions. `/todo <text>` adds, `/todo done <n>` ticks off, or ask Claude to add things. |

## Install

In the Claude desktop app (Code tab, a local session): click + next to the prompt box, then Plugins, Add marketplace with `petercarter19-hue/apps`, then Add plugin and pick each mod you want.

At the prompt of a Claude Code terminal session:

```
/plugin install context-meter --marketplace petercarter19-hue/apps
/plugin install done-ping --marketplace petercarter19-hue/apps
/plugin install todo-panel --marketplace petercarter19-hue/apps
```

Answer `y` to add the marketplace, then press Enter for the user scope.

From a shell:

```
claude plugin install context-meter --marketplace petercarter19-hue/apps
claude plugin install done-ping --marketplace petercarter19-hue/apps
claude plugin install todo-panel --marketplace petercarter19-hue/apps
```

To use the Forest colors in a terminal, run `/theme` and pick **Forest**.
