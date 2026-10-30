# Claude Code mods

Small add-ons that change how Claude Code looks and behaves. You install them from this repository's plugin marketplace, and you don't write any code.

| Mod | What it does |
| --- | --- |
| [context-meter](context-meter/) | Shows how full the conversation's context window is, what fills it, and where tokens go, in a side panel, the status line and a bar above the prompt. `/meter` prints the report. |
| [done-ping](done-ping/) | A notification and a chime when Claude finishes a long task. `/done-ping` sends a test. |
| [todo-panel](todo-panel/) | A to-do list in a side panel that remembers between sessions. `/todo <text>` adds, `/todo done <n>` ticks off. |

## Install

In the Claude desktop app (Code tab, a local session): click + next to the prompt box, then Plugins, Add marketplace with `petercarter19-hue/apps`, then Add plugin and pick each mod you want.

From a terminal:

```
claude plugin install context-meter --marketplace petercarter19-hue/apps
```

## License

[MIT](../LICENSE): use them, change them, share them.
