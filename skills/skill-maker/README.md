# Turn the work you do with AI into skills it remembers

Every time you and your AI get something right, there's a lesson in it: the steps that worked, the fix for what broke, the way you like it done. This skill keeps those lessons and turns them into a reusable skill. The next session then does the job right the first time.
- **While you work,** it keeps short notes in a file: what worked, what you corrected, what failed and why.
- **When you're happy and say "make the skill",** it writes a skill from those notes (or from an earlier session you already finished). Failures become checks, and one-time choices stay out.
- **Then it tests the skill** with a fresh helper that never saw your conversation, on a different example, and tells you honestly what passed.

**You don't write any code.** You work with your AI as usual, and say two short things: "use Skill Maker" at the start, and "make the skill" at the end.

> [!NOTE]
> **Made with Claude Code, works with any AI app.** We built ours with Claude Code. You can use any AI coding app you like, for example Antigravity, Codex or Cursor: the skill and the requests are the same.

A sample of the notes it keeps while you work is in [docs/sample-notes.md](docs/sample-notes.md).

## Start here

1. **Add the skill:** copy this folder into your AI app's skills folder.
   - Claude Code: `~/.claude/skills/skill-maker/`
   - Codex: `~/.codex/skills/skill-maker/`
   - Any other AI app that reads Agent Skills (a folder with a `SKILL.md`) works the same way.
2. **At the start of a task,** ask:

   ```text
   Use Skill Maker while we do this. Today we're turning my monthly sales sheet into a one-page summary.
   ```

3. **When the result is right,** say:

   ```text
   That's right. Make the skill.
   ```

   You get a new skill (or an improvement to one you already have), its test results, and the short request that starts it next time. Forgot to say "use Skill Maker" first? Ask afterward: "Turn yesterday's session where we made the sales summary into a skill."

## What's in here

| | |
|---|---|
| [SKILL.md](SKILL.md) | The instructions your AI reads: Capture, Make and Test |
| [references/notes-template.md](references/notes-template.md) | The notes file it keeps while you work |
| [references/test-plan.md](references/test-plan.md) | How it tests a new skill with a fresh helper, and how it records the results |
| [scripts/session_overview.py](scripts/session_overview.py) | A quick look at a finished Claude Code or Codex session: tools used, skills loaded, files written, every message |
| [scripts/find_transcript.py](scripts/find_transcript.py) | Finds a past session's transcript by its id or title (Windows, Claude desktop app or Codex) |
| [docs/sample-notes.md](docs/sample-notes.md) | A sample of the notes it keeps |

## License

[MIT](../../LICENSE): use it, change it, share it.
