# context-meter

A Claude Code mod that shows how full the conversation's context window is, so you know when to `/compact` before it happens on its own.

- **Status line:** `Context ▓▓▓▓▓▓▓░░░ 72%  ·  $1.84  ·  41 min  ·  5h limit 30%`
- **Bar above the prompt:** green under 60%, yellow from 60%, red from 80% with a nudge to `/compact`.
- **Side panel:** a "Context meter" pane with the percentage, cost, session time and 5-hour limit. Type `/meter` to open it.
- **Forest theme:** moss, pine and leaf greens with honey and rust. Pick it with `/theme`.

The meter refreshes after every turn, after a compaction, and once a minute.

## Install

At the prompt of a Claude Code terminal session:

```
/plugin install context-meter --marketplace petercarter19-hue/apps
```

From a shell:

```
claude plugin install context-meter --marketplace petercarter19-hue/apps
```

In the desktop app: click + next to the prompt box, then Plugins, Add marketplace with `petercarter19-hue/apps`, then Add plugin.
