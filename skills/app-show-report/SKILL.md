---
name: app-show-report
description: "Make the two-part report for a small app you built with an AI coding app. Part 1 is the Show Map, a shooting script for filming or writing up the build again with the fewest prompts. Part 2 is a standalone, colorful build-it-yourself kit with real screenshots that anyone can follow to make the app their own. Use when an app you built with AI is working and you want a shooting script, a show map, a tutorial, a how-to-make-this-easily guide, or a build kit for viewers or customers, or when updating an existing report."
---

# The app show report

You built a small, useful app with an AI coding app, and it works. This skill turns what really happened in that
build into a report with two parts:

- **Part 1, the Show Map (for the creator):** the shooting script for filming the build again, or for writing it up
  as a tutorial. It rebuilds the app with the fewest prompts, lists what went wrong the first time, gives the steps
  to show, and adds presenter notes.
- **Part 2, the build-it-yourself kit (for viewers and customers):** everything someone needs to make the app their
  own. It stands alone and never mentions the show, the video, the camera, the creator or the creator's own copy of
  the app.

The reader of both parts is a beginner who has only ever typed into an AI chat. A finished example kit is public at
https://github.com/petercarter19-hue/Apps/tree/main/glance (see its `KIT.md` and `README.md`). Copy its level of
detail.

## 1. Gather the facts first (never invent them)
- **The real build:** every message the creator sent to the AI and roughly how many there were; every decision made
  late (keys, names, services, style); every bug and what fixed it; what the creator loved or hated. Use the build
  conversation itself, your AI app's saved session history, and any notes kept during the build.
- **The app now:** its README and feature notes, its settings, which models it uses and how big they are, and what
  each feature needs.
- Ask the creator for anything you can't find. A number you can't trace to the real build stays out.

## 2. Follow the rules for both parts
Read `references/rules.md` before writing. In short:
- Plain words for beginners; no code for the reader to write.
- Say "AI", never steer to one brand. Where AI coding apps are named, name several in alphabetical order
  ("Antigravity, Claude Code, Codex or Cursor, or another") and say any of them works.
- Free models on the reader's own computer; anything paid is marked "Optional".
- Fewest prompts: one kickoff message settles every decision, then "Works. Next step."
- Real numbers from the real build.
- The safety lines stay in every kickoff message.
- Nothing private: no emails, account names, keys, personal paths or real people's data.
- American English spelling.

## 3. Write Part 1 and Part 2
Follow the section lists in `references/outline.md`: sections A to G plus the numbered steps for the Show Map, and
eleven sections for the kit. Make the kit colorful, with real screenshots of the app, never mock-ups.

## 4. Publish and check
Follow `references/finish.md`:
1. Save the full report (both parts) and the kit on its own as HTML pages.
2. Print both to PDF and check every screenshot made it in.
3. Run the kit sweep and the brand sweep, and fix every hit.
4. Once the Show Map's steps are settled, make the opener video with the `app-opener-video` skill, if you have it.
5. Give the creator both files and both PDFs. Nothing is shared or published for them.

To update an existing report, change the same files in place. Never make a second copy.
