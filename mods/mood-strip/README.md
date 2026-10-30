# mood-strip

A Claude Code mod that puts a thin colored strip above the prompt box, so you can tell at a glance how the session is going. The colors fade smoothly from one mood to the next.

> Made with Claude Code, works with any AI app.

| Color | Mood | What it says |
| --- | --- | --- |
| Purple | Working, smoothly | `● Working · 1m 12s` |
| Amber | Bumpy: 2 or more errors or retries in 10 minutes | `≈ Bumpy · 3 retries in 10 min · working 4m` |
| Red | Stuck: 3 or more failures in a row | `⚠ Stuck · 4 failures in a row · maybe try a different approach` |
| Green | Done (stays 3 minutes) | `✓ Done in 2m 40s · 1 retry` |
| Pink-red | Stopped early | `■ Stopped after 1m 5s` |
| Time of day | Waiting for you | `☀ Good morning · 8:14 AM` |

Between turns the strip follows the time of day: morning gold, afternoon blue, evening rose and night gray, each drifting slowly toward the next as the hours pass.

- An "error" is a step Claude tried that failed or was refused (a command that errored, an edit that didn't apply). A success ends a run of failures.
- Only the main conversation counts; helper agents running in the background don't change the mood.
- `/mood` says what the strip is showing and what each color means.

The strip shows in the Claude desktop app and in the terminal, wherever the band above the prompt is drawn.

## Install

At the prompt of a Claude Code terminal session:

```
/plugin install mood-strip --marketplace petercarter19-hue/apps
```

From a shell:

```
claude plugin install mood-strip --marketplace petercarter19-hue/apps
```

In the desktop app: click + next to the prompt box, then Plugins, Add marketplace with `petercarter19-hue/apps`, then Add plugin.
