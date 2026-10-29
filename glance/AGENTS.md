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

- `tools\release-test.ps1` runs 22 checks. Publish `tools\Hog` into `tools\Hog\dist` with the same command first.
- `tools\message-test.ps1` sends Windows' own hover and click messages to the app.

## Rules

- **End is two clicks and ends real programs.** In tests, only ever end the test program (`tools\Hog`), never the person's own apps.
- **Hands off the person's screen.** Tests never move the person's mouse, press keys, or take pictures of their real screen. Use the app's own `--preview` pictures instead.
- **Build new versions in a separate folder.** Quit the running copy first, and swap the new one in only when the tests pass.
- **Never fail silently.** Problems go on the card and into `%LocalAppData%\Glance\log.txt`.
- **To change something,** follow `KIT.md`, section 7 ("Make it your own"). Make one change at a time, and test it before the next.
