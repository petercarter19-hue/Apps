# Chirpy

A free Windows app that lets you talk instead of type, anywhere. Hold Left Alt, talk, let go: your words appear where your cursor is.

Free, no account, English only. Your voice is turned into text on your own PC.

## Download

Download **Chirpy-Setup.exe** from the [latest release](https://github.com/petercarter19-hue/Apps/releases/latest) (about 500 MB, because the speech model comes with it, so Chirpy doesn't need to download anything while it sets up).

To check your download is the real file, its SHA-256 fingerprint is:

```
639E423A1988A267486ADF2487711A22A837E8FF467BF0CD58CEFA83880A55B9
```

In PowerShell: `Get-FileHash .\Chirpy-Setup.exe` (the same line is in [Chirpy-Setup.exe.sha256](Chirpy-Setup.exe.sha256)).

## Install

1. Double-click **Chirpy-Setup.exe**, accept the license (it is the MIT license) and follow the window. No administrator needed.
2. Windows may show a blue **Windows protected your PC** screen that says "Unknown publisher". Chirpy isn't signed yet, and Windows can show this for programs it doesn't recognize. If you got Chirpy from this page, click **More info**, then **Run anyway**.
3. Chirpy installs to your user folder, adds itself to the Start menu and starts. It beeps twice when it is ready (about 10 seconds).

Needs Windows 11 (64-bit) and a microphone. It may work on current Windows 10 but isn't tested there. Only one voice tool can listen for keys at a time, so close any other dictation app first.

## Use it

- **Hold Left Alt, talk, let go:** the text is typed where your cursor is.
- **Double-tap Left Alt** for hands-free; tap once more to stop. **Esc** cancels.
- **Fix a misheard word:** select it, hold Shift and tap Left Alt, type what it should be and press Enter. Chirpy types the fix and remembers it.
- Open Chirpy from the Start menu or the bird icon by the clock for Home, History, Words & fixes and Settings. Settings lets you pick other keys.

## Privacy

- Speech is recognized on your PC. There is no account and no online service for it; the speech model, your word list, your fixes and your history all live on your PC.
- The one thing that reaches the internet is the **Send feedback** button, which opens the feedback page in your browser. You decide what to write.
- To type what you said, Chirpy puts the text on the Windows clipboard for about a second, then tries to put back what you had copied. It asks Windows to keep that text out of clipboard history and clipboard sync, but that request doesn't always work. If you have clipboard sync turned on (Windows Settings > System > Clipboard), Windows may keep your dictated text in clipboard history or send it to your other devices through your Microsoft account.
- History keeps what you dictated so you can search it. Open History and choose **Clear history** to erase it.

## Remove it

Windows Settings > Apps > Chirpy > Uninstall. It asks whether to keep your words, fixes, history and settings.

## Send feedback

Bugs, comments or requests: [open a new issue](https://github.com/petercarter19-hue/Apps/issues/new).

## License

Free software under the [MIT license](LICENSE). Third-party components and the speech model's license are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
