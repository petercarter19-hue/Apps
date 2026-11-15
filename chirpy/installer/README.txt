Chirpy

Talk anywhere on your Windows PC and your words are typed where the cursor is. Your voice is turned into text on this PC, with
no account and no online service: the speech model, your word list, your fixes and your history all live here.

English only. Chirpy's speech model understands English and nothing else. It also writes American spellings (color, not
colour).

Install
- Chirpy-Setup.exe: double-click it, accept the license (it is the MIT license) and follow the window. Windows may show a
  blue "Windows protected your PC" screen that says "Unknown publisher". Chirpy isn't signed yet, and Windows can show this
  for programs it doesn't recognize. If you got Chirpy from a place you trust, click "More info", then "Run anyway".
- Setup.cmd (the zip package): extract the whole zip first, then double-click Setup.cmd.
Either way Chirpy installs to your user folder (no administrator needed), adds itself to the Start menu and starts. It beeps
twice when it is ready (about 10 seconds).

No two beeps? Chirpy tells you in a message when it can't start (for example when antivirus software has removed a file it
needs). If you hear no beeps after about a minute and no message appeared, open Chirpy from the Start menu: if its window
opens, it is running (the beeps can also be switched off in Settings). If you see a message, or nothing happens, run Setup
again. It puts the program files back and keeps your words, fixes, history and settings.

Use it
- Hold Left Alt, talk, let go: the text is typed where your cursor is.
- Double-tap Left Alt for hands-free; tap once more to stop. Esc cancels.
- Select a misheard word, hold Shift and tap Left Alt, type what it should be and press Enter. Chirpy types the fix and
  tries to save it for next time. If Chirpy tries to save it and the save fails, a card says so.
- Open Chirpy from the Start menu or the Chirpy bird icon by the clock for Home, History, Words & fixes and Settings. Settings
  lets you pick other keys for talking and for fixing a word: Left Alt, Right Alt, Right Ctrl, Caps Lock or F13 to F24 (Left
  Alt is where you start). Home and Settings both have the dark / light switch.

Only one voice tool can listen for keys at a time: Setup won't install, and Chirpy won't start, while the everyday VoiceTool
runs. If the everyday VoiceTool starts while Chirpy runs, Chirpy says so and closes.

The window needs Microsoft Edge WebView2 Runtime, which comes with Windows 11 and current Windows 10. Without it, a small
settings window opens instead (dictation and teaching a word still work).

What stays on your PC
Chirpy itself doesn't connect to the internet. The one thing that reaches it is the Send feedback button, which opens
Chirpy's feedback page in your web browser; you decide what to write there. Your word list, fixes, history and settings are
kept in Chirpy's own folder on this PC. Its log (voicetool.log) says what Chirpy did, and it also records the words you
teach Chirpy (a word you add, or a fix and what it should be). It doesn't record the text you dictate.
History keeps what you dictated so you can search it. To erase it, open History and choose Clear history (it asks first,
then deletes it from this PC).
To type what you said, Chirpy puts the text on the Windows clipboard for about a second, then tries to put back what you had
copied. Some things you copy, such as pictures, may not come back.
Chirpy asks Windows to keep that text out of clipboard history and clipboard sync, but that request doesn't always work. When
it doesn't, Windows may keep your dictated text in clipboard history, or send it to your other devices through your Microsoft
account if you have turned on clipboard sync across your devices, depending on your clipboard settings (Windows Settings >
System > Clipboard).

Remove it
- Installed with Chirpy-Setup.exe: Windows Settings > Apps > Chirpy > Uninstall. It asks whether to keep your words, fixes,
  history and settings.
- Installed with Setup.cmd: run "Uninstall Chirpy.cmd" in the folder it was installed to.

Chirpy is free software under the MIT license: LICENSE.txt. Third-party components and the speech model's license:
THIRD-PARTY-NOTICES.md. Both are installed beside Chirpy.
