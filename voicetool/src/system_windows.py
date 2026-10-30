"""The PC's half of system_actions.py: each listed computer command, done the Windows way. Nothing else can run from here.
(The Mac's is system_mac.py, with the same function names.) VoiceTool calls these only after the Command key has been
released, so the keys it sends are never mixed up with a held Win key.

- mute / unmute / set_volume: the speakers' own volume control through pycaw (exact). Without pycaw: the mute key (a
  toggle) and volume set by keys (50 presses down, then up to the level: one press is 2%).
- volume up / down, play-pause, next, previous: the keyboard's own media keys (VK_VOLUME_*, VK_MEDIA_*), so Windows shows its
  usual volume pop-up.
- lock_screen: Windows' LockWorkStation. close_window: Ctrl+W, so the app asks about unsaved changes as usual.
- screenshot_window: the window that was in front, saved in Pictures\\Screenshots as "VoiceTool <time>.png".
"""
import ctypes

import actions_windows

VK_VOLUME_MUTE, VK_VOLUME_DOWN, VK_VOLUME_UP = 0xAD, 0xAE, 0xAF
VK_MEDIA_NEXT, VK_MEDIA_PREV, VK_MEDIA_PLAY_PAUSE = 0xB0, 0xB1, 0xB3
VK_CONTROL, VK_W = 0x11, 0x57
VOLUME_STEPS = 5  # one volume key press is 2% on Windows; five is a clear step (10%)
_NO_PYCAW = object()


def press_key(vk, times=1, extended=True):
    """Press and release a key (media and volume keys are "extended" keys), as a keyboard does."""
    import win32api
    import win32con
    flags = win32con.KEYEVENTF_EXTENDEDKEY if extended else 0
    for _ in range(times):
        win32api.keybd_event(vk, 0, flags, 0)
        win32api.keybd_event(vk, 0, flags | win32con.KEYEVENTF_KEYUP, 0)


def _lock_workstation():
    ctypes.windll.user32.LockWorkStation()


def _volume_control():
    """The speakers' volume control (pycaw), or None if pycaw isn't installed."""
    try:
        from pycaw.pycaw import AudioUtilities
        return AudioUtilities.GetSpeakers().EndpointVolume
    except Exception:
        return None


def _with_volume(action):
    """Run action(volume control) on this thread (Windows' audio objects need COM started on it). _NO_PYCAW if pycaw is
    missing."""
    try:
        import comtypes
        comtypes.CoInitialize()
    except Exception:
        comtypes = None
    try:
        control = _volume_control()
        if control is None:
            return _NO_PYCAW
        return action(control)
    finally:
        if comtypes is not None:
            comtypes.CoUninitialize()


def volume():
    """(level 0-100, muted?) or None without pycaw."""
    out = _with_volume(lambda c: (round(c.GetMasterVolumeLevelScalar() * 100), bool(c.GetMute())))
    return None if out is _NO_PYCAW else out


def mute():
    if _with_volume(lambda c: c.SetMute(1, None)) is _NO_PYCAW:
        press_key(VK_VOLUME_MUTE)  # without pycaw the key can only toggle


def unmute():
    if _with_volume(lambda c: c.SetMute(0, None)) is _NO_PYCAW:
        press_key(VK_VOLUME_MUTE)


def volume_up():
    press_key(VK_VOLUME_UP, VOLUME_STEPS)


def volume_down():
    press_key(VK_VOLUME_DOWN, VOLUME_STEPS)


def set_volume(level):
    """Set the volume to 0-100 (and unmute). Exact with pycaw; otherwise by keys: all the way down, then up in 2% steps."""
    level = max(0, min(100, int(level)))

    def apply(control):
        control.SetMasterVolumeLevelScalar(level / 100, None)
        control.SetMute(0, None)
    if _with_volume(apply) is _NO_PYCAW:
        press_key(VK_VOLUME_DOWN, 50)
        press_key(VK_VOLUME_UP, round(level / 2))


def play_pause():
    press_key(VK_MEDIA_PLAY_PAUSE)


def next_track():
    press_key(VK_MEDIA_NEXT)


def previous_track():
    press_key(VK_MEDIA_PREV)


def lock_screen():
    _lock_workstation()


def close_window():
    """Ctrl+W in the window in front (the Command key has been let go): the app's own "save changes?" still appears."""
    import win32api
    import win32con
    win32api.keybd_event(VK_CONTROL, 0, 0, 0)
    win32api.keybd_event(VK_W, 0, 0, 0)
    win32api.keybd_event(VK_W, 0, win32con.KEYEVENTF_KEYUP, 0)
    win32api.keybd_event(VK_CONTROL, 0, win32con.KEYEVENTF_KEYUP, 0)


def screenshot_window(pid=None, hwnd=None):
    """The window that was in front when the key was pressed, saved where Windows keeps screenshots."""
    path = actions_windows.save_window_screenshot(pid, hwnd)
    if path is None:
        raise RuntimeError("there's no window in front to capture")
    return f"Screenshot saved: {path.name} ({path.parent.name})."
