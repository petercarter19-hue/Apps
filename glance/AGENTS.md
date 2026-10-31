# Notes for the AI building this app

This folder is a finished, working Windows app: a memory meter that sits by the clock (C#, .NET 10, WinForms, no main window). The person you're helping may never have coded, so use plain words, short answers and sensible defaults, and tell them what you picked.

## Build it

1. If `dotnet --version` doesn't show 10 or later, install Microsoft's free .NET 10 SDK from https://dotnet.microsoft.com/download (ask before installing).
2. From this folder, run:

   ```
   dotnet publish Glance.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
   ```

3. The app is `dist\Glance.exe`, one file that runs without installing anything else. Start it, then tell the person to look by the clock: Windows hides new icons under the little up arrow (^), and they can drag the number onto the taskbar.

Opening the app again restarts it; `Glance.exe --quit` closes it.

## Test it

- `dotnet run --project tools\Tests -c Release` runs the End-safety tests (made-up processes and owners; nothing real is ended).
- `tools\release-test.ps1` runs 26 checks. Publish `tools\Hog` into `tools\Hog\dist` with the same command first.
- `tools\message-test.ps1`, `tools\hover-test.ps1` and `tools\smoke-test.ps1` send Windows' own hover and click events to a test copy of the app.
- `tools\safety-lint.ps1` checks the test scripts for anything that touches the person's screen, pointer, keys or real Glance. Run it after changing a script.
- All scripts start a **test copy** through `tools\TestLib.ps1`: `GLANCE_TEST_NS` and `GLANCE_TEST_DIR` give it its own lock, settings folder, startup entry, shortcut and window titles. Use that library for any new test; never start, quit or stop the app any other way.

## Rules

- **End is two clicks and ends real programs.** In tests, only ever end the test program (`tools\Hog`), never the person's own apps. End only ever ends processes owned by the account Glance runs as; if Windows won't say who owns one, it isn't ended.
- **Hands off the person's screen.** Tests never move the person's mouse, press keys, or take pictures of their real screen. Use the app's own `--preview` pictures instead. The one test that needs a real pointer lives in `tools\disposable-vm-only\` and only runs in Windows Sandbox.
- **Hands off the person's own Glance.** Tests never stop a program by name and never touch the real Glance settings, startup entry or Start menu shortcut. They stop only the exact processes they started (number and start time).
- **"Remove" deletes only Glance's own files:** its settings, log and preview pictures, plus its shortcut and startup entry. Never the folder's other contents, never documents or photos.
- **Build new versions in a separate folder.** Quit the running copy first, and swap the new one in only when the tests pass.
- **Never fail silently.** Problems go on the card and into `%LocalAppData%\Glance\log.txt`.
- **To change something,** follow `KIT.md`, section 7 ("Make it your own"). Make one change at a time, and test it before the next.
