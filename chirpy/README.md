# Chirpy

A free Windows app that lets you talk instead of type, anywhere. Hold Left Alt, talk, let go: your words appear where your cursor is.

Free, no account, English only. Your voice is turned into text on your own PC. The source code is in this folder, and you can [build it yourself](#build-it-yourself).

## Download

Download **Chirpy-Setup.exe** from the [Chirpy 1.0.0 release](https://github.com/petercarter19-hue/Apps/releases/tag/chirpy-v1.0.0) (about 500 MB, because the speech model comes with it, so Chirpy doesn't need to download anything while it sets up).

To check your download is the real file, its SHA-256 fingerprint is:

```
A882B759A9042A79B0C2CFED5F88FD69AC7D3298CE8E8822B9ACC751BD30F337
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

## Build it yourself

The source is in this folder, so you can read it and build the same app and installer. Everything below is for Windows 11 (64-bit) and PowerShell. Nothing here installs or runs Chirpy; a build only writes into the folders you name.

**You need:** 64-bit [Python 3.13](https://www.python.org/downloads/windows/) (a normal install), the [Visual C++ Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe) (Windows may already have it), [Inno Setup 6](https://jrsoftware.org/isinfo.php) (only for the installer), and the speech model files (see below). All commands run from this `chirpy` folder.

1. **Make the runtime**, the Python that ships inside Chirpy, with the pinned packages (each file is checked against its SHA-256; needs internet):

   ```
   .\tools\make_runtime.ps1 -PythonHome "$env:LOCALAPPDATA\Programs\Python\Python313" -Out C:\build\runtime
   ```

2. **Get the speech model.** Download the four files of [parakeet-tdt-0.6b-v2-onnx](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx) that are named in [tools/build_package.py](tools/build_package.py) (`MODEL_FILES`) (`config.json`, `vocab.txt`, `encoder-model.int8.onnx`, `decoder_joint-model.int8.onnx`) into one folder, for example `C:\build\model`. Its license is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

3. **Build the package** (the app, the setup scripts, the runtime and the model in one folder):

   ```
   python .\tools\build_package.py --runtime C:\build\runtime --model C:\build\model --out C:\build\package
   ```

4. **Build the installer** (optional; this is `Chirpy-Setup.exe`, about 500 MB):

   ```
   .\tools\build_chirpy_setup.ps1 -Package C:\build\package -Out C:\build\setup -Iscc "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" -Python C:\build\runtime\python.exe
   ```

   Your result will not have the same fingerprint as the released installer: a build stamps its own times and sizes into the file. To compare, compare the files inside, not the installer.

**Run the app's own tests** (do this after step 3, or on a copy of the runtime: installing pytest into the runtime before building would put pytest in the package) (they use fakes: no microphone, no speech model, no keys pressed, no window on screen, and every file they write stays in a temporary folder):

```
C:\build\runtime\python.exe -s -m pip install pytest
C:\build\runtime\python.exe -s .\tools\run_tests.py
```

**Is this the code in the installer?** The files in [app/](app) are the files that are installed by `Chirpy-Setup.exe` 1.0.0, whose build label is `16d40c5d`. [app/FILES.sha256](app/FILES.sha256) lists the SHA-256 of each one, so you can check an installed copy: after you install Chirpy, run `Get-FileHash` on its files and compare. (`config.json` is Chirpy's starting settings; Chirpy changes the installed copy as you use it.)

### What is in this folder

| Folder | What it holds |
| --- | --- |
| [app/](app) | Chirpy itself, as installed: `voicetool.py` (the program: keys, microphone, speech, typing), `light_keys.py` (the key choices), `light_files.py` (safe saving of your words, fixes, history and settings), `light_start.py`, `commands.py`, `corrections.py`, `spelling.py`, `mic_light.py` (the recording light), `screen_layout.py`, `app_window_win.py`, `settings_window.py`, `ui_api.py`, and `ui/` (the window you see). |
| [installer/](installer) | `Chirpy-Setup.exe`'s script (`chirpy.iss`), the setup and uninstall scripts for the zip route (`Setup.cmd`, `setup.ps1`, `uninstall.ps1`), the read-me that is installed, the pinned package list (`requirements-friend.txt`) and the small file that keeps your own Python packages out. |
| [tools/](tools) | The build scripts from the steps above, and the program that draws the installer's side pictures. |
| [tests/](tests) | The app's own tests. |

Chirpy's code comments were taken out of `app/` when it was built, so the code reads as the installed program does.

## License

Free software under the [MIT license](LICENSE). Third-party components and the speech model's license are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
