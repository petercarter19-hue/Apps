// Headless tests for Spotlight. Compiles the REAL source files and runs them against made-up monitors and windows.
// Nothing here creates a window, hooks Windows, registers a hotkey or reads the real screen, mouse or keyboard.
//
//   dotnet run --project tests -c Release        (exit code = number of failed checks)
using Spotlight;

// Settings and the sign-in entry use a test name and a temp folder, never the real ones.
string ns = "unit" + Guid.NewGuid().ToString("N")[..8];
string dir = Path.Combine(Path.GetTempPath(), "spotlight-unit-" + ns);
Environment.SetEnvironmentVariable("SPOTLIGHT_TEST_NS", ns);
Environment.SetEnvironmentVariable("SPOTLIGHT_TEST_DIR", dir);
if (!Instance.IsTest || Instance.TestNs != ns) { Console.WriteLine("Refusing to run: test names did not take effect."); return 99; }

int failures = 0, passes = 0;
void Test(string name, Action body)
{
    bool ok;
    try { body(); ok = true; }
    catch (Exception ex) { ok = false; Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}"); }
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (ok) passes++; else failures++;
}
void Expect(bool condition, string what) { if (!condition) throw new Exception("expected: " + what); }
void Eq<T>(T actual, T expected, string what) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"{what}: expected {expected}, got {actual}"); }

// ---- made-up setups -----------------------------------------------------------------------------
// One 1920x1080 screen at 100%.
var one = new List<MonitorInfo> { new(new Box(0, 0, 1920, 1080), 96) };
// Two screens side by side: 1920x1080 at 100%, then 2560x1440 at 150% (mixed DPI).
var mixed = new List<MonitorInfo> { new(new Box(0, 0, 1920, 1080), 96), new(new Box(1920, 0, 4480, 1440), 144) };
// A second screen to the LEFT of the main one (negative coordinates) and one above-right.
var negative = new List<MonitorInfo> { new(new Box(0, 0, 1920, 1080), 96), new(new Box(-1280, 100, 0, 1124), 96), new(new Box(1920, -1080, 3840, 0), 192) };

WindowInfo Win(Box frame, string cls = "Notepad", int pid = 100, long h = 1000, bool max = false, bool caption = true, bool min = false, bool vis = true, bool cloaked = false) =>
    new(h, pid, cls, frame, vis, min, max, cloaked, caption);

// ---- geometry: the cut-out ----------------------------------------------------------------------
Test("one screen: hole is the window, in the screen's own pixels, corners rounded 8 px", () =>
{
    var h = Geometry.Holes(one, new Box(100, 200, 900, 700), false);
    Eq(h.Count, 1, "holes");
    Eq(h[0].Local, new Box(100, 200, 900, 700), "local");
    Eq(h[0].Radius, 8, "radius");
});
Test("maximized window: square corners", () => Eq(Geometry.Holes(one, new Box(0, 0, 1920, 1040), true)[0].Radius, 0, "radius"));
Test("window on the second screen: no hole on the first, local coordinates on the second", () =>
{
    var h = Geometry.Holes(mixed, new Box(2000, 100, 3000, 900), false);
    Eq(h.Count, 1, "holes");
    Eq(h[0].MonitorIndex, 1, "monitor");
    Eq(h[0].Local, new Box(80, 100, 1080, 900), "local");
});
Test("mixed DPI: corner radius follows the screen's scale (8 px at 100%, 12 px at 150%)", () =>
{
    Eq(Geometry.Holes(mixed, new Box(100, 100, 500, 500), false)[0].Radius, 8, "100%");
    Eq(Geometry.Holes(mixed, new Box(2100, 100, 2500, 500), false)[0].Radius, 12, "150%");
});
Test("window across two screens: a hole on each, each in its own pixels, each with its own radius", () =>
{
    var h = Geometry.Holes(mixed, new Box(1800, 200, 2200, 600), false);
    Eq(h.Count, 2, "holes");
    Eq(h[0].Local, new Box(1800, 200, 1920, 600), "first");
    Eq(h[1].Local, new Box(0, 200, 280, 600), "second");
    Eq(h[0].Radius, 8, "radius a"); Eq(h[1].Radius, 12, "radius b");
});
Test("negative coordinates (screen to the left, screen above)", () =>
{
    var left = Geometry.Holes(negative, new Box(-1000, 200, -200, 800), false);
    Eq(left.Count, 1, "left count"); Eq(left[0].MonitorIndex, 1, "left monitor"); Eq(left[0].Local, new Box(280, 100, 1080, 700), "left local");
    var above = Geometry.Holes(negative, new Box(2000, -500, 2600, -100), false);
    Eq(above.Count, 1, "above count"); Eq(above[0].MonitorIndex, 2, "above monitor"); Eq(above[0].Local, new Box(80, 580, 680, 980), "above local");
    Eq(above[0].Radius, 16, "200% radius");
});
Test("window partly off every screen is clipped to the screen", () =>
{
    var h = Geometry.Holes(one, new Box(-50, -20, 300, 300), false);
    Eq(h[0].Local, new Box(0, 0, 300, 300), "clipped");
});
Test("window entirely off screen or empty: no holes", () =>
{
    Eq(Geometry.Holes(one, new Box(5000, 5000, 5200, 5200), false).Count, 0, "off");
    Eq(Geometry.Holes(one, new Box(10, 10, 10, 50), false).Count, 0, "empty");
});

// ---- rules: what gets lit -----------------------------------------------------------------------
Verdict V(WindowInfo? w, List<MonitorInfo>? mons = null, int own = 999) => Rules.Classify(w, mons ?? one, own);
var normal = new Box(100, 100, 900, 700);
Test("normal window is lit", () => Eq(V(Win(normal)), Verdict.Spotlight, "verdict"));
Test("maximized window with its taskbar showing is lit", () => Eq(V(Win(new Box(0, 0, 1920, 1040), max: true)), Verdict.Spotlight, "verdict"));
Test("maximized window that covers the whole screen (auto-hide taskbar) but has a title bar is still lit", () =>
    Eq(V(Win(new Box(0, 0, 1920, 1080), max: true, caption: true)), Verdict.Spotlight, "verdict"));
Test("full-screen game or video (covers the screen, no title bar): no dimming", () =>
    Eq(V(Win(new Box(0, 0, 1920, 1080), caption: false)), Verdict.Clear, "verdict"));
Test("full-screen on the second screen: no dimming; a borderless window that covers no whole screen is still lit", () =>
{
    Eq(V(Win(new Box(1920, 0, 4480, 1440), caption: false), mixed), Verdict.Clear, "second screen full-screen");
    Eq(V(Win(new Box(500, 100, 3000, 900), caption: false), mixed), Verdict.Spotlight, "borderless but not covering a screen");
});
Test("minimized, hidden, cloaked (another virtual desktop) or empty window: no dimming", () =>
{
    Eq(V(Win(normal, min: true)), Verdict.Clear, "minimized");
    Eq(V(Win(new Box(-32000, -32000, -31840, -31972), min: true)), Verdict.Clear, "minimized parked off screen");
    Eq(V(Win(normal, vis: false)), Verdict.Clear, "hidden");
    Eq(V(Win(normal, cloaked: true)), Verdict.Clear, "cloaked");
    Eq(V(Win(new Box(10, 10, 10, 10))), Verdict.Clear, "empty");
});
Test("desktop, Start, Search and Alt+Tab: no dimming", () =>
{
    foreach (var c in new[] { "Progman", "WorkerW", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "TaskSwitcherWnd" })
        Eq(V(Win(normal, c)), Verdict.Clear, c);
});
Test("taskbar and tray pop-ups: leave things as they are (no flicker)", () =>
{
    foreach (var c in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow" })
        Eq(V(Win(normal, c)), Verdict.Hold, c);
});
Test("Spotlight's own windows are never lit", () => Eq(V(Win(normal, pid: 999), own: 999), Verdict.Hold, "verdict"));
Test("no foreground window right now: leave things as they are", () =>
{
    Eq(V(null), Verdict.Hold, "null"); Eq(V(Win(normal, h: 0)), Verdict.Hold, "handle 0");
});

// ---- follow logic -------------------------------------------------------------------------------
FollowLogic NewFollow() => new(999, mixed);
Test("click on a window: show it with a glide; the window becomes the target", () =>
{
    var f = NewFollow();
    var d = f.OnForeground(Win(normal, h: 1));
    Eq(d.Kind, DecisionKind.Show, "kind"); Eq(d.Frame, normal, "frame"); Expect(d.Animate, "glides"); Eq(f.Target, 1L, "target");
});
Test("click on another window: the light moves to it", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    var d = f.OnForeground(Win(new Box(2000, 100, 3000, 900), h: 2));
    Eq(d.Kind, DecisionKind.Show, "kind"); Eq(f.Target, 2L, "target");
});
Test("the lit window moves: the light jumps with it (no glide); other windows' events are ignored", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    var moved = new Box(300, 300, 1100, 900);
    var d = f.OnTargetChanged(Win(moved, h: 1));
    Eq(d.Kind, DecisionKind.Show, "kind"); Eq(d.Frame, moved, "frame"); Expect(!d.Animate, "snaps while dragging");
    Eq(f.OnTargetChanged(Win(moved, h: 7)).Kind, DecisionKind.Hold, "other window");
});
Test("the lit window is minimized or closed: dimming clears and nothing is targeted", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    Eq(f.OnTargetChanged(Win(normal, h: 1, min: true)).Kind, DecisionKind.Clear, "minimized"); Eq(f.Target, 0L, "target 1");
    f.OnForeground(Win(normal, h: 1));
    Eq(f.OnTargetChanged(null).Kind, DecisionKind.Clear, "closed"); Eq(f.Target, 0L, "target 2");
});
Test("the lit window goes full screen: dimming clears", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    Eq(f.OnTargetChanged(Win(new Box(0, 0, 1920, 1080), h: 1, caption: false)).Kind, DecisionKind.Clear, "kind");
});
Test("focus to the taskbar keeps the light; focus to the desktop or Start clears it", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    Eq(f.OnForeground(Win(normal, "Shell_TrayWnd", h: 5)).Kind, DecisionKind.Hold, "taskbar"); Eq(f.Target, 1L, "still 1");
    Eq(f.OnForeground(Win(normal, "Progman", h: 6)).Kind, DecisionKind.Clear, "desktop"); Eq(f.Target, 0L, "no target");
});
Test("focus to Spotlight's own window (its menu) does nothing", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    Eq(f.OnForeground(Win(normal, pid: 999, h: 9)).Kind, DecisionKind.Hold, "own");
});
Test("off: always clear, nothing targeted; on again: lights what is in front", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    f.Enabled = false;
    Eq(f.OnForeground(Win(normal, h: 2)).Kind, DecisionKind.Clear, "off"); Eq(f.Target, 0L, "no target");
    Eq(f.OnTargetChanged(Win(normal, h: 2)).Kind, DecisionKind.Hold, "events ignored while off");
    f.Enabled = true;
    Eq(f.OnForeground(Win(normal, h: 2)).Kind, DecisionKind.Show, "on");
});
Test("monitor change: full-screen is judged against the new screens", () =>
{
    var f = NewFollow();
    var fs = Win(new Box(0, 0, 1280, 720), h: 3, caption: false);
    Eq(f.OnForeground(fs).Kind, DecisionKind.Show, "before: not covering a screen");
    f.SetMonitors(new List<MonitorInfo> { new(new Box(0, 0, 1280, 720), 96) });
    Eq(f.OnForeground(fs).Kind, DecisionKind.Clear, "after: covers the only screen");
});

Test("item 1: the lit window leaves full screen: the dimming comes back at once, without a click elsewhere", () =>
{
    var f = NewFollow();
    var full = new Box(0, 0, 1920, 1080);
    f.OnForeground(Win(normal, h: 1));
    Eq(f.OnTargetChanged(Win(full, h: 1, caption: false)).Kind, DecisionKind.Clear, "goes full screen");
    var back = f.OnTargetChanged(Win(normal, h: 1));
    Eq(back.Kind, DecisionKind.Show, "leaves full screen"); Eq(back.Frame, normal, "frame"); Eq(f.Target, 1L, "lit again");
    Eq(f.OnTargetChanged(Win(full, h: 1, caption: false)).Kind, DecisionKind.Clear, "full screen again");
});
Test("item 1: a window that was already full screen when focused is lit when it leaves full screen", () =>
{
    var f = NewFollow();
    var full = new Box(0, 0, 1920, 1080);
    Eq(f.OnForeground(Win(full, h: 4, caption: false)).Kind, DecisionKind.Clear, "focus on full-screen window");
    Eq(f.Watched, 4L, "its size is still watched");
    Eq(f.OnTargetChanged(Win(normal, h: 4)).Kind, DecisionKind.Show, "leaves full screen");
    Eq(f.Target, 4L, "lit");
});
Test("item 1: a full-screen window's size events never light a different window, another click ends the watch", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(new Box(0, 0, 1920, 1080), h: 4, caption: false));
    Eq(f.OnTargetChanged(Win(normal, h: 7)).Kind, DecisionKind.Hold, "other window ignored");
    Eq(f.OnTargetChanged(Win(new Box(0, 0, 1920, 1080), h: 4, caption: false)).Kind, DecisionKind.Hold, "still full screen: stays clear");
    Eq(f.OnTargetChanged(null).Kind, DecisionKind.Hold, "gone"); Eq(f.Watched, 0L, "no watch left");
    f.OnForeground(Win(new Box(0, 0, 1920, 1080), h: 4, caption: false));
    f.OnForeground(Win(normal, h: 2));
    Eq(f.Watched, 2L, "watching the new lit window"); Eq(f.OnTargetChanged(Win(normal, h: 4)).Kind, DecisionKind.Hold, "old full-screen window ignored");
});
Test("item 1: off, desktop or minimized are not watched; nothing is relit by their size events", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    f.OnTargetChanged(Win(normal, h: 1, min: true)); Eq(f.Watched, 0L, "minimized: not watched");
    f.OnForeground(Win(new Box(0, 0, 1920, 1080), h: 4, caption: false)); f.Enabled = false;
    Eq(f.OnTargetChanged(Win(normal, h: 4)).Kind, DecisionKind.Hold, "off ignores events");
    f.OnForeground(Win(normal, h: 4)); Eq(f.Watched, 0L, "off: no watch");
});
Test("item 2: no foreground event arrived but another normal window is in front: re-check and light it", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    Expect(f.NeedsRecheck(Win(new Box(300, 300, 1100, 900), h: 2)), "different normal window in front");
    Expect(!f.NeedsRecheck(Win(normal, h: 1)), "same window: nothing to do");
    var d = f.OnForeground(Win(new Box(300, 300, 1100, 900), h: 2));
    Eq(d.Kind, DecisionKind.Show, "lit"); Eq(f.Target, 2L, "target");
    Expect(!f.NeedsRecheck(Win(new Box(300, 300, 1100, 900), h: 2)), "now in step");
});
Test("item 2: nothing lit yet (started or cleared) and a normal window is in front: re-check", () =>
{
    var f = NewFollow();
    Expect(f.NeedsRecheck(Win(normal, h: 3)), "nothing lit");
});
Test("item 2: taskbar, own window, none in front, full screen, off: no re-check (no flicker, no churn)", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    Expect(!f.NeedsRecheck(Win(normal, "Shell_TrayWnd", h: 5)), "taskbar");
    Expect(!f.NeedsRecheck(Win(normal, pid: 999, h: 9)), "own window");
    Expect(!f.NeedsRecheck(null), "no foreground");
    var g = NewFollow();
    Expect(!g.NeedsRecheck(Win(new Box(0, 0, 1920, 1080), h: 4, caption: false)), "full screen, nothing lit: already clear");
    Expect(!g.NeedsRecheck(Win(normal, "Progman", h: 6)), "desktop, nothing lit: already clear");
    f.Enabled = false;
    Expect(!f.NeedsRecheck(Win(normal, h: 2)), "off");
});
Test("item 2: something lit but the front window is now the desktop or full screen: re-check so the dimming clears", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    Expect(f.NeedsRecheck(Win(normal, "Progman", h: 6)), "desktop");
    Expect(f.NeedsRecheck(Win(new Box(0, 0, 1920, 1080), h: 4, caption: false)), "full screen");
});

Test("item 2: zero idle cost: no polling while a normal window is lit", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    Expect(!f.ShouldPoll, "lit: no poll");
    f.OnTargetChanged(Win(new Box(300, 300, 1100, 900), h: 1));
    Expect(!f.ShouldPoll, "lit window moved: still no poll");
});
Test("item 2: polling runs only while the taskbar has focus or nothing is lit, and stops once a window is lit again", () =>
{
    var f = NewFollow();
    Expect(f.ShouldPoll, "nothing lit yet");
    f.OnForeground(Win(normal, h: 1)); Expect(!f.ShouldPoll, "lit");
    f.OnForeground(Win(normal, "Shell_TrayWnd", h: 5)); Expect(f.ShouldPoll, "taskbar has focus (old window still lit)"); Eq(f.Target, 1L, "still lit");
    Expect(f.NeedsRecheck(Win(new Box(300, 300, 1100, 900), h: 2)), "new window in front, no event");
    f.OnForeground(Win(new Box(300, 300, 1100, 900), h: 2)); Expect(!f.ShouldPoll, "new window lit: poll stops");
    f.OnForeground(Win(normal, "Progman", h: 6)); Expect(f.ShouldPoll, "desktop: nothing lit");
    f.OnForeground(Win(normal, h: 1)); Expect(!f.ShouldPoll, "lit again: stops");
    f.OnTargetChanged(Win(normal, h: 1, min: true)); Expect(f.ShouldPoll, "lit window minimized: nothing lit");
    f.OnForeground(Win(normal, pid: 999, h: 9)); Expect(f.ShouldPoll, "own menu has focus");
});
Test("item 2: full screen and off: full screen is polled (nothing lit), off never polls", () =>
{
    var f = NewFollow();
    f.OnForeground(Win(normal, h: 1));
    f.OnTargetChanged(Win(new Box(0, 0, 1920, 1080), h: 1, caption: false)); Expect(f.ShouldPoll, "full screen: nothing lit");
    f.OnTargetChanged(Win(normal, h: 1)); Expect(!f.ShouldPoll, "left full screen: lit, poll stops");
    f.Enabled = false; Expect(!f.ShouldPoll, "off");
    f.OnForeground(Win(normal, "Shell_TrayWnd", h: 5)); Expect(!f.ShouldPoll, "off, taskbar: still off");
});

// ---- animation: the glide and the fades ---------------------------------------------------------
Box A = new(100, 100, 500, 400), B = new(1000, 300, 1600, 900);
Test("easing: starts at 0, ends at 1, never goes backwards", () =>
{
    Eq(SpotlightModel.Ease(0), 0.0, "start"); Eq(SpotlightModel.Ease(1), 1.0, "end");
    double prev = 0;
    for (int i = 1; i <= 100; i++) { double e = SpotlightModel.Ease(i / 100.0); Expect(e >= prev, "monotonic"); prev = e; }
    Expect(SpotlightModel.Ease(0.5) > 0.5, "eases out (quick start)");
});
Test("first light: appears in place and fades in (no glide from nowhere)", () =>
{
    var m = new SpotlightModel();
    m.Apply(new Decision(DecisionKind.Show, A, false, true), 0);
    Eq(m.Rect, (Box?)A, "rect now"); Eq(m.Opacity, 0.0, "opacity at 0 ms");
    Expect(m.Advance(150), "still fading"); Expect(m.Opacity > 0 && m.Opacity < 1, "mid fade");
    Expect(!m.Advance(SpotlightModel.FadeInMs), "done"); Eq(m.Opacity, 1.0, "full");
});
Test("click another window: the light glides from the old window to the new one and lands exactly", () =>
{
    var m = new SpotlightModel();
    m.Apply(new Decision(DecisionKind.Show, A, false, true), 0); m.Advance(1000);
    m.Apply(new Decision(DecisionKind.Show, B, false, true), 1000);
    m.Advance(1000 + SpotlightModel.GlideMs / 2);
    var mid = m.Rect!.Value;
    Expect(mid.Left > A.Left && mid.Left < B.Left && mid.Right > A.Right && mid.Right < B.Right, "between the two");
    Expect(m.Advance(1000 + SpotlightModel.GlideMs - 1) || true, "frame");
    Expect(!m.Advance(1000 + SpotlightModel.GlideMs), "finished"); Eq(m.Rect, (Box?)B, "landed");
    Eq(m.Opacity, 1.0, "stayed dark around it");
});
Test("window dragged with no glide running: the light jumps with it", () =>
{
    var m = new SpotlightModel();
    m.Apply(new Decision(DecisionKind.Show, A, false, true), 0); m.Advance(1000);
    var moved = new Box(120, 130, 520, 430);
    m.Apply(new Decision(DecisionKind.Show, moved, false, false), 1100);
    Eq(m.Rect, (Box?)moved, "snapped"); Expect(!m.Animating, "no animation");
});
Test("target moves during a glide: the glide re-aims and still lands on the newest place", () =>
{
    var m = new SpotlightModel();
    m.Apply(new Decision(DecisionKind.Show, A, false, true), 0); m.Advance(1000);
    m.Apply(new Decision(DecisionKind.Show, B, false, true), 1000); m.Advance(1100);
    var B2 = new Box(1100, 350, 1700, 950);
    m.Apply(new Decision(DecisionKind.Show, B2, false, false), 1110);
    m.Advance(2000);
    Eq(m.Rect, (Box?)B2, "landed on newest");
});
Test("clear: fades out, then there is no rect; a later show starts fresh in place", () =>
{
    var m = new SpotlightModel();
    m.Apply(new Decision(DecisionKind.Show, A, false, true), 0); m.Advance(1000);
    m.Apply(Decision.Clear, 1000);
    m.Advance(1000 + SpotlightModel.FadeOutMs / 2); Expect(m.Opacity > 0 && m.Opacity < 1, "mid fade-out"); Expect(m.Rect != null, "still has rect while fading");
    Expect(!m.Advance(1000 + SpotlightModel.FadeOutMs), "done"); Eq(m.Opacity, 0.0, "dark gone"); Expect(m.Rect == null, "rect cleared");
    m.Apply(new Decision(DecisionKind.Show, B, false, true), 5000);
    Eq(m.Rect, (Box?)B, "fresh, no glide from the old place");
});
Test("clear when already clear does nothing", () =>
{
    var m = new SpotlightModel();
    m.Apply(Decision.Clear, 0);
    Expect(!m.Animating && m.Rect == null && m.Opacity == 0, "idle");
});
Test("idle model needs no frames (no timer needed when nothing moves)", () =>
{
    var m = new SpotlightModel();
    m.Apply(new Decision(DecisionKind.Show, A, false, true), 0);
    Expect(!m.Advance(5000), "settled"); Expect(!m.Animating, "idle");
    m.Apply(Decision.Hold, 6000); Expect(!m.Animating, "hold changes nothing");
});
Test("show again while fading out: fades back in, keeps its place", () =>
{
    var m = new SpotlightModel();
    m.Apply(new Decision(DecisionKind.Show, A, false, true), 0); m.Advance(1000);
    m.Apply(Decision.Clear, 1000); m.Advance(1100);
    m.Apply(new Decision(DecisionKind.Show, A, false, true), 1100);
    m.Advance(5000);
    Eq(m.Opacity, 1.0, "back to full"); Eq(m.Rect, (Box?)A, "same place");
});
Test("hole follows the glide across screens: a mid-glide frame spans both screens' holes", () =>
{
    var m = new SpotlightModel();
    var onFirst = new Box(1000, 100, 1800, 700); var onSecond = new Box(2400, 100, 3200, 700);
    m.Apply(new Decision(DecisionKind.Show, onFirst, false, true), 0); m.Advance(1000);
    m.Apply(new Decision(DecisionKind.Show, onSecond, false, true), 1000);
    bool sawBoth = false;
    for (long t = 1000; t <= 1000 + SpotlightModel.GlideMs; t += 8)
    {
        m.Advance(t);
        if (Geometry.Holes(mixed, m.Rect!.Value, false).Count == 2) sawBoth = true;
    }
    Expect(sawBoth, "crossed the boundary"); Eq(Geometry.Holes(mixed, m.Rect!.Value, false).Count, 1, "ends on one screen");
});

// ---- strength, hotkey, settings -----------------------------------------------------------------
Test("dim slider 0 to 100: 0 is no dimming, steps only get darker, 100 is a full blackout, steep early curve, scaled by the fade", () =>
{
    Eq(Strengths.Alpha(0, 1), (byte)0, "0% = no dimming");
    byte prev = 0; bool rising = true;
    for (int p = 1; p <= 100; p++) { byte a = Strengths.Alpha(p, 1); if (a < prev) rising = false; prev = a; }
    Expect(rising, "never gets lighter as the slider goes up"); Eq(Strengths.Alpha(100, 1), (byte)255, "100% is a full blackout");
    for (int p = 0; p <= 100; p++) Eq(Strengths.Alpha(p, 1), (byte)Math.Round((1 - Math.Pow(1 - p / 100.0, 2.5)) * 255), $"curve at {p}%");
    Expect(Strengths.Alpha(40, 1) >= 0.70 * 255, "40% is already clearly dark"); Expect(Strengths.Alpha(20, 1) >= 0.40 * 255, "20% is visible");
    Eq(Strengths.Alpha(100, 0), (byte)0, "no fade = clear"); Eq(Strengths.Alpha(50, 5), Strengths.Alpha(50, 1), "fade clamped");
    Eq(Strengths.Alpha(-20, 1), (byte)0, "below 0 = 0"); Eq(Strengths.Alpha(500, 1), Strengths.Alpha(100, 1), "above 100 = 100");
    Expect(Math.Abs(Strengths.Alpha(Strengths.FromLegacy(DimStrength.Medium), 1) - 0.5225 * 255) < 3, "old Medium keeps its look");
    Expect(Strengths.FromLegacy(DimStrength.Light) < Strengths.FromLegacy(DimStrength.Medium) && Strengths.FromLegacy(DimStrength.Medium) < Strengths.FromLegacy(DimStrength.Strong), "old three ordered");
});
Test("hotkey from pressed keys (settings window): needs Ctrl, Alt or Win and a letter, digit or F1-F12", () =>
{
    Expect(Hotkey.TryFromKeys(true, true, false, false, 'K', out var h) && h.ToString() == "Ctrl+Alt+K", "Ctrl+Alt+K");
    Expect(Hotkey.TryFromKeys(false, true, true, false, 0x7B, out var f) && f.ToString() == "Alt+Shift+F12", "Alt+Shift+F12");
    Expect(Hotkey.TryFromKeys(true, false, false, false, '5', out _), "Ctrl+5");
    Expect(!Hotkey.TryFromKeys(false, false, false, false, 'K', out _), "plain key");
    Expect(!Hotkey.TryFromKeys(false, false, true, false, 'K', out _), "Shift only");
    Expect(!Hotkey.TryFromKeys(true, true, false, false, 0x20, out _), "space is not allowed");
    Expect(!Hotkey.TryFromKeys(true, true, false, false, 0x7C, out _), "F13 is not allowed");
    Expect(Hotkey.TryParse(h.ToString(), out var back) && back == h, "round trip through settings.json text");
});
Test("hotkey: Ctrl+Alt+S is the default; parses, prints, and rejects unsafe or malformed text", () =>
{
    Expect(Hotkey.TryParse("Ctrl+Alt+S", out var h) && h == Hotkey.Default, "default"); Eq(h.ToString(), "Ctrl+Alt+S", "print");
    Expect(Hotkey.TryParse("alt + ctrl + shift + f9", out var f) && f.VirtualKey == 0x78 && f.Modifiers == 7, "F9 combo"); Eq(f.ToString(), "Ctrl+Alt+Shift+F9", "print F9");
    Expect(!Hotkey.TryParse("S", out _), "plain key rejected");
    Expect(!Hotkey.TryParse("Shift+S", out _), "shift-only rejected (would block typing capitals)");
    Expect(!Hotkey.TryParse("Ctrl+Alt", out _), "no key");
    Expect(!Hotkey.TryParse("Ctrl+A+B", out _), "two keys");
    Expect(!Hotkey.TryParse("", out _) && !Hotkey.TryParse(null, out _) && !Hotkey.TryParse("Ctrl+F13", out _), "empty, null, F13");
});
Test("settings: first run writes defaults; changes survive a reload; a broken file falls back to defaults", () =>
{
    var s = Settings.Load();
    Expect(s.FirstRun && s.Enabled && s.DimPercent == Strengths.Default && s.Strength == null && s.Hotkey == "Ctrl+Alt+S", "defaults");
    s.Enabled = false; s.DimPercent = 80; s.Hotkey = "Ctrl+Alt+L"; s.Save();
    var r = Settings.Load();
    Expect(!r.FirstRun && !r.Enabled && r.DimPercent == 80 && r.ParsedHotkey.ToString() == "Ctrl+Alt+L", "saved");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
    var bad = Settings.Load();
    Expect(bad.Enabled && bad.DimPercent == Strengths.Default, "broken file = defaults");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Hotkey\":\"banana\"}");
    Eq(Settings.Load().ParsedHotkey, Hotkey.Default, "bad hotkey text falls back to Ctrl+Alt+S");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"DimPercent\":250}"); Eq(Settings.Load().DimPercent, 100, "too high pulled to 100");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"DimPercent\":-5}"); Eq(Settings.Load().DimPercent, 0, "too low pulled to 0");
});
Test("item 3: settings.json does not save the derived ParsedHotkey block; an old file that has it still loads", () =>
{
    var s = new Settings { Hotkey = "Ctrl+Alt+L" }; s.Save();
    string text = File.ReadAllText(Path.Combine(dir, "settings.json"));
    Expect(!text.Contains("ParsedHotkey"), "ParsedHotkey not written"); Expect(System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("Hotkey").GetString() == "Ctrl+Alt+L", "Hotkey text still written");
    File.WriteAllText(Path.Combine(dir, "settings.json"),
        "{\"Enabled\":false,\"Strength\":\"Strong\",\"Hotkey\":\"Ctrl+Alt+K\",\"ParsedHotkey\":{\"Modifiers\":3,\"VirtualKey\":75}}");
    var old = Settings.Load();
    Expect(!old.Enabled && old.DimPercent == Strengths.FromLegacy(DimStrength.Strong) && old.Hotkey == "Ctrl+Alt+K" && old.ParsedHotkey.ToString() == "Ctrl+Alt+K", "old file with the block loads");
    old.Save();
    string saved = File.ReadAllText(Path.Combine(dir, "settings.json"));
    Expect(!saved.Contains("ParsedHotkey"), "block dropped on next save");
    Expect(!saved.Contains("\"Strength\"") && saved.Contains("\"DimPercent\": " + Strengths.FromLegacy(DimStrength.Strong)), "old Strength word is replaced by the number on the next save");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Strength\":\"Light\",\"DimPercent\":20}");
    Eq(Settings.Load().DimPercent, 20, "a file with both keeps the number");
});
// ---- version 1.2.0 options ------------------------------------------------------------------------
Test("soft edge: layers stack to the chosen darkness far away and build up in equal steps toward the window", () =>
{
    foreach (double d in new[] { 0.2, 0.55, 0.95, 1.0 })
    {
        var plan = SoftEdge.Plan(d);
        Eq(plan.Length, SoftEdge.LayerCount, "layers");
        double through = 1; foreach (var (_, a) in plan) through *= 1 - a;
        Expect(Math.Abs((1 - through) - d) < 1e-9, $"stacked dimming {d}");
        Eq(plan[0].Grow, 0, "first layer = the window itself");
        for (int j = 1; j < plan.Length; j++) Expect(plan[j].Grow > plan[j - 1].Grow, "holes get bigger");
        Eq(plan[^1].Grow, SoftEdge.WidthAt100, "band width");
        double cum = 1;
        for (int j = 0; j < plan.Length; j++) { cum *= 1 - plan[j].Alpha; Expect(Math.Abs((1 - cum) - d * (j + 1) / plan.Length) < 1e-9, "equal steps"); }
    }
    foreach (var (_, a) in SoftEdge.Plan(0)) Eq(a, 0.0, "no dimming = clear layers");
});
Test("soft edge geometry: a bigger hole grows on all sides (more at higher scale) and its corners grow too", () =>
{
    var h0 = Geometry.Holes(one, new Box(100, 200, 900, 700), false, 0)[0];
    var h = Geometry.Holes(one, new Box(100, 200, 900, 700), false, 10)[0];
    Eq(h.Local, new Box(90, 190, 910, 710), "grown"); Eq(h.Radius, h0.Radius + 10, "corner grows");
    var big = Geometry.Holes(mixed, new Box(2000, 100, 3000, 900), false, 10)[0];
    Eq(big.Local.Width, 1000 + 2 * 15, "10 px at 150% = 15");
    Eq(Geometry.Holes(one, new Box(0, 0, 1920, 1040), true, 8)[0].Local, new Box(0, 0, 1920, 1048), "stays on the screen");
});
Test("dimming colors: five choices, unknown name = black, GDI order is blue-green-red", () =>
{
    Eq(Tints.Names.Length, 5, "five"); Eq(Tints.ColorRef("Black"), 0, "black"); Eq(Tints.ColorRef("nonsense"), 0, "unknown = black"); Eq(Tints.IndexOf("blue"), 2, "case does not matter");
    var (r, g, b) = Tints.Color("Blue"); Eq(Tints.ColorRef("Blue"), r | (g << 8) | (b << 16), "packed");
    Expect(Tints.Names.All(n => Tints.Color(n).R < 80 && Tints.Color(n).G < 80 && Tints.Color(n).B < 80), "every color is a dark one");
});
Test("never-dim apps: a listed program in front clears the dimming, others and the taskbar are unchanged", () =>
{
    var skip = new HashSet<string> { "zoom" };
    var zoom = new WindowInfo(1, 100, "ZPContentViewWndClass", new Box(100, 100, 900, 700), ProcessName: "zoom");
    var note = new WindowInfo(2, 101, "Notepad", new Box(100, 100, 900, 700), ProcessName: "notepad");
    Eq(Rules.Classify(zoom, one, 999, skip), Verdict.Clear, "zoom clears"); Eq(Rules.Classify(zoom, one, 999), Verdict.Spotlight, "no list = lit");
    Eq(Rules.Classify(note, one, 999, skip), Verdict.Spotlight, "notepad lit");
    Eq(Rules.Classify(new WindowInfo(3, 4, "Shell_TrayWnd", new Box(0, 1040, 1920, 1080), ProcessName: "explorer"), one, 999, new HashSet<string> { "explorer" }), Verdict.Hold, "taskbar still holds");
    Eq(Rules.NormalizeApp(" Zoom.EXE "), "zoom", "name tidy"); Expect(Rules.MeetingApps.Contains("teams") && Rules.MeetingApps.Contains("zoom"), "meeting list");
    var f = new FollowLogic(999, one) { SkipApps = skip };
    Eq(f.OnForeground(zoom).Kind, DecisionKind.Clear, "follow: clear in zoom"); Eq(f.OnForeground(note).Kind, DecisionKind.Show, "follow: lit again in notepad");
});
Test("picked area: the light stays on it, ignores windows, still obeys off, and polling stays off", () =>
{
    var f = new FollowLogic(999, one) { FixedArea = new Box(200, 200, 800, 600) };
    var d = f.OnForeground(Win(new Box(0, 0, 500, 500)));
    Eq(d.Kind, DecisionKind.Show, "shows"); Eq(d.Frame, new Box(200, 200, 800, 600), "the area"); Expect(!f.ShouldPoll, "no polling");
    Eq(f.OnForeground(Win(new Box(900, 900, 1200, 1000), "Progman")).Frame, new Box(200, 200, 800, 600), "desktop click does not change it");
    Eq(f.OnForeground(null).Kind, DecisionKind.Show, "nothing in front: still lit"); Expect(!f.NeedsRecheck(Win(new Box(0, 0, 10, 10))), "no recheck");
    f.Enabled = false; Eq(f.OnForeground(Win(new Box(0, 0, 500, 500))).Kind, DecisionKind.Clear, "off = clear");
    f.Enabled = true; f.FixedArea = null; Eq(f.OnForeground(Win(new Box(0, 0, 500, 500))).Frame, new Box(0, 0, 500, 500), "back to following the window");
});
Test("picked area: kept only while it still touches a screen", () =>
{
    Eq(Geometry.UsableArea(new Box(100, 100, 500, 500), one), new Box(100, 100, 500, 500), "on screen");
    Eq(Geometry.UsableArea(new Box(1800, 900, 2500, 1500), one), new Box(1800, 900, 2500, 1500), "partly on screen");
    Expect(Geometry.UsableArea(new Box(3000, 3000, 3400, 3400), one) is null, "unplugged screen"); Expect(Geometry.UsableArea(null, one) is null, "none"); Expect(Geometry.UsableArea(new Box(5, 5, 5, 50), one) is null, "empty");
});
Test("notification pop-ups: only the small shell notification window counts, not Start, Search or the notification center", () =>
{
    WindowInfo W(string cls, string proc, Box f, bool vis = true, bool cloaked = false) => new(5, 50, cls, f, vis, false, false, cloaked, true, proc);
    var toast = new Box(1540, 900, 1904, 1010);
    Expect(Rules.IsToast(W("Windows.UI.Core.CoreWindow", "shellexperiencehost", toast)), "a toast");
    Expect(Rules.IsToast(W("Windows.UI.Core.CoreWindow", "shellhost", toast)), "a toast on the newer shell");
    Expect(!Rules.IsToast(W("Windows.UI.Core.CoreWindow", "shellexperiencehost", new Box(1500, 0, 1920, 1040))), "tall notification center is not a toast");
    Expect(!Rules.IsToast(W("Windows.UI.Core.CoreWindow", "startmenuexperiencehost", toast)), "Start menu is not a toast");
    Expect(!Rules.IsToast(W("Notepad", "shellexperiencehost", toast)), "wrong window class");
    Expect(!Rules.IsToast(W("Windows.UI.Core.CoreWindow", "shellexperiencehost", toast, vis: false)) && !Rules.IsToast(W("Windows.UI.Core.CoreWindow", "shellexperiencehost", toast, cloaked: true)), "hidden or cloaked");
    var h = Geometry.Holes(one, toast, false, 0); Eq(h.Count, 1, "a toast gets its own hole");
});
Test("always lit: the taskbar (square) and its pop-ups and menus (rounded) are lit; hidden ones and screen-sized ones are not", () =>
{
    WindowInfo W(string cls, Box f, bool vis = true, bool cloaked = false) => new(9, 60, cls, f, vis, false, false, cloaked, true, "explorer");
    var bar = Rules.ShellPiece(W("Shell_TrayWnd", new Box(0, 1040, 1920, 1080)), one);
    Expect(bar is { Square: true } && bar.Value.Frame == new Box(0, 1040, 1920, 1080), "taskbar, square");
    Expect(Rules.ShellPiece(W("Shell_SecondaryTrayWnd", new Box(1920, 1392, 4480, 1440)), mixed) is { Square: true }, "second screen taskbar");
    Expect(Rules.ShellPiece(W("NotifyIconOverflowWindow", new Box(1500, 800, 1900, 1040)), one) is { Square: false }, "tray pop-up");
    Expect(Rules.ShellPiece(W("#32768", new Box(300, 300, 520, 640)), one) is { Square: false }, "pop-up menu");
    Expect(Rules.ShellPiece(W("Microsoft.UI.Content.PopupWindowSiteBridge", new Box(2087, 999, 2343, 1430)), one) is { Square: false }, "Windows 11 right-click menu");
    Expect(Rules.ShellPiece(W("Shell_TrayWnd", new Box(0, 1040, 1920, 1080), vis: false), one) is null, "hidden");
    Expect(Rules.ShellPiece(W("Shell_TrayWnd", new Box(0, 1040, 1920, 1080), cloaked: true), one) is null, "cloaked");
    Expect(Rules.ShellPiece(W("Xaml_WindowedPopupClass", new Box(0, 0, 1920, 1080)), one) is null, "a screen-sized pop-up would undo the dimming");
    Expect(Rules.ShellPiece(W("CabinetWClass", new Box(100, 100, 600, 400)), one) is null, "a normal Explorer window is not a shell piece");
    Expect(Rules.IsShellClass("#32768") && Rules.IsShellClass("Shell_TrayWnd") && !Rules.IsShellClass("Notepad"), "class list");
    var holes = Geometry.Holes(one, bar!.Value.Frame, bar.Value.Square);
    Eq(holes.Count, 1, "one hole"); Eq(holes[0].Radius, 0, "square corners");
    Expect(new Settings().KeepShellBright && !new Settings().LightNotifications, "taskbar bright by default, notifications off by default");
});
Test("dimming window is never full screen: one pixel row short of every monitor, so Windows does not switch on Do Not Disturb", () =>
{
    foreach (var mons in new[] { one, mixed, negative })
        foreach (var m in mons)
        {
            var o = Geometry.OverlayBounds(m.Bounds);
            Expect(!o.Covers(m.Bounds), "does not cover the whole monitor"); Eq(o.Left, m.Bounds.Left, "left"); Eq(o.Top, m.Bounds.Top, "top"); Eq(o.Width, m.Bounds.Width, "width"); Eq(o.Height, m.Bounds.Height - 1, "one row short");
        }
    Eq(Geometry.Holes(one, new Box(0, 0, 1920, 1040), true)[0].Local, new Box(0, 0, 1920, 1040), "holes still line up from the monitor's corner");
});
Test("quick bar layout: panel parts fit inside the panel in order at every scale; pill parts fit inside the pill", () =>
{
    foreach (double sc in new[] { 1.0, 1.25, 1.5, 2.0 })
    {
        var pill = QuickBarLayout.PillRect(100, 100, sc);
        Eq(pill.Width, (int)Math.Round(151 * sc), "pill width"); Eq(pill.Height, (int)Math.Round(40 * sc), "pill height");
        var local = new Box(0, 0, pill.Width, pill.Height);
        foreach (var part in new[] { QuickBarLayout.Grip(sc), QuickBarLayout.Switch(sc), QuickBarLayout.Label(sc), QuickBarLayout.Chevron(sc) }) Expect(local.Covers(part), $"pill part inside at {sc}");
        Expect(QuickBarLayout.Grip(sc).Right <= QuickBarLayout.Switch(sc).Left && QuickBarLayout.Switch(sc).Right <= QuickBarLayout.Label(sc).Left && QuickBarLayout.Label(sc).Right <= QuickBarLayout.Chevron(sc).Left + 1, "pill parts left to right");
        var p = QuickBarLayout.Panel(sc); var inside = new Box(0, 0, p.Width, p.Height);
        var rows = new List<Box> { p.HowLabel, p.Slider, p.ColorLabel, p.WhereLabel, p.RadioFollow, p.RadioPick, p.Pause, p.Settings, p.Hide };
        rows.AddRange(p.Swatches);
        foreach (var r in rows) Expect(inside.Covers(r), $"panel row inside at {sc}");
        var ordered = new[] { p.HowLabel, p.Slider, p.ColorLabel, p.Swatches[0], p.WhereLabel, p.RadioFollow, p.RadioPick, p.Pause, p.Settings, p.Hide };
        for (int i = 1; i < ordered.Length; i++) Expect(ordered[i].Top >= ordered[i - 1].Bottom - 1, $"rows do not overlap at {sc}");
        for (int i = 1; i < p.Swatches.Count; i++) Expect(p.Swatches[i].Left > p.Swatches[i - 1].Right, "swatches do not overlap");
        Eq(p.Dividers.Count, 3, "dividers");
    }
});
Test("quick bar places: default at bottom center, stays on screen, snaps to edges, panel opens toward the larger side", () =>
{
    var work = new Box(0, 0, 1920, 1040); var all = new Box(0, 0, 1920, 1080);
    var d = QuickBarLayout.DefaultPill(work, 1.0);
    Expect(Math.Abs((d.Left + d.Right) / 2 - 960) <= 1 && d.Top > work.Top && d.Top < work.Top + 60, "default: top center, like the Windows screenshot bar");
    var off = QuickBarLayout.Clamp(new Box(-300, 2000, -149, 2040), all);
    Expect(all.Covers(off) && off.Width == 151 && off.Height == 40, "clamped fully onto the screen, same size");
    var snapped = QuickBarLayout.Snap(new Box(8, 500, 159, 540), work, 18); Eq(snapped.Left, 9, "snaps near the left edge (with a small gap)");
    var far = QuickBarLayout.Snap(new Box(700, 500, 851, 540), work, 18); Eq(far.Left, 700, "far from edges: no snap");
    Expect(!QuickBarLayout.OpensUp(d, work, 1.0), "top pill: the panel drops down beneath it"); Expect(QuickBarLayout.OpensUp(new Box(900, 960, 1051, 1000), work, 1.0), "pill near the bottom: no room below, so up"); Expect(!QuickBarLayout.OpensUp(new Box(900, 500, 1051, 540), work, 1.0), "pill in the middle: there is room below, so it still drops down"); Expect(QuickBarLayout.OpensUp(new Box(900, 700, 1051, 740), work, 1.0), "pill at y=700: only 300 px below, panel needs about 360: opens up");
    var panel = QuickBarLayout.PanelBox(d, work, 1.0); Expect(work.Covers(panel) && panel.Top >= d.Bottom, "panel beneath the pill and on screen");
    var lowPanel = QuickBarLayout.PanelBox(new Box(900, 960, 1051, 1000), work, 1.0); Expect(work.Covers(lowPanel) && lowPanel.Bottom <= 960, "low pill: panel above it and on screen");
    var (sx, sy) = SettingsPlacement.Below(d, work, 470, 800, 10); Expect(sy >= d.Bottom + 10 - 1 && sx >= work.Left && sx + 470 <= work.Right && sy + 800 <= work.Bottom, "settings window right under the pill, on screen");
    var (ux, uy) = SettingsPlacement.Below(new Box(1700, 980, 1851, 1020), work, 470, 800, 10); Expect(uy + 800 <= 980 && ux + 470 <= work.Right, "settings window: no room below, so above the pill, and pulled in from the right edge");
    var edge = QuickBarLayout.PanelBox(new Box(1800, 900, 1951 - 31, 940), work, 1.0); Expect(work.Covers(edge), "panel at the right edge is pulled back onto the screen");
    var w = QuickBarLayout.WindowRect(d, panel, 1.0); Expect(w.Covers(panel) && w.Covers(d), "picture window holds both");
    Eq(QuickBarLayout.StatusText(true, false, 55), "55%", "on"); Eq(QuickBarLayout.StatusText(false, false, 55), "Off", "off"); Eq(QuickBarLayout.StatusText(true, true, 55), "Paused", "paused");
    Eq(QuickBarLayout.NormalizeTheme("light"), "Light", "theme light"); Eq(QuickBarLayout.NormalizeTheme("nonsense"), "Dark", "theme default dark");
});
Test("quick bar settings: on by default, dark glass, position saved, bad theme repaired", () =>
{
    var s = new Settings(); Expect(s.QuickBar && s.QuickBarTheme == "Dark" && s.QuickBarX is null && s.QuickBarY is null, "defaults");
    s.QuickBarTheme = "Light"; s.QuickBarX = 640; s.QuickBarY = 900; s.QuickBar = false; s.Save();
    var r = Settings.Load(); Expect(!r.QuickBar && r.QuickBarTheme == "Light" && r.QuickBarX == 640 && r.QuickBarY == 900, "saved");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"QuickBarTheme\":\"Neon\"}"); Eq(Settings.Load().QuickBarTheme, "Dark", "bad theme = Dark");
});
Test("full screen in front is reported so the quick bar can hide", () =>
{
    var f = new FollowLogic(999, one);
    Expect(!f.FullScreenInFront, "starts false");
    f.OnForeground(Win(new Box(0, 0, 1920, 1080), caption: false)); Expect(f.FullScreenInFront, "full-screen window in front");
    f.OnForeground(Win(new Box(100, 100, 900, 700))); Expect(!f.FullScreenInFront, "normal window: false again");
});
Test("settings file: saved atomically (no half file, no leftover), one bad field keeps the rest, a copy of the damaged file is kept, and the tray gets a one-time message", () =>
{
    var s = new Settings { DimPercent = 70, Tint = "Green", Hotkey = "Ctrl+Alt+L" }; s.NeverDimApps.Add("zoom"); s.Save();
    Expect(!File.Exists(Path.Combine(dir, "settings.json.tmp")), "no temp file left");
    Expect(System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))).RootElement.GetProperty("DimPercent").GetInt32() == 70, "whole file written");
    Settings.TakeProblem();
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"DimPercent\":70,\"Tint\":\"Green\",\"Hotkey\":\"Ctrl+Alt+L\",\"NeverDimApps\":[\"zoom\"],\"Area\":\"oops\",\"SoftEdge\":\"yes\"}");
    var r = Settings.Load();
    Expect(r.DimPercent == 70 && r.Tint == "Green" && r.Hotkey == "Ctrl+Alt+L" && r.NeverDimApps.Contains("zoom") && r.Area is null && !r.SoftEdge, "good fields kept, bad ones back to normal");
    Expect(File.Exists(Path.Combine(dir, "settings.bad.json")), "a copy of the damaged file was kept");
    Expect(Settings.TakeProblem() is { Length: > 20 }, "a message is waiting for the tray"); Expect(Settings.TakeProblem() is null, "and it is shown only once");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "garbage");
    var g = Settings.Load(); Expect(g.DimPercent == Strengths.Default && g.Enabled, "total garbage: defaults"); Expect(Settings.TakeProblem() is not null, "total garbage also leaves a message");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "[1,2,3]"); Expect(Settings.Load().DimPercent == Strengths.Default, "wrong shape: defaults");
    Settings.TakeProblem();
});
Test("quick bar rests on a real screen: never in a gap between screens, never half off, nearest screen wins", () =>
{
    var left = new Box(0, 0, 1920, 1080); var right = new Box(1920, 500, 4480, 1940); // the second screen is lower: a gap above it
    var screens = new[] { left, right };
    var inGap = QuickBarLayout.ClampToScreens(new Box(2200, 100, 2351, 140), screens);
    Expect(right.Covers(inGap) || left.Covers(inGap), "a pill in the empty corner above the right screen is pulled onto a real screen");
    Expect(inGap.Width == 151 && inGap.Height == 40, "same size");
    var between = QuickBarLayout.ClampToScreens(new Box(1850, 520, 2001, 560), screens);
    Expect(right.Covers(between) || left.Covers(between), "a pill straddling two screens ends up wholly on one");
    var far = QuickBarLayout.ClampToScreens(new Box(9000, 9000, 9151, 9040), screens); Expect(right.Covers(far), "far away: the nearest screen (the lower right one)");
    var inside = new Box(300, 300, 451, 340); Eq(QuickBarLayout.ClampToScreens(inside, screens), inside, "already on a screen: unchanged");
    Eq(QuickBarLayout.ClampToScreens(inside, new Box[0]), inside, "no screens known: unchanged");
});
Test("notification pop-ups at higher display scale are still recognized (the size limits grow with the scale)", () =>
{
    WindowInfo W(Box f) => new(5, 50, "Windows.UI.Core.CoreWindow", f, true, false, false, false, true, "shellexperiencehost");
    var big = new Box(3000, 1500, 3000 + 728, 1500 + 220); // a normal toast at 200%
    Expect(!Rules.IsToast(W(big)), "too wide for 100%"); Expect(Rules.IsToast(W(big), 2.0), "fine at 200%");
    Expect(!Rules.IsToast(W(new Box(2800, 100, 3200, 1500)), 2.0), "a tall panel is still not a toast at 200%");
});
Test("a damaged never-dim list (empty, null or odd entries) never stops startup", () =>
{
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"NeverDimApps\":[\"Zoom.exe\",null,\"\",\"  \",\"obs64\",\"zoom\"]}");
    var r = Settings.Load(); Expect(r.NeverDimApps.SequenceEqual(new[] { "zoom", "obs64" }), $"cleaned: {string.Join(",", r.NeverDimApps)}");
    var o = r.ToOptions(); Expect(o.SkipApps.Contains("zoom") && o.SkipApps.Contains("obs64") && o.SkipApps.Count == 2, "engine options built");
    var direct = new Settings(); direct.NeverDimApps.Add(null!); direct.NeverDimApps.Add("vlc"); Expect(direct.ToOptions().SkipApps.SequenceEqual(new[] { "vlc" }), "a null entry set in code does not crash");
    Settings.TakeProblem();
});
Test("settings edits from two copies never lose each other, and a busy file never means defaults", () =>
{
    new Settings { DimPercent = 42 }.Save();
    var a = Task.Run(() => { for (int i = 0; i < 25; i++) Settings.Edit(s => s.NeverDimApps.Add("a" + i)); });
    var b = Task.Run(() => { for (int i = 0; i < 25; i++) Settings.Edit(s => s.NeverDimApps.Add("b" + i)); });
    Task.WaitAll(a, b);
    var r = Settings.Load(); Eq(r.NeverDimApps.Count, 50, "all 50 edits from both threads survived"); Eq(r.DimPercent, 42, "other fields intact");
    using (var fs = new FileStream(Path.Combine(dir, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var busy = Settings.Load(); Eq(busy.DimPercent, 42, "file held open by someone else: the last good settings are used, not the defaults");
        Eq(busy.NeverDimApps.Count, 50, "and the list is intact");
        bool ran = false; var (refused, savedBusy) = Settings.Edit(x => { ran = true; x.DimPercent = 99; });
        Expect(!savedBusy && !ran, "an edit while the file cannot be read is refused and never runs (it would be built on defaults)");
        Eq(refused.DimPercent, 42, "and the settings shown are still the last good ones");
        Expect(Settings.TakeProblem() is not null, "and the user is told");
    }
    Eq(Settings.Load().DimPercent, 42, "nothing was overwritten while the file was busy");
    Settings.ForgetLastGood();
    using (var fs2 = new FileStream(Path.Combine(dir, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var cold = Settings.Load();
        Expect(cold.Unavailable && !cold.Active(DateTime.UtcNow), "a fresh start with the file locked: Spotlight stays off, it does not switch on with the usual choices");
        Expect(Settings.TakeProblem() is not null, "and says why");
    }
    var again = Settings.Load(); Expect(!again.Unavailable && again.DimPercent == 42, "once the file can be read the real choices are back");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "null");
    var (fixedUp, savedNull) = Settings.Edit(x => x.DimPercent = 61);
    Expect(savedNull && fixedUp.DimPercent == 61 && Settings.Load().DimPercent == 61, "a settings file holding just null is repaired by the next change instead of blocking every change");
    Settings.TakeProblem();
    Settings.Edit(x => x.DimPercent = 42);
    // a plain read racing with saves must never see "no file" and write the usual choices over the real ones
    new Settings { DimPercent = 42, Tint = "Blue" }.Save();
    bool stop = false; int bad = 0, reads = 0;
    var saver = Task.Run(() => { int i = 0; while (!Volatile.Read(ref stop)) { Settings.Edit(x => x.Tint = (i++ % 2 == 0) ? "Green" : "Blue"); } });
    var reader = Task.Run(() => { while (!Volatile.Read(ref stop)) { var r = Settings.Load(); reads++; if (r.FirstRun || r.DimPercent != 42) bad++; } });
    Thread.Sleep(2500); Volatile.Write(ref stop, true); Task.WaitAll(saver, reader);
    Eq(bad, 0, $"{reads} plain reads during saves: none saw a missing file or the usual choices");
    Eq(Settings.Load().DimPercent, 42, "and the saved choices are still there");
    Settings.TakeProblem();
    using (var held = new System.Threading.Mutex(false, Instance.SettingsLockName))
    {
        var holder = Task.Run(() => { held.WaitOne(); Thread.Sleep(5500); held.ReleaseMutex(); });
        Thread.Sleep(300);
        bool ran2 = false; var (_, savedLocked) = Settings.Edit(x => { ran2 = true; x.DimPercent = 98; });
        Expect(!savedLocked && !ran2, "an edit while another copy holds the settings lock is refused after the wait, not applied anyway");
        holder.Wait(); Settings.TakeProblem();
    }
    Eq(Settings.Load().DimPercent, 42, "and the file still has the other copy's value");
    var (back, saved) = Settings.Edit(s => s.DimPercent = 43); Expect(saved && back.DimPercent == 43 && Settings.Load().DimPercent == 43, "saves again once the file is free");
    Settings.TakeProblem();
});
Test("settings 1.2.0: new options save and reload, a bad color or area is repaired, pause ends by itself, engine options are built from them", () =>
{
    File.Delete(Path.Combine(dir, "settings.json")); // start from a fresh file, whatever the tests before left behind
    var s = Settings.Load();
    Expect(s.Tint == "Black" && !s.SoftEdge && !s.OneScreenOnly && !s.HideFromCapture && !s.SkipMeetingApps && !s.AreaMode && s.Area is null && s.NeverDimApps.Count == 0 && s.PausedUntilUtc is null, "defaults");
    s.Tint = "Purple"; s.SoftEdge = true; s.OneScreenOnly = true; s.HideFromCapture = true; s.SkipMeetingApps = true; s.NeverDimApps = new() { "Obs64.exe", "vlc" };
    s.Area = new[] { 10, 20, 410, 320 }; s.AreaMode = true; s.PausedUntilUtc = DateTime.UtcNow.AddHours(1); s.Save();
    var r = Settings.Load();
    Expect(r.Tint == "Purple" && r.SoftEdge && r.OneScreenOnly && r.HideFromCapture && r.SkipMeetingApps && r.AreaMode && r.AreaBox == new Box(10, 20, 410, 320), "reloaded");
    Expect(r.IsPaused(DateTime.UtcNow) && !r.Active(DateTime.UtcNow) && r.Active(DateTime.UtcNow.AddHours(2)), "pause: off for an hour, then on again by itself");
    var o = r.ToOptions();
    Expect(o.SkipApps.Contains("obs64") && o.SkipApps.Contains("vlc") && o.SkipApps.Contains("zoom") && o.Area == new Box(10, 20, 410, 320) && o.Tint == "Purple", "engine options (apps tidied, meeting apps added)");
    r.AreaMode = false; Expect(r.ToOptions().Area is null, "area ignored when following windows");
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Tint\":\"Chartreuse\",\"Area\":[5,5,2,2],\"NeverDimApps\":null}");
    var bad = Settings.Load();
    Expect(bad.Tint == "Black" && bad.Area is null && bad.NeverDimApps.Count == 0, "bad color, backwards area and null list are repaired");
    Settings.Change(x => x.DimPercent = 33); Eq(Settings.Load().DimPercent, 33, "Settings.Change saves on top of the newest file");
});
Test("start at sign-in: a test copy uses a file in its own folder, never the real Run key", () =>
{
    Expect(!Settings.StartsAtSignIn(), "off at first");
    Settings.SetStartAtSignIn(true); Expect(Settings.StartsAtSignIn(), "on"); Expect(File.Exists(Path.Combine(dir, "startup-entry.txt")), "in test folder");
    Settings.SetStartAtSignIn(false); Expect(!Settings.StartsAtSignIn(), "off again");
});

Test("only the installed copy may own the start-at-sign-in entry (unins000.exe beside it)", () =>
{
    Expect(Settings.IsInstalledCopy(@"C:\Users\x\AppData\Local\Programs\Spotlight\Spotlight.exe", p => p == @"C:\Users\x\AppData\Local\Programs\Spotlight\unins000.exe"), "installed");
    Expect(!Settings.IsInstalledCopy(@"D:\portable\Spotlight.exe", _ => false), "portable copy");
    Expect(!Settings.IsInstalledCopy(@"C:\Users\x\Downloads\Spotlight.exe", p => p.EndsWith(@"Programs\Spotlight\unins000.exe")), "other copy, installed one elsewhere");
    Expect(!Settings.IsInstalledCopy(null, _ => true) && !Settings.IsInstalledCopy("", _ => true), "no path");
});
Test("a repeating error is logged once, a different one is logged too", () =>
{
    var logFile = Path.Combine(dir, "log.txt");
    for (int i = 0; i < 100; i++) Log.WriteOnce(new InvalidOperationException("tick failed"));
    Expect(Log.WriteOnce(new ArgumentException("another")), "new kind is logged");
    Expect(!Log.WriteOnce(new ArgumentException("another")), "same again is not");
    int lines = File.ReadAllLines(logFile).Count(l => l.Contains("tick failed") && l.Contains("InvalidOperationException"));
    Eq(lines, 1, "log lines for the repeated error");
});

// Clean up this test's own temp folder: delete only the files this test made, then the folder if empty (never a recursive delete).
foreach (var f in new[] { "settings.json", "settings.bad.json", "settings.json.tmp", "startup-entry.txt", "log.txt" }) { try { File.Delete(Path.Combine(dir, f)); } catch { } }
try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }

Console.WriteLine($"\n{passes} passed, {failures} failed");
return failures;
