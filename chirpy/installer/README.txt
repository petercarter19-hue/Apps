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
  tries to save it for next time. The Learned card tells you what was saved. If the save fails, a card says so.
  Fix "saw" next to a number, and Chirpy fixes it next to a number on that same side. For example, "saw 6.1" becomes
  "Sol 6.1" and "I saw the dog" stays as you said it. From the latest dictation, the fix saves only which side had a
  number. To tie a fix to a particular word, select the neighboring word too and leave it in the correction, as in
  "ask saw" to "ask Sol". If you also select a number right on the other side, as in "ask saw 6.1", only the number
  condition applies. For example, "send saw 6.2" becomes "send Sol 6.2". With neither condition, the fix works everywhere.
  Words & fixes shows the condition when you edit a fix.
- Open Chirpy from the Start menu or the Chirpy bird icon by the clock for Home, History, Words & fixes and Settings. Settings
  lets you pick other keys for talking and for fixing a word: Left Alt, Right Alt, Right Ctrl, Caps Lock or F13 to F24 (Left
  Alt is where you start). Home and Settings both have the dark / light switch.

Only one voice tool can listen for keys at a time: Setup won't install, and Chirpy won't start, while the everyday VoiceTool
runs. If the everyday VoiceTool starts while Chirpy runs, Chirpy says so and closes.

If Chirpy notices that another app is using its hold key, a card says "Another app is using" that key, with a button that
opens the key setting. Pick a different hold key in Settings, or close the other app. Chirpy never takes the key from the
other app and never stops it.

The window needs Microsoft Edge WebView2 Runtime, which comes with Windows 11 and current Windows 10. Without it, a small
settings window opens instead (dictation and teaching a word still work).

What stays on your PC
Chirpy itself doesn't connect to the internet. The one thing that reaches it is the Send feedback button, which opens
Chirpy's feedback page in your web browser; you decide what to write there. Your word list, fixes, history and settings are
kept in Chirpy's own folder on this PC. Its log (voicetool.log) says what Chirpy did, and it also records the words you
teach Chirpy (a word you add, or a fix and what it should be). It doesn't record the text you dictate.
History keeps what you dictated so you can search it. To erase it, open History and choose Clear history (it asks first,
then deletes it from this PC).
Chirpy also learns from your History which words you say next to each other. It counts, on this PC only, how many times you
said each word and the pairs of words you said one after the other in the final text you dictated. When Chirpy first
builds these counts, it uses up to the last 2 MB of existing History. It skips commands and dictations changed by a usual
word swap. The counts are in usual_words.json next to your fixes. Word pairs can reveal parts of a phrase. This file is
limited in size (the least-said counts are dropped first) and is never sent anywhere. When Chirpy hears a rare word where
you usually say a similar word, it can type your usual word instead. A pair normally needs to occur three times.
Clear history removes the History and its counts. Fixes you taught Chirpy stay in Words & fixes, where you can forget them.
To stop saving History and learning or applying usual words, set "keep_history": false in config.json. Turning History off leaves
existing History and counts on this PC until you choose Clear history. Adjust "usual_words_after" to change the learning threshold.
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
