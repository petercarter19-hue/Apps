# Apps

Small Windows apps you can build yourself with AI, skills for your AI app, and Claude Code mods.

## Windows apps

| App | What it does |
| --- | --- |
| [glance](glance/) | A memory meter by the clock: hover it to see which apps use the most memory, and end a greedy one in two clicks. Comes with a build-it-yourself kit, every prompt, and the finished code. |

## Skills

Skills you can add to Claude Code, Codex or any AI coding app that reads Agent Skills. See [skills](skills/) for the list and how to install them.

## Claude Code mods

| Mod | What it does |
| --- | --- |
| [context-meter](mods/context-meter/) | Shows how full the conversation's context window is, what fills it (system prompt, tools, messages, ...) and where tokens go (input, output, cache reads and writes), in a side panel, the status line and a bar above the prompt. `/meter` prints the report. Includes a Forest color theme. |
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
