"""The recording light: a small glass microphone at the bottom middle of the screen with a soft glow that breathes with
your voice (green while you talk, blue for a command, an amber light running round it while it writes).

Two halves, kept apart so the drawing can be tested (and photographed to PNG files) without a screen:

* MicPainter draws one frame with Pillow, as an RGBA picture. Nothing in it touches the screen.
* MicWindow shows such a picture with UpdateLayeredWindow, so every pixel keeps its own transparency (a Tk colour key
  cannot do soft glows). It never takes focus, has no taskbar button, and clicks pass straight through it.
  Use it only from the window (Tk) thread.

All sizes are logical (96-dpi) numbers, scaled by the DPI of the monitor the light is on (geometry() says where it goes).
"""
import ctypes
import math
from ctypes import wintypes

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

DISC = 40  # the glass circle, in logical (96-dpi) pixels
SIDE = 96  # the see-through square it is drawn in, with room for the glow round it
GAP = 28  # space between the bottom of the circle and the bottom of the work area
SS = 3  # each piece is drawn this many times bigger, then shrunk, so its edges are smooth
LEVEL_STEPS = 16  # the voice level is drawn in this many steps (so a frame can be remembered)
SPIN_STEPS = 48  # positions of the light running round the circle while it works
SPIN_PER_SECOND = 0.8  # turns a second

GREEN, BLUE, AMBER, GREY, CLAUDE = "#22c55e", "#4c9dff", "#f5b700", "#9aa0a6", "#d97757"
GLASS, RIM, IDLE_INK = (20, 23, 27, 214), (255, 255, 255, 60), (232, 236, 239)

# state: (colour, what it is called, motion). Motion "voice": the glow breathes with your voice; "spin": a light runs round
# the circle; "still": a steady faint glow. The name is for people reading the code and the log; nothing is written on screen.
LOOKS = {
    "listening": (GREEN, "Listening", "voice"),
    "handsfree": (GREEN, "Hands-free", "voice"),
    "meeting": (GREEN, "Taking meeting notes", "voice"),
    "command": (BLUE, "Command", "voice"),
    "command_handsfree": (BLUE, "Command hands-free", "voice"),
    "working": (AMBER, "Writing", "spin"),
    "meeting_notes": (AMBER, "Writing meeting notes", "spin"),
    "thinking": (BLUE, "Thinking", "spin"),
    "claude": (CLAUDE, "Claude is working", "spin"),
    "error": (GREY, "Didn't catch that", "still"),
}
LOCKED = ("handsfree", "command_handsfree")  # these get a small lock badge: it keeps listening until you tap again


def dpi_scale(dpi):
    """1.0 at 96 dpi, 1.25 at 120, 1.5 at 144. Anything odd (0, None, below 96) counts as 96."""
    try:
        d = float(dpi)
    except (TypeError, ValueError):
        return 1.0
    return min(max(d, 96.0), 960.0) / 96.0


def scaled(n, dpi):
    return int(round(n * dpi_scale(dpi)))


def geometry(work, dpi):
    """(side, x, y) of the light's square, in real pixels: the glass circle sits bottom-centre of the work area (left, top,
    right, bottom), GAP above its bottom, with the glow's room round it. 96 x 96 at 96 dpi (scaled up on a sharper
    screen). The glow may reach past the work area; the circle never does."""
    left, top, right, bottom = work
    side, disc = scaled(SIDE, dpi), scaled(DISC, dpi)
    cx = left + (right - left) // 2
    cy = max(top + disc // 2, bottom - scaled(GAP, dpi) - disc // 2)
    return side, cx - side // 2, cy - side // 2


def hex_rgb(colour):
    colour = colour.lstrip("#")
    return tuple(int(colour[i:i + 2], 16) for i in (0, 2, 4))


def clamp(v, lo=0.0, hi=1.0):
    return max(lo, min(hi, v))


def premultiplied_bgra(image):
    """The bytes Windows wants for a see-through picture: blue, green, red, alpha, each colour already multiplied by its alpha
    (so a fully clear pixel is all zeros), top row first."""
    a = np.asarray(image.convert("RGBA"), dtype=np.uint16)
    alpha = a[..., 3:4]
    rgb = (a[..., :3] * alpha + 127) // 255
    return np.concatenate([rgb[..., 2::-1], alpha], axis=2).astype(np.uint8).tobytes()


class MicPainter:
    """Draws frames of the light. Nothing here touches the screen."""

    def __init__(self):
        self._cache = {}

    def _remember(self, key, make):
        found = self._cache.get(key)
        if found is None:
            if len(self._cache) > 400:
                self._cache.clear()
            found = self._cache[key] = make()
        return found

    @staticmethod
    def side(dpi):
        return scaled(SIDE, dpi)

    def _glow(self, colour, dpi, step):
        """The soft light round the microphone at one voice-level step (0..LEVEL_STEPS): bigger and brighter when louder."""
        def make():
            side, s = self.side(dpi), dpi_scale(dpi)
            lv = step / LEVEL_STEPS
            c = side / 2.0
            inner = DISC / 2 * 0.6 * s
            outer = DISC / 2 * (1.25 + 1.0 * lv) * s
            yy, xx = np.mgrid[0:side, 0:side]
            d = np.hypot(xx + 0.5 - c, yy + 0.5 - c)
            fall = np.clip((outer - d) / max(1.0, outer - inner), 0.0, 1.0) ** 1.6
            alpha = (fall * (0.35 + 0.5 * lv) * 255).astype(np.uint8)
            layer = Image.new("RGBA", (side, side), hex_rgb(colour) + (0,))
            layer.putalpha(Image.fromarray(alpha))
            return layer
        return self._remember(("glow", colour, dpi, step), make)

    def _disc(self, colour, ring, dpi):
        """The fogged dark glass circle with the microphone on it, and a coloured rim when `ring` is on."""
        def make():
            side, s = self.side(dpi), dpi_scale(dpi)
            big = side * SS
            c = big / 2.0
            r = DISC / 2 * s * SS
            img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
            shadow = Image.new("L", (big, big), 0)
            off = 2 * s * SS
            ImageDraw.Draw(shadow).ellipse((c - r, c - r + off, c + r, c + r + off), fill=255)
            shadow = shadow.filter(ImageFilter.GaussianBlur(3 * s * SS)).point(lambda v: int(v * 0.35))
            sh = Image.new("RGBA", (big, big), (0, 0, 0, 0))
            sh.putalpha(shadow)
            img = Image.alpha_composite(img, sh)
            d = ImageDraw.Draw(img)
            d.ellipse((c - r, c - r, c + r, c + r), fill=GLASS)
            sheen = Image.new("L", (big, big), 0)  # a light patch near the top of the glass
            ImageDraw.Draw(sheen).ellipse((c - r * 0.78, c - r * 0.95, c + r * 0.78, c + r * 0.15), fill=255)
            sheen = sheen.filter(ImageFilter.GaussianBlur(r * 0.22))
            clip = Image.new("L", (big, big), 0)
            ImageDraw.Draw(clip).ellipse((c - r, c - r, c + r, c + r), fill=255)
            sheen = Image.fromarray((np.asarray(sheen, np.float32) * np.asarray(clip, np.float32) / 255 * 0.16).astype(np.uint8))
            white = Image.new("RGBA", (big, big), (255, 255, 255, 0))
            white.putalpha(sheen)
            img = Image.alpha_composite(img, white)
            d = ImageDraw.Draw(img)
            d.ellipse((c - r, c - r, c + r, c + r), outline=RIM, width=max(1, round(1.2 * s * SS)))
            rgb = hex_rgb(colour)
            if ring:
                lr = r - 1.2 * s * SS
                d.ellipse((c - lr, c - lr, c + lr, c + lr), outline=rgb + (255,), width=max(1, round(2.6 * s * SS)))
            ink = rgb if ring else IDLE_INK
            self._microphone(d, c, r, s, ink)
            return img.resize((side, side), Image.LANCZOS)
        return self._remember(("disc", colour, ring, dpi), make)

    @staticmethod
    def _microphone(d, c, r, s, ink):
        """The microphone: a capsule, the cradle under it, the stem and the base."""
        w = max(1, round(1.9 * s * SS))
        cw, ch, top = r * 0.40, r * 0.74, c - r * 0.56
        d.rounded_rectangle((c - cw / 2, top, c + cw / 2, top + ch), radius=cw / 2, outline=ink + (255,), width=w)
        cy, cr = top + ch * 0.55, r * 0.36
        d.arc((c - cr, cy - cr, c + cr, cy + cr), 15, 165, fill=ink + (255,), width=w)
        d.line((c, cy + cr, c, c + r * 0.48), fill=ink + (255,), width=w)
        d.line((c - r * 0.2, c + r * 0.48, c + r * 0.2, c + r * 0.48), fill=ink + (255,), width=w)

    def _spin(self, colour, dpi, index):
        """The light running round the circle while it works: a bright head with a fading tail."""
        def make():
            side, s = self.side(dpi), dpi_scale(dpi)
            big = side * SS
            c = big / 2.0
            r = DISC / 2 * s * SS - 1.2 * s * SS
            layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
            d = ImageDraw.Draw(layer)
            head = index * 360.0 / SPIN_STEPS - 90.0  # (0 degrees is 3 o'clock in Pillow; start at 12)
            rgb, steps = hex_rgb(colour), 12
            for i in range(steps):  # tail first (dim) to head (bright)
                start = head - 110 * (1 - i / steps)
                end = head - 110 * (1 - (i + 1) / steps) + 1.0
                d.arc((c - r, c - r, c + r, c + r), start, end, fill=rgb + (int(255 * ((i + 1) / steps) ** 0.7),),
                      width=max(1, round(3.2 * s * SS)))
            return layer.resize((side, side), Image.LANCZOS)
        return self._remember(("spin", colour, dpi, index), make)

    def _lock(self, dpi):
        """A small badge at the lower right: hands-free keeps listening until you tap again."""
        def make():
            side, s = self.side(dpi), dpi_scale(dpi)
            big = side * SS
            c = big / 2.0
            r = DISC / 2 * s * SS
            bx, by, br = c + r * 0.72, c + r * 0.66, r * 0.34
            layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
            d = ImageDraw.Draw(layer)
            d.ellipse((bx - br, by - br, bx + br, by + br), fill=hex_rgb(GREEN) + (255,), outline=(20, 23, 27, 255),
                      width=max(1, round(1.2 * s * SS)))
            w = max(1, round(1.4 * s * SS))
            d.rectangle((bx - br * 0.42, by - br * 0.05, bx + br * 0.42, by + br * 0.5), fill=(255, 255, 255, 255))
            d.arc((bx - br * 0.3, by - br * 0.42, bx + br * 0.3, by + br * 0.18), 180, 360, fill=(255, 255, 255, 255), width=w)
            return layer.resize((side, side), Image.LANCZOS)
        return self._remember(("lock", dpi), make)

    def frame(self, state, dpi, level=0.0, t=0.0):
        """The RGBA picture of the light for this state. `level` is the voice level 0..1 (already smoothed), `t` the time in
        seconds (for the running light)."""
        colour, _, motion = LOOKS[state]
        side = self.side(dpi)
        canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        if motion == "voice":
            canvas.alpha_composite(self._glow(colour, dpi, self.level_step(level)))
            canvas.alpha_composite(self._disc(colour, True, dpi))
        elif motion == "spin":
            canvas.alpha_composite(self._glow(colour, dpi, 2))
            canvas.alpha_composite(self._disc(colour, False, dpi))
            canvas.alpha_composite(self._spin(colour, dpi, self.spin_index(t)))
        else:
            canvas.alpha_composite(self._glow(colour, dpi, 0))
            canvas.alpha_composite(self._disc(colour, True, dpi))
        if state in LOCKED:
            canvas.alpha_composite(self._lock(dpi))
        return canvas

    @staticmethod
    def level_step(level):
        return int(round(clamp(level) * LEVEL_STEPS))

    @staticmethod
    def spin_index(t):
        return int(t * SPIN_PER_SECOND * SPIN_STEPS) % SPIN_STEPS


# ---------- the window ----------

try:
    import win32con
    import win32gui
    user32, gdi32 = ctypes.windll.user32, ctypes.windll.gdi32
except Exception:  # (tests on a machine without Windows pieces only use MicPainter)
    win32con = win32gui = user32 = gdi32 = None

CLASS_NAME = "VoiceToolMicLight"
EX_STYLE = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080 | 0x00000008  # LAYERED | TRANSPARENT | NOACTIVATE | TOOLWINDOW | TOPMOST
ULW_ALPHA, AC_SRC_OVER, AC_SRC_ALPHA = 2, 0, 1
HWND_TOPMOST = -1
SWP_FLAGS = 0x0001 | 0x0002 | 0x0010  # NOSIZE | NOMOVE | NOACTIVATE
WM_NCHITTEST, WM_MOUSEACTIVATE, WM_DPICHANGED = 0x0084, 0x0021, 0x02E0
HTTRANSPARENT, MA_NOACTIVATE = -1, 3


class BLENDFUNCTION(ctypes.Structure):
    _fields_ = [("BlendOp", ctypes.c_ubyte), ("BlendFlags", ctypes.c_ubyte),
                ("SourceConstantAlpha", ctypes.c_ubyte), ("AlphaFormat", ctypes.c_ubyte)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
                ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG), ("biYPelsPerMeter", wintypes.LONG),
                ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD)]


if user32 is not None:
    user32.UpdateLayeredWindow.argtypes = [wintypes.HWND, wintypes.HDC, ctypes.POINTER(wintypes.POINT),
                                           ctypes.POINTER(wintypes.SIZE), wintypes.HDC, ctypes.POINTER(wintypes.POINT),
                                           wintypes.COLORREF, ctypes.POINTER(BLENDFUNCTION), wintypes.DWORD]
    user32.GetDC.argtypes, user32.GetDC.restype = [wintypes.HWND], wintypes.HDC
    user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
    gdi32.CreateCompatibleDC.argtypes, gdi32.CreateCompatibleDC.restype = [wintypes.HDC], wintypes.HDC
    gdi32.CreateDIBSection.argtypes = [wintypes.HDC, ctypes.POINTER(BITMAPINFOHEADER), wintypes.UINT,
                                       ctypes.POINTER(ctypes.c_void_p), wintypes.HANDLE, wintypes.DWORD]
    gdi32.CreateDIBSection.restype = wintypes.HBITMAP
    gdi32.SelectObject.argtypes, gdi32.SelectObject.restype = [wintypes.HDC, wintypes.HGDIOBJ], wintypes.HGDIOBJ
    gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
    gdi32.DeleteDC.argtypes = [wintypes.HDC]


def _register():
    wc = win32gui.WNDCLASS()
    wc.lpszClassName = CLASS_NAME
    wc.lpfnWndProc = {
        WM_NCHITTEST: lambda hwnd, msg, wp, lp: HTTRANSPARENT,  # clicks go to whatever is underneath
        WM_MOUSEACTIVATE: lambda hwnd, msg, wp, lp: MA_NOACTIVATE,
        WM_DPICHANGED: lambda hwnd, msg, wp, lp: 0,  # we place and size it ourselves
    }
    wc.hInstance = win32gui.GetModuleHandle(None)
    try:
        win32gui.RegisterClass(wc)
    except win32gui.error as e:
        if e.winerror != 1410:  # (already registered)
            raise


class MicWindow:
    """The see-through window the light is drawn in. Real screen pixels, any monitor."""
    TOP_AGAIN_SECONDS = 0.5  # how often it is pushed back above other topmost windows while it shows

    def __init__(self):
        _register()
        self.hwnd = win32gui.CreateWindowEx(EX_STYLE, CLASS_NAME, "VoiceTool light", win32con.WS_POPUP,
                                            0, 0, 1, 1, 0, 0, win32gui.GetModuleHandle(None), None)
        self.visible = False
        self._topped = 0.0

    def paint(self, image, x, y, now=0.0):
        """Show this RGBA picture with its top-left corner at (x, y), real screen pixels."""
        w, h = image.size
        data = premultiplied_bgra(image)
        header = BITMAPINFOHEADER(ctypes.sizeof(BITMAPINFOHEADER), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)  # (-h: top row first)
        screen = user32.GetDC(None)
        memory = gdi32.CreateCompatibleDC(screen)
        bits = ctypes.c_void_p()
        bitmap = gdi32.CreateDIBSection(memory, ctypes.byref(header), 0, ctypes.byref(bits), None, 0)
        try:
            if not bitmap or not bits:
                raise OSError("could not make the picture for the light")
            ctypes.memmove(bits, data, len(data))
            old = gdi32.SelectObject(memory, bitmap)
            blend = BLENDFUNCTION(AC_SRC_OVER, 0, 255, AC_SRC_ALPHA)
            ok = user32.UpdateLayeredWindow(self.hwnd, screen, ctypes.byref(wintypes.POINT(int(x), int(y))),
                                            ctypes.byref(wintypes.SIZE(w, h)), memory, ctypes.byref(wintypes.POINT(0, 0)),
                                            0, ctypes.byref(blend), ULW_ALPHA)
            gdi32.SelectObject(memory, old)
            if not ok:
                raise ctypes.WinError()
        finally:
            if bitmap:
                gdi32.DeleteObject(bitmap)
            gdi32.DeleteDC(memory)
            user32.ReleaseDC(None, screen)
        if not self.visible:
            win32gui.ShowWindow(self.hwnd, win32con.SW_SHOWNOACTIVATE)
            self.visible = True
            self._topped = 0.0
        if now - self._topped >= self.TOP_AGAIN_SECONDS:
            win32gui.SetWindowPos(self.hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_FLAGS)  # (stays above other windows)
            self._topped = now

    def hide(self):
        if self.visible:
            win32gui.ShowWindow(self.hwnd, win32con.SW_HIDE)
            self.visible = False

    def destroy(self):
        try:
            win32gui.DestroyWindow(self.hwnd)
        except Exception:
            pass
        self.visible = False
