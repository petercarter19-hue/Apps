# context-meter

A Claude Code mod that shows how full the conversation's context window is, what is filling it, and where your tokens go, so you know when to `/compact` before it happens on its own.

- **Status line:** `Context ▓▓▓▓▓▓▓░░░ 72%  ·  $1.84  ·  41 min  ·  5h limit 30%`
- **Bar above the prompt:** green under 60%, yellow from 60%, red from 80% with a nudge to `/compact`.
- **Side panel ("Context meter"):**
  - how full the window is
  - **what's in it**, by category as `/context` lists it (system prompt, tools, memory files, messages, ...)
  - **tokens by kind** for this session, the last reply and today: input, output, cache reads, cache writes, and the share served from cache
  - cost, session length and the 5-hour limit
- **`/meter`:** opens the panel and prints the same report as text.
- **Forest theme:** moss, pine and leaf greens with honey and rust. Pick it with `/theme`.

Today's totals are kept between sessions. The meter refreshes after every reply, after a compaction, and once a minute.

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
