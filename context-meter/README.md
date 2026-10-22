# context-meter

A Claude Code mod that shows how full the conversation's context window is, what is filling it, and where your tokens go, so you know when to `/compact` before it happens on its own.

- **Status line:** `Context ▓▓▓▓▓▓▓░░░ 72%  ·  $1.84  ·  41 min  ·  5h limit 30%`
- **Bar above the prompt:** green under 60%, yellow from 60%, red from 80% with a nudge to `/compact`. Rest the pointer on it to see what's in the context, this session's tokens, cost and limits.
- **Side panel ("Context meter"):**
  - how full the window is
  - **what's in it**, by category as `/context` lists it (system prompt, tools, memory files, messages, ...)
  - **tokens by kind** for this session, the last reply and today: input, output, cache reads, cache writes, and the share served from cache
  - cost, session length and the 5-hour limit
  - hover a category for its share of the window, or the token rows for what each kind means
- **At the start of each session:** the full report, as a note only you see (Claude does not read it, so it costs no context). Turn it off with the "Full report at start" setting.
- **Under each reply:** one line, always on, e.g. `Context ███░░░░░░░ 27% (271k of 1.0M) · this reply: in 44k, out 800, 90% cached`. Turn it off with the plugin's "Meter under each reply" setting.
- **`/meter`:** three short lines (fill, what's in it, tokens). `/meter full` prints the whole report.
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
