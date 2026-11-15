

import ctypes
import logging

log = logging.getLogger("voicetool")

BASE_DPI = 96
MDT_EFFECTIVE_DPI = 0
MONITOR_DEFAULTTOPRIMARY = 1
E_ACCESSDENIED = -2147024891

def make_dpi_aware(windll=None):

    dll = windll if windll is not None else ctypes.windll
    result = "none"
    try:
        if dll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
            result = "per-monitor v2"
    except Exception as e:
        log.info("SetProcessDpiAwarenessContext not available (%s)", e)
    if result == "none":
        try:
            hr = dll.shcore.SetProcessDpiAwareness(2)
            if hr == 0:
                result = "per-monitor"
            elif hr == E_ACCESSDENIED:
                result = "already set"
        except Exception as e:
            log.info("SetProcessDpiAwareness not available (%s)", e)
    if result == "none":
        try:
            if dll.user32.SetProcessDPIAware():
                result = "system"
        except Exception as e:
            log.info("SetProcessDPIAware not available (%s)", e)
    log.info("DPI awareness: %s", result)
    return result

def foreground_monitor(win32gui=None, win32api=None, windll=None):

    if win32gui is None:
        import win32gui
    if win32api is None:
        import win32api
    hwnd = win32gui.GetForegroundWindow()
    if not hwnd:
        return None
    monitor = win32api.MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY)
    work = tuple(int(v) for v in win32api.GetMonitorInfo(monitor)["Work"])
    return work, monitor_dpi(monitor, windll)

def primary_monitor(win32api=None, windll=None):

    if win32api is None:
        import win32api
    monitor = win32api.MonitorFromPoint((0, 0), MONITOR_DEFAULTTOPRIMARY)
    return tuple(int(v) for v in win32api.GetMonitorInfo(monitor)["Work"]), monitor_dpi(monitor, windll)

def work_area_and_dpi():

    for look in (foreground_monitor, primary_monitor):
        try:
            found = look()
            if found is not None:
                return found
        except Exception as e:
            log.info("could not ask which monitor to use: %s", e)
    return (0, 0, 1920, 1040), BASE_DPI

def monitor_dpi(monitor, windll=None):

    dll = windll if windll is not None else ctypes.windll
    try:
        x, y = ctypes.c_uint(), ctypes.c_uint()
        if dll.shcore.GetDpiForMonitor(ctypes.c_void_p(int(monitor)), MDT_EFFECTIVE_DPI, ctypes.byref(x), ctypes.byref(y)) == 0 \
                and x.value:
            return int(x.value)
    except Exception:
        pass
    return BASE_DPI

def dpi_scale(dpi):

    try:
        d = float(dpi)
    except (TypeError, ValueError):
        return 1.0
    return min(max(d, BASE_DPI), 960.0) / BASE_DPI

def scaled(n, dpi):

    return int(round(n * dpi_scale(dpi)))

def font_pixels(points, dpi):

    return -max(1, int(round(points * BASE_DPI / 72.0 * dpi_scale(dpi))))

def tk_geometry(w, h, x, y):

    return f"{int(w)}x{int(h)}+{int(x)}+{int(y)}"

def tk_position(x, y):
    return f"+{int(x)}+{int(y)}"

def corner_geometry(work, dpi, width, height, margin=20, min_width=0):

    left, top, right, bottom = work
    w = max(int(width), scaled(min_width, dpi))
    h = int(height)
    m = scaled(margin, dpi)
    return w, h, max(left, right - w - m), max(top, bottom - h - m)

def centered_above_bottom(work, dpi, width, height, lift=120):

    left, top, right, bottom = work
    return left + ((right - left) - int(width)) // 2, max(top, bottom - int(height) - scaled(lift, dpi))

def settings_corner(work, dpi, width, height):

    left, top, right, bottom = work
    return max(left, right - int(width) - scaled(40, dpi)), max(top + scaled(20, dpi), bottom - int(height) - scaled(40, dpi))
