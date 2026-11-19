# Chirpy

A free Windows app that lets you talk instead of type, anywhere. Hold Left Alt, talk, let go: your words appear where your cursor is.

Free, no account, English only. Your voice is turned into text on your own PC. The source code is in this folder, and you can [build it yourself](#build-it-yourself).

**Build it yourself with AI, no coding:** read the [build kit](KIT.md) (or the printable [PDF](docs/build-it-yourself-kit.pdf)) and paste the message in [prompts/kickoff.txt](prompts/kickoff.txt). It works with any AI coding app.

## Download

Download **Chirpy-Setup.exe** from the [Chirpy 1.1.0 release](https://github.com/petercarter19-hue/Apps/releases/tag/chirpy-v1.1.0) (501,244,105 bytes, about 501 MB, because the speech model comes with it, so Chirpy doesn't need to download anything while it sets up).

To check your download is the real file, its SHA-256 fingerprint is:

```
00E4E7141E84C126E3A044143AFF854B4A45380ADB35C2700838097D6F29D346
```

In PowerShell: `Get-FileHash .\Chirpy-Setup.exe` (the same line is in [Chirpy-Setup.exe.sha256](Chirpy-Setup.exe.sha256)).

## What's new in 1.1

- **Word fixes remember their neighbors.** If you teach Chirpy that “saw 6.1” should be “Sol 6.1,” it can use the nearby number to make that fix and leave “I saw the dog” alone.
- **Help with hold-key clashes.** Chirpy can spot some hold-key clashes. When it detects one, it shows a card once with a button to open the key setting.
- **Chirpy learns the words you say often.** From your own History on this PC, it notices word pairs you say a lot; when it hears a rare word where one of your usual words belongs (and the two look alike), it types your usual word. It never changes your own fixes, your word list or numbers, and it is off when History is off. Clear history erases what it learned.
- **Setup works with a very long Windows PATH.**

## Install

1. Double-click **Chirpy-Setup.exe**, accept the license (it is the MIT license) and follow the window. No administrator needed.
2. Windows may show a blue **Windows protected your PC** screen that says "Unknown publisher". Chirpy isn't signed yet, and Windows can show this for programs it doesn't recognize. If you got Chirpy from this page, click **More info**, then **Run anyway**.
3. Chirpy installs to your user folder, adds itself to the Start menu and starts. It beeps twice when it is ready (about 10 seconds).

Needs Windows 11 (64-bit) and a microphone. It may work on current Windows 10 but isn't tested there. If another dictation app uses the same hold key, close it or pick a different key in Chirpy’s Settings.

## Use it

- **Hold Left Alt, talk, let go:** the text is typed where your cursor is.
- **Double-tap Left Alt** for hands-free; tap once more to stop. **Esc** cancels.
- **Fix a misheard word:** select it, hold Shift and tap Left Alt, type what it should be and press Enter. Chirpy types the fix and remembers it.
- Open Chirpy from the Start menu or the bird icon by the clock for Home, History, Words & fixes and Settings. Settings lets you pick other keys.

## Privacy

- Speech is recognized on your PC. There is no account and no online service for it; the speech model, your word list, your fixes and your history all live on your PC.
- The one thing that reaches the internet is the **Send feedback** button, which opens the feedback page in your browser. You decide what to write.
- To type what you said, Chirpy puts the text on the Windows clipboard for about a second, then tries to put back what you had copied. It asks Windows to keep that text out of clipboard history and clipboard sync, but that request doesn't always work. If you have clipboard sync turned on (Windows Settings > System > Clipboard), Windows may keep your dictated text in clipboard history or send it to your other devices through your Microsoft account.
- History keeps what you dictated so you can search it. Chirpy also keeps local word-pair counts to help recognize words you often say together. Open History and choose **Clear history** to erase the history and its counts. Fixes you taught Chirpy stay in Words & fixes.

## Remove it

Windows Settings > Apps > Chirpy > Uninstall. It asks whether to keep your words, fixes, history and settings.

## Send feedback

Bugs, comments or requests: [open a new issue](https://github.com/petercarter19-hue/Apps/issues/new).

## Build it yourself

**With AI, no coding:** the [build kit](KIT.md) walks you through it, and [prompts/kickoff.txt](prompts/kickoff.txt) is the message to paste into your AI app.

**From the finished code:** the steps below build the app and installer from this public source.

Everything below is for Windows 11 (64-bit) and PowerShell. Nothing here installs or runs Chirpy; a build only writes into the folders you name.

**You need:** 64-bit [Python 3.13](https://www.python.org/downloads/windows/) (a normal install), the [Visual C++ Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe) (Windows may already have it), [Inno Setup 6](https://jrsoftware.org/isinfo.php) (only for the installer), and the speech model files (see below). All commands run from this `chirpy` folder.

1. **Make the runtime**, the Python that ships inside Chirpy, with the pinned packages (each file is checked against its SHA-256; needs internet):

   ```
   .\tools\make_runtime.ps1 -PythonHome (python -c "import sys; print(sys.base_prefix)") -Out .\build\runtime
   ```

2. **Get the speech model.** Download the four files of [parakeet-tdt-0.6b-v2-onnx](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx) that are named in [tools/build_package.py](tools/build_package.py) (`MODEL_FILES`) (`config.json`, `vocab.txt`, `encoder-model.int8.onnx`, `decoder_joint-model.int8.onnx`) into one folder, for example `.\build\model`. Its license is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

3. **Build the package** (the app, the setup scripts, the runtime and the model in one folder):

   ```
   python .\tools\build_package.py --runtime .\build\runtime --model .\build\model --out .\build\package
   ```

4. **Build the installer** (optional, about 501 MB). Make `ISCC.exe` available on PATH or pass its location to `-Iscc`.

   ```
   .\tools\build_chirpy_setup.ps1 -Package .\build\package -Out (Join-Path $PWD 'build\setup') -Iscc (Get-Command ISCC.exe).Source -Python .\build\runtime\python.exe
   ```

   Your result will not have the same fingerprint as the released installer: a build stamps its own times and sizes into the file. To compare, compare the files inside, not the installer.

**Run the app's own tests** (do this after step 3, or on a copy of the runtime: installing pytest into the runtime before building would put pytest in the package) (they use fakes: no microphone, no speech model, no keys pressed, no window on screen, and every file they write stays in a temporary folder):

```
.\build\runtime\python.exe -s -m pip install pytest
.\build\runtime\python.exe -s .\tools\run_tests.py
```

**Is this the code in the installer?** The files in [app/](app) come from `Chirpy-Setup.exe` 1.1.0, build `014a42c3`. Every file here except FILES.sha256 is byte-for-byte the file the installer puts on your PC; [app/FILES.sha256](app/FILES.sha256) lists their SHA-256, so you can check an installed copy. (`config.json` is Chirpy's starting settings; Chirpy changes the installed copy as you use it.)

### What is in this folder

| Folder | What it holds |
| --- | --- |
| [app/](app) | Chirpy's app files: `voicetool.py` (the program: keys, microphone, speech, typing), `light_keys.py` (the key choices), `light_files.py` (safe saving of your words, fixes, history and settings), `light_start.py`, `commands.py`, `corrections.py`, `usual_words.py`, `spelling.py`, `mic_light.py` (the recording light), `screen_layout.py`, `app_window_win.py`, `settings_window.py`, `ui_api.py`, and `ui/` (the window you see). |
| [installer/](installer) | `Chirpy-Setup.exe`'s script (`chirpy.iss`), the setup and uninstall scripts for the zip route (`Setup.cmd`, `setup.ps1`, `uninstall.ps1`), the read-me that is installed, the pinned package list (`requirements-friend.txt`) and the small file that keeps your own Python packages out. |
| [tools/](tools) | The build scripts from the steps above, and the program that draws the installer's side pictures. |
| [tests/](tests) | The app's own tests. |

Chirpy's code comments were taken out of `app/` when it was built, so the code reads as the installed program does.

## License

Free software under the [MIT license](LICENSE). Third-party components and the speech model's license are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
