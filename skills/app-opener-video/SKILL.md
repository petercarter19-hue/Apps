---
name: app-opener-video
description: "Make a short opener video for a video about building an app with AI - six glass step cards in one fixed design, first as a 10-second silent preview, then timed to your own voice-over so each card lands on the word you say. Use when an app's build steps are settled (for example after the app-show-report skill), or when asked for an opener, an intro video, an upfront video of what we'll build, or to time the opener to a voice recording."
---

# The app opener video

The opener shows what the video will build: the creator talks through six build steps while glass cards come in, one
per step, exactly on their words. The design is **fixed**, so every opener in a series looks and moves the same; only
the app name, the six steps and the timing change.

Below, "the creator" is the person making the video. Their files go in `<your show folder>/opener/`.

## What you need
- Python 3, Node.js (for `npx`) and `ffmpeg`/`ffprobe` on the PATH.
- For the voice version: `pip install faster-whisper numpy` (free, runs on the computer, no account).
- Everything else is in this folder: `build_opener.py`, `transcribe.py` and `template/`. `<skill folder>` below means
  the folder this file is in.

## What's fixed (never change it per video)
- **The look:** a dark navy background with slowly drifting blue, teal and indigo glows and a faint blue floor grid;
  glass slab cards (three layers deep), blue for motion and focus and green for done.
- **Layout and motion:** exactly **six** cards in one row, each fading in from deep in the scene; the camera pans
  right when the row runs out of room; the current card faces you with a blue ring, then settles back at an angle;
  then green check marks, the loop line and the finish button.
- **The fixed words:** "TODAY'S BUILD"; "Your own `<app>`, step by step"; "Each step: AI builds, you test, "Works.
  Next step.""; "Your own `<app>`, free on your PC".
- **The tools:** HyperFrames (CLI pinned at `hyperframes@0.8.212`) through `build_opener.py` and `template/`. Never
  hand-edit a built project. A design change is a change to `template/steps.tpl.html` for every future opener, made
  only when the creator asks for one.

## What changes per video (all of it goes in `opener.json`)
- `app`: the app's short name for the blanks, at most 14 characters (like "voice app").
- `steps`: exactly six, in build order. Group the build steps into six if there are more.
  - `title`: at most 20 characters.
  - `sub`: the short line, at most 40 characters. Use a list of up to 3 parts when several things are named one after
    another (like `["Notes,", "read aloud,", "transcripts"]`); each part then appears as it's said.
  - `icon`: one of folder, message, mic, wand, letters, layers, doc, mail, calendar, chart, play, globe, search, lock,
    check, bolt, speaker.
  - Step 1 is always the setup (AI and an empty folder; never a brand name), and step 2 is always the one kickoff
    message.
- `voice` and `times`: only for the voice version.

`example/voice-control.preview.json` and `example/voice-control.opener.json` show both shapes.

## 1. The 10-second preview
1. Write `opener/opener.json` with `app` and `steps` only.
2. From the opener folder, build, check and render:
   ```
   python "<skill folder>/build_opener.py" opener.json preview
   cd preview
   npx --yes hyperframes@0.8.212 check --json
   npx --yes hyperframes@0.8.212 render --quality delivery -o "../<App> opener - preview.mp4" --quiet
   ```
3. Look at frames across the whole video (`ffmpeg -ss <t> -i <mp4> -frames:v 1 <png>`), and confirm with `ffprobe`
   that it's 10.0 s.
4. Give the creator the MP4, the six cards written out, and a suggested talk track, one plain line per card (see
   `references/timing.md`). Ask them to record themselves talking it through, and to say where to start and stop if
   there's extra at either end.

## 2. The voice version
When the recording comes in, follow `references/timing.md`: transcribe it, fill in `voice` and `times`, then build
and render with `voice` in place of `preview`. Check every card lands on its word before handing it over.

## Hand it over
- Give the creator the MP4 paths.
- If there's a Show Map (from `app-show-report`), its presenter notes get one line: "Opener: play
  `<App> opener - timed to voice.mp4` at the top."
