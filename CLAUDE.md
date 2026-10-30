# Apps: Claude Code mods

This repository is a Claude Code plugin marketplace (`.claude-plugin/marketplace.json`, name `apps`) holding the mods, each in its own folder under `mods/`. A mod is a plugin of function hooks: a TypeScript module the engine loads and calls through `$`, the engine interface.

The owner works on Windows, in the Claude desktop app and in PowerShell. They are not a programmer: explain in plain words, give exact commands to paste, and say what they will see.

## The mods

| Mod | Version | What it does |
| --- | --- | --- |
| `context-meter` | 0.8.1 | Context fill and the 5-hour limit with time left in the status line, the side panel on `/meter panel`, a line under each reply, the full report as a notice at session start. It leaves the band above the prompt to mood-strip. Tallies tokens by kind (input, output, cache read, cache write) per session, last reply and day (`$.store`, keyed `day:YYYY-MM-DD`). Ships a Forest theme in `themes/forest.json`. |
| `done-ping` | 0.3.0 | After a main-loop reply of `minSeconds` (default 60) or more: a toast, `$.ui.notify`, and one soft chime (`$.audio.play` on macOS, PowerShell `Media.SoundPlayer` on Windows, detected by `OS=Windows_NT`). `/done-ping` sends a test. |
| `mood-strip` | 0.7.0 | The band above the prompt (`AbovePrompt`) as a full-width `Svg` in three parts: now (or Claude's latest thinking line, from a `turn.step` stream hook), the trail of recent steps, and the plan (`update_plan`) as dots. Moods: working, long (past the usual time), waiting (AskUserQuestion, ExitPlanMode), win (push, commit, PR, install), bumpy, stuck, done, stopped, else the time of day. Redraws only when the words or mood change; fades are SMIL animations inside the Svg. Never shows the user's own message. `/mood`, `/mood demo`. |
| `todo-panel` | 0.1.0 | A to-do list kept in `$.store` (key `items`), shown in a pane, driven by `/todo` and by two tools Claude calls when asked (`mcp__todo-panel__add`, `mcp__todo-panel__list`). |

Each mod folder holds `.claude-plugin/plugin.json`, `hooks/hooks.json` (one module path), `hooks/register.ts(x)`, a `types/index.d.ts` contract when it keeps `$.state` values, a `README.md`, and `tests/*.test.ts(x)`.

## Working on a mod

The API's authority is the declaration file the engine writes beside a loaded mod, `<mod>/.claude-plugin/types/claude-code/index.d.ts` (ignored by git), and the `plugin-authoring` skill. Load that skill before changing a mod.

Check every change before committing (in PowerShell, `claude` is `claude.cmd`; see Windows below):

```
claude.cmd plugin validate <mod folder>
claude.cmd plugin test <mod folder>
npx tsc -p <mod folder>
```

`plugin validate` on the repository root checks the marketplace too.

### Seeing a change without restarting

An installed plugin only changes after `plugin update` and a restart. While developing, load the mod from this folder instead, which hot-reloads each save when the turn ends:

1. Turn off the installed copy, so it does not run twice: `claude.cmd plugin disable context-meter@apps`
2. Start Claude Code on the folder: `claude.cmd --plugin-dir C:\path\to\apps\context-meter` (repeat `--plugin-dir` for several). A session the desktop app starts reads the same folders from the `CLAUDE_CODE_PLUGIN_DIRS` environment variable (absolute paths, separated by `;` on Windows).
3. When done, `claude.cmd plugin enable context-meter@apps` and release as below.

### Releasing

1. Bump `version` in the mod's `plugin.json` (and `VERSION` in `context-meter/hooks/register.tsx`, which `/meter full` and `/meter debug` print).
2. Update the mod's `README.md`, and the table in the root `README.md` and `marketplace.json` when a mod is added or its summary changes.
3. Validate and test as above, commit, push to `main`.
4. On the PC: `claude.cmd plugin marketplace update apps`, then `claude.cmd plugin update <mod>@apps`, then fully restart the app. A new mod: `claude.cmd plugin install <mod>@apps`.

## What the engine allows (learned building these)

- A helper that takes `$` must be a top-level function declaration (or a const bound to one), never a closure inside `register`: the module does not load otherwise.
- `turn.complete` returning `{ ...result, text }` with a different text shows that text under the answer; the transcript is not rewritten. context-meter's line under each reply uses this, and it is plain text (no hover, no clicks).
- `$.session.append({ message: { type: 'system', content: [{ type: 'text', text }] } })` adds a notice the person sees and the model never reads (no context cost). A `session.append` hook in a test must call `next(e)`.
- `$.state` survives a reload and `session.start` runs again on each reload, so a once-per-session action keeps a flag in state (context-meter's `reported`).
- Hover: a keyed `Box` scopes hover for what is beneath it; a hidden `Box` (`display: 'none'`, `hover: { display: 'flex' }`) must not carry a key of its own, or the tree is refused. Whether a surface draws hover is the surface's business; clicks (`Button` `onPress`) always reach the mod, so context-meter's bubble opens on click.
- On the desktop app, raw hex colors on `Box`/`Text` are not painted (a white bar); theme keys are. An `Svg` paints exact colors: give it `width={4000}` and an inner `style="width:100%"` to fill the row. Redrawing an `Svg` every second flashes, so redraw only on change and fade with SMIL `<animate>` (a negative `begin` resumes mid-fade). A `Button` holds only text, not an `Svg`, and a `PromptHint` button was not clickable on desktop.
- Right-click is only reachable through a `Client` element's surface module, which the desktop app does not run. Do not promise right-click.
- `$.audio.play` plays only on macOS. `$.ui.notify` writes through terminal channels (iTerm2, kitty, Ghostty, the bell); in the desktop app the toast is what shows.
- `$.process.run` runs a program on the host (no shell), which is how done-ping plays sound on Windows.
- A cloud session viewed in the app may have no surface attached (`$.session.surfaces()` empty), so panes, bands and the status line do not show there; command output and the line under replies do.
- A mod's tools are deferred behind ToolSearch by default, which keeps them out of the context until used.

## Open items

- **Line below the composer:** the owner reported the context meter's line below the composer disappeared on their PC. Not reproduced. `/meter debug` prints the version, attached screens, the status text last set and the last error; start there.
- **Meter styles:** the owner was to choose which of the status line, the bar and the line under replies to keep. Not decided yet.
- **On the PC, not yet confirmed:** the click bubble, hover in the pane, the Windows chime, and that `todo-panel` installed (its install output was never seen; `claude.cmd plugin list` shows it).

## Windows

- PowerShell refuses `claude` (the npm `claude.ps1` shim) while scripts are disabled. Use `claude.cmd`, or Command Prompt, or `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` if the owner chooses to.
- Install scope is `user`, so the desktop app's local sessions load what the terminal installs.
