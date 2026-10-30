# Notes for the AI building this mod

This folder is a finished, working Claude Code mod: a plugin whose hooks module (`hooks/register.tsx`) draws a full-width colored strip above the prompt box and keeps it in step with the session. It only runs inside Claude Code (the desktop app or the terminal). The person you're helping may never have coded, so use plain words, short answers and sensible defaults, and tell them what you picked.

## Files

- `.claude-plugin/plugin.json`: name, version, description.
- `hooks/hooks.json`: points to `register.tsx`.
- `hooks/register.tsx`: everything the strip does. `VERSION` here must match `plugin.json`.
- `types/index.d.ts`: the saved values (atoms) the hooks read and write.
- `tests/strip.test.tsx`: the tests.

## Check and test

From this folder (on Windows, `claude.cmd` instead of `claude`):

```
claude plugin validate .
claude plugin test .
```

`validate` warns that `CLAUDE.md` at the plugin root isn't loaded as project context. That's expected: `CLAUDE.md` and `GEMINI.md` are here for AI coding apps opened in this folder, not for the installed mod.

Every test must pass before a change is installed. Tests use the testing kit's fake clock and fake store (`mock.clock`, `mock.store`), never the person's real settings or sessions.

## To see it while building

Load the folder as a development mod that reloads on change, and turn off any installed copy of `mood-strip` first, or the old copy keeps drawing and the new one seems missing. Development mods load when a reply ends; a new session always picks them up. `/mood` names the running version, and `/mood demo` plays every look.

## Rules the desktop app taught us

- **Colors:** the desktop app does not paint raw color codes on `Box` or `Text` (you get a white bar). Use the theme's named colors there, or draw with `Svg`, which it paints exactly. The terminal uses `Box backgroundColor` and `Text`.
- **No flashing:** redraw the `Svg` only when what it shows changes. Do fades inside the picture with SMIL (`<animate values keyTimes dur begin="-Nms" fill="freeze">`), never by redrawing every second.
- **Full width:** give the `Svg` component `width={4000}`, and the picture `width="4000"` plus `style="width:100%"`. Without it the picture fills only part of the space.
- **Clicks:** only `Button` takes presses, and a `Button` can't hold an `Svg`. The strip has nothing to click.
- **Privacy:** never put the person's own message text on the strip, and never store it. A test checks this.
- **Commands:** never the whole command line (it can hold keys, passwords or private paths). A well-known program shows by name ("Running npm"); anything else is "a command". Text written into files (heredocs, here-strings) is never read as a command.
- **Builds:** "Building…", "✓ Build passed", "✗ Build failed, fixing…"; the recap says "build passing". A build never counts as a test.
- **Tool names** read like "Using read file".
- **Thinking line:** opt-in. The `showThinking` option ("Show Claude's thinking") is OFF by default, and its description warns that thinking can put private details on screen (names, health, money). When on: `“sentence…”` at the start of the middle part, complete sentences only, up to 60 characters. Drop any sentence that echoes the person's own words, says "the user", quotes something, holds a path, URL, email or token, has 5 or more digits, looks like a phone number or street address, or uses health, money or identity words. To turn it on: `/config` (the option is a row) or `claude plugin configure mood-strip@apps`, then `/reload-plugins`.
- **Private-looking names:** file, helper, skill and step names that look private fall back to "a file", "a helper", "a skill" or "Step N of M".
- **Long text:** each part caps at about 48 / 75 / 48 characters, cut at a word with "…", with a soft fade at its right edge; never mid-letter.
- **Done:** teal holds for the whole 3 minutes, then fades to the time-of-day colors in the last 2.5 seconds.
- **After a reload mid-task:** step, file and helper names stay hidden until the next message.
- **`/mood`** also reports "Screen width reported: N columns".
- **Failures:** a tool the person declined or interrupted is not a failure, and neither is exit code 1 from grep, test or diff.
- **Endings:** "✓  Done in X", "■  Stopped after X" (stop pressed), "✗  Ran into an error after X" (API error), "■  Claude declined after X" (refusal). The step count shows once. Background work shows "Tests started" or "Helper started".
- **Terminal:** no fades and no amber drift there; paint each mood's color at once.
- **Plans:** the plan tool, TodoWrite and TaskCreate/TaskUpdate all feed the dots.
- **Demo:** `/mood demo` plays 25 looks between tasks only; during a task it answers "A task is running, so the demo would hide what Claude is doing. Try /mood demo between tasks." It can be rerun safely, and a real turn ends it. `/mood` says when the demo is playing.
- **Contrast:** words on every mood color pass 4.5 to 1 at full strength (working #726BC7, long #A66815, waiting #C34D74, win #54831D, stuck #D14544, done #198664, stopped #C6522C).
- **Escaping:** file names and thinking text go into the picture's XML, so escape `<`, `&` and quotes.
- **Streaming:** the `turn.step` hook passes every chunk on unchanged, watches only the main conversation (`agentId` undefined), and must never let a failed store write stop the stream.

## Rules for working

- **Build changes in a copy** and swap them in only when `validate` and `test` pass.
- **Never fail silently.** Every hook passes its event on even when the strip can't update, and the problem is saved as `lastError`, which `/mood` shows as "Last problem".
- **To change something,** follow `KIT.md`, section 7 ("Make it your own"). One change at a time, tested before the next.
