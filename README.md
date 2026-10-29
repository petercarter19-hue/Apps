# Apps

Small Windows apps you can build yourself with AI, and Claude Code mods.

## Windows apps

| App | What it does |
| --- | --- |
| [glance](glance/) | A memory meter by the clock: hover it to see which apps use the most memory, and end a greedy one in two clicks. Comes with a build-it-yourself kit, every prompt, and the finished code. |

## Claude Code mods

| Mod | What it does |
| --- | --- |
| [context-meter](context-meter/) | Shows how full the conversation's context window is, what fills it (system prompt, tools, messages, ...) and where tokens go (input, output, cache reads and writes), in a side panel, the status line and a bar above the prompt. `/meter` prints the report. Includes a Forest color theme. |
| [done-ping](done-ping/) | A notification and a chime when Claude finishes a long task. `/done-ping` sends a test. |
| [todo-panel](todo-panel/) | A to-do list in a side panel that remembers between sessions. `/todo <text>` adds, `/todo done <n>` ticks off, or ask Claude to add things. |

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
