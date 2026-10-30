# Notes for the AI adding this skill

This folder is a skill, not an app: `SKILL.md` (plain text, 37 lines) tells an AI to write every status update, wrap-up or question in five parts (being worked on now, done, next steps, how long, what I need from you), with every question written out with a suggested answer and the actual thing shown. There's nothing to build or install. The person you're helping may never have coded, so use plain words, short answers and sensible defaults, and tell them what you picked.

## Install it

1. Copy `SKILL.md` into a folder named `status-update` in the place where you load skills, without changing it:
   - for all their projects, your user-level skills folder (for example `~/.claude/skills/status-update/` or `~/.codex/skills/status-update/`);
   - for one project only, that project's skills folder (for example `.claude/skills/status-update/`).
2. If you can't use skills, add the text of `SKILL.md` to your always-on instructions for that folder instead (for example `AGENTS.md`).
3. Ask before writing outside the folder you're working in. Then tell the person in one line where it is, and that they should start a new chat so the skill loads.

The person may instead paste `prompts/add-the-skill.txt`, which carries the same text between two marker lines. Save exactly what's between the markers.

## Test it

In a new chat, send these in order and check each reply:

1. `prompts/try-1-practice-status.txt`: a bold first line saying whether anything waits on the person; the five parts in order; a Likely / Latest table with dates in plain words; the preview link; any question written out with a **Suggested:** answer, or one line saying what you decided because their stated goal answered it.
2. `prompts/try-2-whats-waiting.txt`: the color choice is asked (looks are always theirs to decide), with both preview links and a suggested answer, and the first line says it's waiting.
3. `prompts/try-3-one-event-note.txt`: 2 to 4 sentences, not five parts, with anything still waiting in the first line.

Check the real clock before writing any time.

## Rules

- **Don't change `SKILL.md` unless the person asks.** To change how it writes, follow `KIT.md`, section 7 ("Make it your own"). Save a copy first, make one change at a time, and test with the practice messages.
- **Made-up data only in tests.** The practice project is a made-up bake sale; never put real names, passwords or private details into a practice message.
- **The skill only changes how you write.** It never lets you decide looks, publishing, spending, deleting, credentials or messages sent in the person's name: those are always asked.
