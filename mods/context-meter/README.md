# context-meter

A Claude Code mod that shows how full the conversation's context window is, what is filling it, and where your tokens go, so you know when to `/compact` before it happens on its own.

> Made with Claude Code, works with any AI app.

- **Status line, under the prompt:** `Context ▓▓▓▓▓▓▓░░░ 72%  ·  5h limit 45%, 2h 10m left`: how full the window is, and how much of your 5-hour usage limit is used with the time until it resets.
- **"details ▸" link, beside the hint under the prompt:** click it to open the side panel, click again to close it. The space above the prompt is left free for other mods, such as mood-strip.
- **Side panel ("Context meter"), only when you click the link or type `/meter panel`:**
  - how full the window is
  - **what's in it**, by category as `/context` lists it (system prompt, tools, memory files, messages, ...)
  - **tokens by kind** for this session, the last reply and today: input, output, cache reads, cache writes, and the share served from cache
  - the 5-hour limit and the time left
  - **▸ Show details** (or hovering) adds each category's share of the window and what each kind of token means
- **At the start of each session:** the full report, as a note only you see (Claude does not read it, so it costs no context). Turn it off with the "Full report at start" setting.
- **Under each reply:** one line, always on, e.g. `Context ███░░░░░░░ 27% (271k of 1.0M) · this reply: in 44k, out 800, 90% cached`. Turn it off with the plugin's "Meter under each reply" setting.
- **`/meter`:** four short lines (fill, 5-hour limit, what's in it, tokens). `/meter full` prints the whole report and the installed version, `/meter panel` opens the side panel, and `/meter debug` shows what the meter sees (for troubleshooting).
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
