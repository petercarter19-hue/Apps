"""Everyday computer commands, from a FIXED list only: never free-form shell commands from speech.
Shared by the Mac and the PC: this table is the list; each machine has a small file that does each action
(system_mac.py on the Mac; the PC uses virtual keys, LockWorkStation and its screenshot folder).

Hold the Command key and say "mute", "volume up", "set the volume to 30", "screenshot this window", "lock the screen",
"pause", "next song", "close this window" (the app's own "save changes?" question still appears).
"""

# id: (what the router hears, what the card says afterwards)
ACTIONS = {
    "mute": ("mute the sound", "Muted."),
    "unmute": ("unmute the sound", "Sound on."),
    "volume_up": ("turn the volume up", "Volume up."),
    "volume_down": ("turn the volume down", "Volume down."),
    "set_volume": ("set the volume to a number from 0 to 100 (put the number in \"value\")", "Volume set."),
    "screenshot_window": ("take a screenshot of the window in front", "Screenshot saved."),
    "lock_screen": ("lock the screen", "Locked."),
    "play_pause": ("play or pause music or video", "Play/pause."),
    "next_track": ("skip to the next track", "Next track."),
    "previous_track": ("go back to the previous track", "Previous track."),
    "close_window": ("close the window in front (its own save prompt still appears)", "Closed."),
}


def router_lines():
    """The list as the Command key's router sees it."""
    return "\n".join(f'  - "{k}": {v[0]}' for k, v in ACTIONS.items())


def run(platform, action, value="", context=None):
    """Do one listed action. Returns the sentence for the card. Anything not on the list is refused."""
    if action not in ACTIONS:
        raise ValueError("that isn't one of VoiceTool's computer commands")
    if action == "set_volume":
        try:
            level = max(0, min(100, int(float(str(value).strip().rstrip("%")))))
        except ValueError:
            raise ValueError("I didn't catch the volume number")
        platform.set_volume(level)
        return f"Volume set to {level}."
    if action == "screenshot_window":  # the window that was in front when the key was pressed
        context = context or {}
        extra = {"hwnd": context["hwnd"]} if context.get("hwnd") else {}  # PC: the exact window (the Mac's context has none)
        result = platform.screenshot_window(context.get("pid"), **extra)
    else:
        result = getattr(platform, action)()
    return result if isinstance(result, str) and result else ACTIONS[action][1]
