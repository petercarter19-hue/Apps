# Publish and check

## Deliverables
1. **The full report (both parts)** as one HTML page, with Part 2 linked from the contents and starting on a new page
   when printed. Its title is the app or episode name plus "Show Map".
2. **The kit on its own** as a second HTML page: the same file with Part 1 removed, a title of two to four words, and
   the cover's top border dropped.
3. **Two PDFs** in `<your show folder>`: `<App> Show Map.pdf` and `<App> kit.pdf`.
4. **The opener video** (optional): once the Show Map's steps are settled, follow the `app-opener-video` skill. Make
   the 10-second preview right away, and the version timed to the creator's voice once they record one. Add its line
   to the presenter notes (G).
5. **Hand over** the two pages, the two PDFs and any opener MP4s to the creator. Keep everything private; the creator
   decides when and where to share the kit.

## Print to PDF
Wrap the page in `<!doctype html><html lang="en"><head><meta charset="utf-8"></head><body>...</body></html>` if it
isn't a full page already, then print it with a Chromium browser in headless mode. On Windows, Edge works:

```
"<path to msedge.exe>" --headless=new --disable-gpu --no-pdf-header-footer --virtual-time-budget=8000 "--print-to-pdf=<pdf>" file:///<html>
```

Chrome takes the same flags. Then check the PDF's text came out clean, for example with Python's `pypdf`.

## Sweeps before publishing
- **Kit sweep:** search the kit's text for `camera`, `episode`, `presenter`, `show map`, `viewers`, the creator's
  name, the private name of the creator's own copy of the app, and any lone brand name. It must find nothing.
- **Brand sweep (both parts):** search for "Claude", "claude.ai", "ChatGPT", "Codex", "Antigravity", "Cursor" and
  "Gemini". Every hit must sit in What you need, Get set up or the glossary, inside an alphabetical list of several
  apps, or in the "Made with ..." note and its on-camera line. A lone brand anywhere else is a fail.
- **Privacy sweep:** search both parts and every screenshot for email addresses, account names, keys, personal folder
  paths and real people's names.

## Check before you hand it over
- The kickoff copies cleanly, and every [bracket] is explained in "Choose your options".
- Every number comes from the real build.
- The kit sweep comes back empty, and the brand and privacy sweeps pass.
- Spelling is American everywhere.
- Both PDFs open and their text is clean.
- The kit is colorful, and every screenshot shows in the PDF (count its images with `pypdf`; a step without a picture
  is a gap). No real personal data shows in any picture.
- Each step's test is something a beginner can actually do in under two minutes.
