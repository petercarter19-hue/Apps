# done-ping

A Claude Code mod that tells you when Claude finishes a long task, so you can switch to something else while it works.

- A pop-up in Claude Code and a system notification, headed with how long it took ("Claude is done (1m 35s)") and the first line of Claude's answer.
- A short chime on macOS. Windows and Linux have no player Claude Code can use, so there it stays silent.
- "Claude stopped" when a long task ends on an error.
- Quiet for quick replies, for subagents, and when you interrupt.
- `/done-ping` sends a test ping, to check notifications reach you.

## Settings

In `/config` (or the plugin's settings):

- **Ping after (seconds):** only ping when a reply took at least this long. Default 30.
- **Play a chime:** on or off. Default on.

## Install

At the prompt of a Claude Code terminal session:

```
/plugin install done-ping --marketplace petercarter19-hue/apps
```

From a shell:

```
claude plugin install done-ping --marketplace petercarter19-hue/apps
```

In the desktop app: click + next to the prompt box, then Plugins, Add marketplace with `petercarter19-hue/apps`, then Add plugin.
