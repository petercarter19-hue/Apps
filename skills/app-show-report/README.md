# app-show-report

An Agent Skill that turns the build of a small app you made with an AI coding app into a two-part report:

1. **The Show Map:** a shooting script for filming (or writing up) the build again with the fewest prompts, with
   what went wrong the first time, the steps to show, and presenter notes.
2. **The build-it-yourself kit:** a standalone, colorful guide with real screenshots, so anyone who has only ever
   typed into an AI chat can build the app for themselves. It never mentions the show.

Both parts come out as HTML pages and PDFs, built from what really happened in your build, with real numbers and
brand-neutral "AI" wording. A finished example kit is at
https://github.com/petercarter19-hue/Apps/tree/main/glance.

## Install
Copy this whole folder into your skills folder:

- **Claude Code:** `~/.claude/skills/app-show-report/`
- **Codex:** `~/.codex/skills/app-show-report/`

Any AI coding app that reads Agent Skills (a folder with a `SKILL.md`) works the same way.

## Use it
Once your app works, ask in the same conversation you built it in, for example:

> My habit tracker works. Make the show report for it: a show map I can film from, and a build-it-yourself kit
> people can follow.

Pairs well with the `app-opener-video` skill for a short opener video of the build steps.
