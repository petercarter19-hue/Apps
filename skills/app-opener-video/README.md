# app-opener-video

An Agent Skill that makes a short opener video for a video about building an app with AI: six glass step cards on a
dark, glowing background, one per build step, in one fixed design. It makes a 10-second silent preview first, then a
version timed to your own voice-over, so each card comes in on the word you say.

It uses [HyperFrames](https://www.npmjs.com/package/hyperframes) (HTML rendered to video) and, for the voice version,
free speech-to-text on your own computer with faster-whisper. No accounts or paid services.

## What's in the folder
- `SKILL.md`: the steps the AI follows.
- `references/timing.md`: how the cards are timed to your voice.
- `build_opener.py`: builds the video project from a small `opener.json`.
- `transcribe.py`: prints every word in your recording with the second it starts.
- `template/`: the fixed design (the fonts are Inter and Outfit, both under the SIL Open Font License 1.1).
- `example/`: a preview and a voice-timed `opener.json` for a voice-typing app.

## Install
Copy this whole folder into your skills folder:

- **Claude Code:** `~/.claude/skills/app-opener-video/`
- **Codex:** `~/.codex/skills/app-opener-video/`

Any AI coding app that reads Agent Skills (a folder with a `SKILL.md`) works the same way. You also need Python 3,
Node.js and ffmpeg; for the voice version, `pip install faster-whisper numpy`.

## Use it
For example:

> Make the opener video for my habit tracker. The six steps are: set up, one message, log a habit, the streak
> calendar, reminders, and the weekly chart.

Then record yourself reading the suggested talk track, and send the recording:

> Here's my voice-over for the opener. Time the cards to it.
