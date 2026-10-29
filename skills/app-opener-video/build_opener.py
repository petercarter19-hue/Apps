"""Build the opener video project from opener.json.

    python build_opener.py <opener.json> <out_dir>

Everything about the look, layout and motion is fixed in template/steps.tpl.html. opener.json only supplies the app
name, the six steps and, for the voice version, the voice-over recording and the times. With no "voice" it builds the
10-second silent preview. A relative voice "file" is read from the folder opener.json is in.
"""
import html
import json
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
TEMPLATE = HERE / "template"

ICONS = {
    "folder": '<path d="M8 19 a5 5 0 0 1 5 -5 h11 l6 6 h21 a5 5 0 0 1 5 5 v24 a5 5 0 0 1 -5 5 h-38 a5 5 0 0 1 -5 -5 z"/>',
    "message": '<path d="M10 13 h44 a5 5 0 0 1 5 5 v23 a5 5 0 0 1 -5 5 h-25 l-12 9 v-9 h-7 a5 5 0 0 1 -5 -5 v-23 a5 5 0 0 1 5 -5 z"/><path d="M19 26 h26 M19 34 h16"/>',
    "mic": '<rect x="23" y="6" width="18" height="32" rx="9"/><path d="M14 30 a18 18 0 0 0 36 0 M32 48 v9 M23 57 h18"/>',
    "wand": '<path d="M11 53 L39 25 M34 20 l9 9 M46 8 v9 M41.5 12.5 h9 M53 27 v7 M49.5 30.5 h7 M20 11 v7 M16.5 14.5 h7"/>',
    "letters": '<path d="M7 46 L16 18 L25 46 M10.5 37 h11 M32 46 v-16 a7 7 0 0 1 14 0 v16 M32 38 h14"/><path d="M44 13 l5 5 l9 -10"/>',
    "layers": '<path d="M32 9 L55 21 L32 33 L9 21 Z M9 32 L32 44 L55 32 M9 43 L32 55 L55 43"/>',
    "doc": '<path d="M16 7 h22 l12 12 v36 a3 3 0 0 1 -3 3 h-31 a3 3 0 0 1 -3 -3 v-45 a3 3 0 0 1 3 -3 z M38 7 v12 h12 M22 32 h20 M22 41 h20"/>',
    "mail": '<rect x="8" y="14" width="48" height="36" rx="5"/><path d="M10 18 L32 35 L54 18"/>',
    "calendar": '<rect x="9" y="12" width="46" height="42" rx="5"/><path d="M9 24 h46 M21 6 v12 M43 6 v12"/>',
    "chart": '<path d="M10 54 h44 M18 46 v-12 M30 46 v-24 M42 46 v-18"/>',
    "play": '<rect x="8" y="12" width="48" height="40" rx="6"/><path d="M28 23 L42 32 L28 41 Z"/>',
    "globe": '<circle cx="32" cy="32" r="22"/><path d="M10 32 h44 M32 10 a34 34 0 0 1 0 44 M32 10 a34 34 0 0 0 0 44"/>',
    "search": '<circle cx="27" cy="27" r="15"/><path d="M38 38 L54 54"/>',
    "lock": '<rect x="14" y="28" width="36" height="26" rx="5"/><path d="M21 28 v-7 a11 11 0 0 1 22 0 v7"/>',
    "check": '<path d="M12 33 L26 47 L52 19"/>',
    "bolt": '<path d="M36 6 L14 36 h16 l-4 22 L50 26 h-16 z"/>',
    "speaker": '<path d="M10 25 h10 l14 -11 v36 l-14 -11 h-10 z M42 24 a11 11 0 0 1 0 16 M48 18 a19 19 0 0 1 0 28"/>',
}

# fixed layout: six 520px cards, 110px gaps, starting at the title-safe edge; pan when a card would pass x=1728
X0, W, GAP, TOP, H = 192, 520, 110, 330, 340
PANS = [max(0, X0 + i * (W + GAP) + W - 1728) for i in range(6)]
CHECK = ('<svg class="num-check" viewBox="0 0 64 64"><path d="M19 33 L28 42 L45 23" fill="none" stroke="#0a1120" '
         'stroke-width="4.5" stroke-linecap="round" stroke-linejoin="round"/></svg>')


def fail(msg):
    sys.exit("opener.json: " + msg)


def load(path):
    c = json.loads(Path(path).read_text(encoding="utf-8"))
    app = c.get("app", "").strip()
    if not app or len(app) > 14:
        fail('"app" is the short app name for "Your own ___", at most 14 characters (like "voice app")')
    steps = c.get("steps", [])
    if len(steps) != 6:
        fail("an opener always has exactly six steps")
    for i, s in enumerate(steps, 1):
        sub = s.get("sub")
        parts = sub if isinstance(sub, list) else [sub]
        if not s.get("title") or len(s["title"]) > 20:
            fail(f"step {i} needs a title of at most 20 characters")
        if not all(parts) or len(parts) > 3 or len(" ".join(parts)) > 40:
            fail(f"step {i} needs a short line of at most 40 characters (a list of up to 3 parts is allowed)")
        if s.get("icon") not in ICONS:
            fail(f"step {i} icon must be one of: {', '.join(ICONS)}")
        s["parts"] = parts
    return c, app, steps


def preview_times(steps):
    t = {"end": 10.0, "eyebrow": 0.15, "title": [0.25, 0.45], "settle": 8.2, "loop": 7.2, "checks": 8.3,
         "checkGap": 0.08, "finish": 8.7, "steps": []}
    for i, s in enumerate(steps):
        at = round(1.0 + 1.05 * i, 2)
        t["steps"].append({"at": at, "sub": [round(at + 0.15 + 0.15 * k, 2) for k in range(len(s["parts"]))]})
    return t


def voice_times(c, steps):
    v, t = c["voice"], c.get("times")
    if not t:
        fail('the voice version needs "times" (see references/timing.md)')
    if len(t.get("steps", [])) != 6 or len(t.get("title", [])) != 2:
        fail('"times" needs two title times and six step entries')
    for i, (s, ts) in enumerate(zip(steps, t["steps"]), 1):
        sub = ts["sub"] if isinstance(ts["sub"], list) else [ts["sub"]]
        if len(sub) != len(s["parts"]):
            fail(f"step {i} has {len(s['parts'])} line part(s) but {len(sub)} time(s)")
        ts["sub"] = sub
    ats = [s["at"] for s in t["steps"]]
    if ats != sorted(ats):
        fail("step times must go in order")
    end = round(v["to"] - v["from"], 2)
    return {"end": end, "eyebrow": t["eyebrow"], "title": t["title"], "steps": t["steps"],
            "settle": round(t["checks"] - 0.1, 2), "checks": t["checks"], "checkGap": 0.1,
            "loop": round(t["checks"] + 0.5, 2), "finish": t["finish"]}


def clip(t, end):
    return f'data-start="{t}" data-duration="{round(end - t, 2)}"'


def build(cfg_path, out):
    c, app, steps = load(cfg_path)
    base = Path(cfg_path).resolve().parent
    voiced = "voice" in c
    T = voice_times(c, steps) if voiced else preview_times(steps)
    end = T["end"]
    e = html.escape

    header = (f'    <div id="steps-eyebrow" class="clip" {clip(T["eyebrow"], end)} data-track-index="1">TODAY\'S BUILD</div>\n'
              f'    <div id="steps-title" class="clip" {clip(T["title"][0], end)} data-track-index="1">\n'
              f'      <span id="steps-title-in"><span class="title-part">Your own {e(app)},</span> '
              f'<span class="title-part">step by step</span></span>\n    </div>')
    cards = []
    for i, (s, ts) in enumerate(zip(steps, T["steps"]), 1):
        if len(s["parts"]) == 1:
            sub = e(s["parts"][0])
        else:
            sub = " ".join(f'<span class="sub-part">{e(p)}</span>' for p in s["parts"])
        cards.append(
            f'      <div id="card-{i}" class="card clip" style="left: {X0 + (i - 1) * (W + GAP)}px; top: {TOP}px" '
            f'{clip(ts["at"], end)} data-track-index="2">\n'
            '        <div class="card-body">\n'
            '          <div class="slab slab-2"></div>\n'
            '          <div class="slab slab-1"></div>\n'
            '          <div class="card-in">\n'
            '            <div class="card-top">\n'
            f'              <div class="num"><span class="num-digit">{i}</span>{CHECK}</div>\n'
            f'              <svg class="icon" viewBox="0 0 64 64">{ICONS[s["icon"]]}</svg>\n'
            '            </div>\n'
            f'            <div class="card-title">{e(s["title"])}</div>\n'
            f'            <div class="card-sub">{sub}</div>\n'
            '          </div>\n'
            '        </div>\n'
            '      </div>')
    arrows = []
    for i in range(1, 6):
        t = round(T["steps"][i]["at"] - 0.35, 2)
        arrows.append(
            f'      <svg id="arrow-{i}" class="arrow clip" style="left: {X0 + i * (W + GAP) - GAP}px; top: {TOP + H // 2 - 15}px" '
            f'viewBox="0 0 110 30" {clip(t, end)} data-track-index="3"><path class="a-line" d="M28 15 H82"/>'
            '<path class="a-head" d="M71 4 L82 15 L71 26"/></svg>')
    loop = (f'    <div id="steps-loop" class="clip" {clip(T["loop"], end)} data-track-index="4">\n'
            '      <div id="steps-loop-in">\n'
            '        <svg width="34" height="34" viewBox="0 0 34 34"><path d="M27 12 a11 11 0 1 0 1.5 9 M27 5 v7 h-7"/></svg>\n'
            '        <span>Each step: AI builds, you test, <b>"Works. Next step."</b></span>\n'
            '      </div>\n    </div>')
    finish = (f'    <div id="steps-finish" class="clip" {clip(T["finish"], end)} data-track-index="5">\n'
              '      <div id="steps-pill"><svg width="30" height="24" viewBox="0 0 30 24"><path d="M3 12 H26 M18 4 L26 12 L18 20"/></svg>'
              f'<span>Your own {e(app)}, free on your PC</span></div>\n    </div>')
    data = {"end": end, "eyebrow": T["eyebrow"], "title": T["title"], "pans": PANS, "settle": T["settle"],
            "loop": T["loop"], "checks": T["checks"], "checkGap": T["checkGap"], "finish": T["finish"],
            "steps": [{"at": s["at"], "sub": s["sub"]} for s in T["steps"]]}

    scene = (TEMPLATE / "steps.tpl.html").read_text(encoding="utf-8")
    for k, v in {"HEADER": header, "CARDS": "\n".join(cards), "ARROWS": "\n".join(arrows), "LOOP": loop,
                 "FINISH": finish, "DATA": json.dumps(data)}.items():
        scene = scene.replace(f"%%{k}%%", v)
    assert "%%" not in scene

    out = Path(out)
    (out / "compositions").mkdir(parents=True, exist_ok=True)
    (out / "assets" / "fonts").mkdir(parents=True, exist_ok=True)
    for f in (TEMPLATE / "assets" / "fonts").iterdir():
        shutil.copy2(f, out / "assets" / "fonts" / f.name)
    for name in ("hyperframes.json", "package.json"):
        shutil.copy2(TEMPLATE / name, out / name)
    (out / "compositions" / "steps.html").write_text(scene, encoding="utf-8")

    audio = ""
    if voiced:
        v = c["voice"]
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", str(v["from"]), "-t", str(end), "-i", str(base / v["file"]),
                        "-c:a", "aac", "-b:a", "192k", str(out / "assets" / "voice.m4a")], check=True)
        audio = (f'      <audio id="voice" class="clip" src="assets/voice.m4a" data-start="0" data-duration="{end}" '
                 'data-track-index="2" data-volume="1"></audio>\n')
    (out / "index.html").write_text(f'''<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=1920, height=1080" />
    <script src="https://cdn.jsdelivr.net/npm/gsap@3.14.2/dist/gsap.min.js"></script>
    <style>
      * {{ margin: 0; padding: 0; box-sizing: border-box; }}
      html, body {{ margin: 0; width: 1920px; height: 1080px; overflow: hidden; background: #070c18; }}
    </style>
  </head>
  <body>
    <div id="root" data-composition-id="main" data-start="0" data-duration="{end}" data-width="1920" data-height="1080">
      <div id="steps" data-composition-id="steps" data-composition-src="compositions/steps.html" data-start="0"
        data-duration="{end}" data-track-index="1" data-track-kind="graphics" data-width="1920" data-height="1080"></div>
{audio}    </div>
    <script>
      window.__timelines["main"] = gsap.timeline({{ paused: true }});
    </script>
  </body>
</html>
''', encoding="utf-8")
    print(f"built {'voice' if voiced else 'preview'} opener ({end} s) in {out}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    build(sys.argv[1], sys.argv[2])
