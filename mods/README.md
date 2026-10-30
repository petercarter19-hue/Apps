# Claude Code mods

Small add-ons that change how Claude Code looks and behaves. You install them from this repository's plugin marketplace, and you don't write any code.

| Mod | What it does |
| --- | --- |
| [context-meter](context-meter/) | Shows how full the conversation's context window is, your 5-hour limit with the time left, what fills the window, and where tokens go, in the status line and a side panel you open with a click. `/meter` prints the report. |
| [mood-strip](mood-strip/) | A colored strip above the prompt that shows how the session is going (working, bumpy, stuck, done), fading smoothly from one color to the next. `/mood` explains it. |
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
