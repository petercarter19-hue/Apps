# Notes for the AI building this app

This folder is a finished, working Windows app that finds duplicate photos (C#, .NET 10, WPF, Magick.NET for reading every photo type). The person you're helping may never have coded, so use plain words, short answers and sensible defaults, and tell them what you picked.

## Build it

1. If `dotnet --version` doesn't show 10 or later, install Microsoft's free .NET 10 SDK from https://dotnet.microsoft.com/download (ask before installing).
2. From this folder, run:

   ```
   powershell -ExecutionPolicy Bypass -File tools\install.ps1
   ```

   It builds the one-file app in `dist-next`, checks it, swaps it into `app\PhotoTwins.exe` and adds the Start menu entry "PhotoTwins". With `-DontClose` it never closes a copy that's open: it builds and checks, and leaves the swap for later.
3. Open it from the Start menu. `PhotoTwins.exe --quit` asks a running copy to close.

## Test it

```
dotnet test tests\PhotoTwins.Tests
```

About 470 tests. They only ever use photos they make themselves under `test-work\` (git-ignored); the test file system refuses to touch anything outside it. The app's pictures of itself (light and dark, 100%, 125% and 150%) are drawn off-screen into `test-work\pictures\`, with no real screen, mouse or keyboard. Removal tests use the real Recycle Bin on test photos and put everything back.

`PhotoTwins.exe --selfcheck <data folder> <picture.png>` builds the screen, draws it to a picture without showing a window, and exits.

**The showcase** (`ShowcaseTests`) only runs when `PHOTOTWINS_SHOWCASE` names an output folder. It makes a made-up photo library that looks real, then draws the kit's pictures and short video clips of the app in use, off-screen. It needs the free ffmpeg (`PHOTOTWINS_FFMPEG` = its full path, or on the PATH).

## Where things live

- `src\PhotoTwins.Core`: scanning, fingerprints, matching, the keeper rule, shared and cloud detection, removal (the safety check, `FileMutations`, the Undo record), the cache, settings. No screen code.
- `src\PhotoTwins`: the window, the glass look (`Glass.cs`, `Theme.xaml`) and the self-check.
- `tests\PhotoTwins.Tests`: all tests. `fixtures\` holds two HEIC test photos made once by `tools\make-heic-fixtures.py` (the free image reader can read HEIC but not write it).
- The app's own data (settings, remembered fingerprints and pictures, the Undo record, logs) is in `%LocalAppData%\PhotoTwins`, never in this folder.

## Rules

- **The person's photos are precious.** Never remove, move, rename or change a real photo while building or testing. Tests use only made-up photos in `test-work\`.
- **Removal is always: the person's click, a safety check, the Recycle Bin (or the "Duplicates to delete" folder), and Undo.** Every group keeps at least one photo. Shared-folder copies stay. Look-alikes are never removed in bulk. Keep all of that when you change anything.
- **Hands off the person's screen.** Tests never move the mouse, press keys, or take pictures of the real screen. Use the app's own off-screen pictures instead.
- **Build new versions in a separate folder.** `tools\install.ps1` does this; swap in only when the tests pass.
- **Never fail silently.** Problems go on screen and into `%LocalAppData%\PhotoTwins\logs`.
- **To change something,** follow `KIT.md`, section 7 ("Make it your own"). Make one change at a time, and test it before the next.
