# Notes for the AI building this app

This folder is a finished, working Windows app that dims everything except the window you are working in (C#, .NET 10, Windows Forms, Win32 calls). The person you are helping may never have coded, so use plain words, short answers and sensible defaults, and tell them what you picked.

## Build it

1. If `dotnet --version` does not show 10 or later, install Microsoft's free .NET 10 SDK from https://dotnet.microsoft.com/download (ask before installing). To build the installer too you need the free Inno Setup 6 (https://jrsoftware.org/isinfo.php, ask before installing).
2. From this folder:

   ```
   powershell -ExecutionPolicy Bypass -File tools\build.ps1
   ```

   It runs the tests, publishes the one-file app into `dist\` and builds the installer into `installer\out\`. With `-SkipTests` it skips the tests. Nothing here starts the app or installs it.
3. `Spotlight.exe --quit` asks a running copy to close.

## Test it

```
dotnet run --project tests -c Release
```

About 75 headless checks of the real source files (the cut-out geometry, which window gets the light, the glide, the hotkey, the settings file, the quick bar's layout) against made-up screens and windows. They open no window, show no overlay and never touch the real screen, mouse, keyboard, hotkey, registry or settings (a test copy gets its own names through the `SPOTLIGHT_TEST_NS` environment variable).

## Where things live

- `src\Core`: the decisions, with no Windows calls (geometry, rules, follow logic, the animation, the hotkey, options and the quick bar layout).
- `src`: the Windows parts: the dimming windows (`OverlayWindow`, `Engine`), the tray (`TrayApp`), the settings window (`SettingsForm`), the area picker (`AreaPicker`), the quick bar (`QuickBarWindow`) and the settings file (`Settings`).
- `installer\Spotlight.iss`: the per-user installer script.
- The app's own data is in `%LocalAppData%\Spotlight`, never in this folder.

## Rules

- **Hands off the person's screen.** Tests never take pictures of the real screen, press keys or move the mouse. Try the real app yourself in Windows Sandbox, not on the person's PC.
- **Keep the lessons:** the dimming windows stay one pixel row short of full screen (a full-cover window makes Windows switch on Do Not Disturb); the dimming is one window region per monitor, not a redrawn bitmap; follow windows with Windows event hooks, not polling; settings are read, changed and saved as one locked step and written by swapping in a whole new file; every helper window closes when the tray copy quits.
