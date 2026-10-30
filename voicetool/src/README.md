# VoiceTool

Talk instead of typing, anywhere on your computer. Everything runs on this computer: your voice and your text are not
sent anywhere.

## The keys
- **Hold Left Alt**, talk, let go: your words are pasted where your cursor is. Double-tap Left Alt to keep the mic on
  hands-free, and tap it once more to stop. Esc cancels.
- **Hold Left Windows** and say what you want: "make this shorter" (with text selected), "write a thank-you note
  to Sam", "what does this mean", "open Notepad", "mute", "read this to me", "note: buy milk", "journal: today I...".
  Double-tap it for a longer instruction; tap again to stop.
- **Shift-tap Left Windows** with a misheard word selected: type what it should be. VoiceTool fixes it and
  remembers.
- A small light at the bottom of the screen shows when it is listening. Soft beeps: higher means listening, a double
  beep means hands-free is on, lower means done.

The first start takes about 10 seconds. VoiceTool beeps twice when it is ready. Open the VoiceTool window from the
Start menu or the microphone icon by the clock (it may be under the ^ arrow).

## What is on, and what is off
**On:** dictation, the command key, typed fixes, your word list and learned fixes, named styles and app rules, voice
shortcuts, history, voice notes saved to a folder, the journal, read aloud, computer controls (volume, media, lock).

**Off until you turn it on in Settings:**
- **Meeting notes.** Starting one always asks first: "Before recording, make sure everyone on the call agrees to be
  recorded." The other people's audio is deleted as soon as the notes are written.
- **Email and calendar (Claude).** Needs Claude Code installed and signed in with your own Claude plan. It only creates
  drafts; you press Send.
- **Spoken answers**, and **Save recordings**.
- **Azure OpenAI**: an advanced option for the "Keep everything on this computer" switch. It needs an Azure endpoint
  in config.json, so for most people it stays on.

**Not included:** transcribing links, questions about the screen, and messages to an AI team.

If the tidy-up model hasn't finished downloading, VoiceTool still types what you said (without the tidy-up) and the
Home page shows a **Finish setup** button.

## Where things are
Everything is in the folder VoiceTool is installed in (by default `%LOCALAPPDATA%\VoiceTool`).
- `config.json`: your settings. Most of them are switches in the VoiceTool window.
- `words.txt`: names and words that must be spelled exactly (one per line), also on the Words and fixes page.
- `corrections.json`: mishearings VoiceTool has learned.
- `style.md`: how you like to write, also on the My style page.
- `history.jsonl`: what you dictated. It stays on this computer.
- `voicetool.log`: what VoiceTool has been doing, if something goes wrong.
- Voice notes go to `Documents\Voice notes` and meeting notes to a `Meeting notes` folder, unless you choose another
  folder in Settings.
- `Start VoiceTool.cmd` and `Stop VoiceTool.cmd` start and stop it.

## How to uninstall
Use **Uninstall VoiceTool** in the Start menu (or double-click `Uninstall.cmd` in the installed VoiceTool folder; the
folder you unzipped is not the installed one). It asks whether to keep a copy of your words, fixes, style and history in
`Documents\VoiceTool backup`, then removes VoiceTool, its start-up entry and its Start menu shortcuts.
