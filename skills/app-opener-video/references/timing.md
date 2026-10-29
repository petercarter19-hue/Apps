# Timing the opener to a voice-over

## A suggested talk track
One plain line per card, for example:

> Here's what we're going to do today. We're going to build our own `<app>`, step by step. First, the setup: AI and
> an empty folder. Then one message, the kickoff prompt. Then `<step 3>`... `<step 6>`. Each step, the AI builds it,
> we test it, and say "Works. Next step." And at the end you've got your own `<app>`, free on your PC.

## 1. Find when each word is said
1. Copy the recording to `<your show folder>/opener/voice-over.m4a` (any audio format `ffmpeg` reads works).
2. From the opener folder, run `python "<skill folder>/transcribe.py" voice-over.m4a`.
   - It prints every word with the second it starts.
   - It's free and runs on your own computer (faster-whisper, `small.en` model, downloaded once on first run).

## 2. Fill in `voice` and `times`
- **The clip:** `voice` is `{ "file": "voice-over.m4a", "from": <start s>, "to": <end s> }`. A relative `file` is
  read from the folder `opener.json` is in.
  - Use the creator's start and end if they gave them.
  - Otherwise start 0.25 s before the first word, and end 1 s after the last thing said.
- **Every time in `times` is in video seconds,** which is the recording time minus `from`.
- **The fixed timing rules:**

| Time | When |
|---|---|
| `eyebrow` | 0.25 (or the first word minus 0.1, if that's later) |
| `title[0]` | when the creator says what they're building ("build our own app"), minus 0.1 |
| `title[1]` | "step by step", minus 0.1 |
| `steps[i].at` | the first word of that step's name, minus 0.2 |
| `steps[i].sub` | the first word of the matching line, minus 0.1; a list gets one time per part |
| `checks` | 0.5 s after the start of the last word said about step six |
| `finish` | the "free" / "on your PC" line (the word "free"), minus 0.2 |

- **If a step's line is never said,** set its `sub` to that step's `at` plus 0.15. The line then comes in with the
  card.
- **If a step is skipped or the order changes,** keep the cards in build order, time each card to the moment it's
  named, and tell the creator which one was skipped.

## 3. Build, check and render
1. From the opener folder:
   ```
   python "<skill folder>/build_opener.py" opener.json voice
   cd voice
   npx --yes hyperframes@0.8.212 check --json
   npx --yes hyperframes@0.8.212 render --quality delivery -o "../<App> opener - timed to voice.mp4" --quiet
   ```
2. These check warnings are expected: `first_frame_empty`, `text_below_floor` (the eyebrow), `dead_stretch` while
   the creator is talking between steps, and `timeline_track_too_dense`. In the preview, `line_unread` is also
   expected. Fix anything else.
3. Run `ffprobe`: the video and the audio must both be there, at the right length.
4. Grab a frame 0.6 s after each card's time and after each line's time, and look at every one. Each card and line
   must be on screen as it's said.
5. Give the creator the MP4 with a short list of which words bring in which card, and ask whether the timing is
   right. If something is early or late, move that one time and render again.
