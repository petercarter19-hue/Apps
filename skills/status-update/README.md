# Get clear status updates from your AI

A skill that makes your AI report progress the same way every time, in five parts:
- what's being worked on now;
- what's done;
- what's next;
- how long it will take;
- what it needs from you.

Every question comes written out in full with a suggested answer, and the AI shows you the actual thing (the picture, the link, the number) instead of describing it.

**Made for anyone who isn't technical, or who checks in from a phone.** You don't write any code: you add the skill once, then just ask "where are we?"

> [!NOTE]
> **Made with Claude Code, works with any AI app.** We built ours with Claude Code. You can use any AI coding app you like, for example Antigravity, Codex or Cursor: the skill and the requests are the same.

## What it looks like

```text
Waiting on you: one question at the bottom.

1. Being worked on now: the photo app is getting faster previews.
2. Done: the duplicate finder passed all its checks (4:15 PM).
3. Next: you try it on one real folder, then I write the guide.
4. How long: faster previews, likely 6 PM, latest 8 PM. Guide, likely tomorrow.
5. What I need from you: Should it skip photos in shared folders? Suggested: yes.
```

## Start here

1. **Read the kit:** [KIT.md](KIT.md), or the printable [PDF](docs/status-update-kit.pdf). It covers setup, the one message that adds the skill, three practice messages with real replies, and how to make it your own.
2. **Add the skill:** paste [prompts/add-the-skill.txt](prompts/add-the-skill.txt) into your AI app (change the [brackets] first), or copy this folder into your AI app's skills folder.
   - Claude Code: `~/.claude/skills/status-update/`
   - Codex: `~/.codex/skills/status-update/`
   - Any other AI app that reads Agent Skills (a folder with a `SKILL.md`) works the same way.
3. **Start a new chat and ask for a status,** for example: "Give me a status update." or "Where are we, and what's waiting on me?"
4. **That's it.** The AI also uses this format on its own whenever it wraps up or needs a decision from you.

## What's in here

| | |
|---|---|
| [SKILL.md](SKILL.md) | The instructions your AI reads: the five parts, showing the actual thing, deciding what it can, plain words |
| [KIT.md](KIT.md) | The build-it-yourself kit: setup, options, the message that adds it, practice with real replies, making it your own, fixing problems, staying safe |
| [docs/status-update-kit.pdf](docs/status-update-kit.pdf) | The same kit, ready to print |
| [prompts/](prompts/) | Every message to paste, as files you can copy, with [the order to send them](prompts/README.md) |
| [AGENTS.md](AGENTS.md) | Notes your AI app reads to install and test the skill (`CLAUDE.md` and `GEMINI.md` point other apps to it) |

## License

[MIT](../../LICENSE): use it, change it, share it.
