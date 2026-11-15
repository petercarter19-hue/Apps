

import json
import os
import re
import stat
import threading
import time
import uuid
from pathlib import Path

try:
    import msvcrt
    fcntl = None
except ImportError:
    msvcrt = None
    import fcntl

DEFAULT_DICTATE = "alt_l"
DEFAULT_TEACH = "alt_l"

DEFAULT_CONFIG = {
    "dictate_key": "alt_l", "let_key_through": True, "teach_key": "alt_l", "mic_name_contains": "", "hold_seconds": 0.3,
    "double_tap_seconds": 0.4, "speech_model": "nemo-parakeet-tdt-0.6b-v2", "speech_model_path": "models/parakeet-tdt-0.6b-v2",
    "beeps": True, "beep_volume": 0.12, "enabled": True, "dictate_enabled": True, "show_light": True, "typed_fix": True,
    "min_peak": 0.005,
}

READ_TRIES = 6
READ_WAIT_SECONDS = 0.25

def _kind_ok(name, value):

    default = DEFAULT_CONFIG[name]
    if isinstance(default, bool):
        return isinstance(value, bool)
    if isinstance(default, str):
        if not isinstance(value, str):
            return False
        return bool(value.strip()) if name in ("speech_model", "speech_model_path", "dictate_key", "teach_key") else True
    if isinstance(value, bool) or not isinstance(value, (int, float)) or value != value or value in (float("inf"), float("-inf")):
        return False
    return 0 <= value <= (1 if name in ("beep_volume", "min_peak") else 60)

def valid(name, value):

    if name in ("dictate_key", "teach_key"):
        return isinstance(value, str) and value in KEYS
    if name == "theme":
        return value in ("light", "dark")
    return name in DEFAULT_CONFIG and _kind_ok(name, value)

def classify(raw):

    if raw is None:
        return "missing", None, []
    try:
        data = json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, ValueError):
        return "damaged", None, []
    if not isinstance(data, dict):
        return "damaged", None, []
    settings, bad = dict(data), []
    for name, default in DEFAULT_CONFIG.items():
        if name not in data:
            settings[name] = default
        elif not _kind_ok(name, data[name]):
            settings[name] = default
            bad.append(name)
    if not data:
        return "empty", settings, []
    return ("mixed" if bad else "good"), settings, bad

def _free_name(path, stem):

    stamp = time.strftime("%Y%m%d-%H%M%S")
    aside = path.with_name(f"config.{stem}-{stamp}.json")
    for n in range(2, 100):
        if not aside.exists():
            break
        aside = path.with_name(f"config.{stem}-{stamp}-{n}.json")
    return aside

REPLACE_TRIES = 6
_TEMP = re.compile(r"config\.json\.\d+-[0-9a-f]{8}\.tmp")
_LOCK = threading.RLock()

_STATE = {"settings": None, "no_save": False}
_RUN_ONLY = {}
FORCED = {}
on_problem = None

SAVED = "Saved"
NOT_SAVED = "Couldn't save this change. It works until you close Chirpy."

def _read_bytes(path):

    for attempt in range(READ_TRIES):
        try:
            return path.read_bytes()
        except FileNotFoundError:
            raise
        except OSError:
            if attempt == READ_TRIES - 1:
                raise
            time.sleep(READ_WAIT_SECONDS)

def _new_file(path, raw):

    with open(path, "xb") as f:
        f.write(raw)
        f.flush()
        os.fsync(f.fileno())

def _replace_atomic(src, dst):

    for attempt in range(REPLACE_TRIES):
        try:
            os.replace(src, dst)
            return
        except OSError:
            if attempt == REPLACE_TRIES - 1:
                raise
            time.sleep(READ_WAIT_SECONDS)

def _save(path, changes):

    try:
        raw = _read_bytes(path)
    except FileNotFoundError:
        raw = None
    kind, settings, bad = classify(raw)
    base = settings if settings is not None else dict(_STATE["settings"] or DEFAULT_CONFIG)
    new = dict(base, **changes)
    out = {"saved": False, "settings": new, "kind": kind, "bad": bad, "kept": None, "error": None}
    if kind == "good" and new == settings:
        out["saved"] = True
        return out
    tmp = path.with_name(f"{path.name}.{os.getpid()}-{uuid.uuid4().hex[:8]}.tmp")
    try:
        if kind in ("damaged", "empty", "mixed"):
            kept = _free_name(path, "damaged" if kind == "damaged" else "reset")
            _new_file(kept, raw)
            out["kept"] = kept
        _new_file(tmp, (json.dumps(new, indent=2) + "\n").encode("utf-8"))
        _replace_atomic(tmp, path)
        out["saved"] = True
    except Exception as e:
        out["error"] = str(e)
        try:
            tmp.unlink()
        except OSError:
            pass
    return out

def _publish(config, settings=None):

    if settings is not None:
        _STATE["settings"] = dict(settings)
    new = dict(_STATE["settings"] or DEFAULT_CONFIG, **_RUN_ONLY, **FORCED)
    config.update(new)
    for name in [n for n in config if n not in new]:
        del config[name]

def force(config, **settings):

    if _STATE["settings"] is None:
        _STATE["settings"] = {k: v for k, v in config.items() if k not in FORCED}
    FORCED.update(settings)
    _publish(config)

START_PROBLEM = {
    "missing": "Your settings file was missing, so Chirpy started with its usual settings.",
    "empty": "Your settings file was empty, so Chirpy started with its usual settings.",
    "mixed": "Some of your settings couldn't be used, so Chirpy went back to its usual settings for them.",
    "damaged": "Your settings file couldn't be read, so Chirpy started with its usual settings.",
}
START_BUSY = ("Chirpy couldn't open your settings file when it started, so it is using its usual settings. Changes you make "
              "now work until you close Chirpy. Quit Chirpy and start it again to try again.")

def _cleanup_temporaries(path):

    removed, left = [], []
    try:
        entries = list(os.scandir(path.parent))
    except OSError:
        return removed, left
    for entry in entries:
        if not _TEMP.fullmatch(entry.name):
            continue
        try:
            st = os.lstat(entry.path)
        except OSError:
            continue
        if not stat.S_ISREG(st.st_mode) or getattr(st, "st_file_attributes", 0) & 0x400:
            continue
        try:
            os.unlink(entry.path)
            removed.append(entry.name)
        except OSError:
            left.append(entry.name)
    return removed, left

def read_settings(path):

    try:
        raw = _read_bytes(Path(path))
    except FileNotFoundError:
        raw = None
    except OSError:
        return dict(DEFAULT_CONFIG)
    settings = classify(raw)[1]
    return settings if settings is not None else dict(DEFAULT_CONFIG)

def start(path, config):

    path = Path(path)
    _STATE.update(settings=None, no_save=False)
    _RUN_ONLY.clear()
    with _LOCK:
        removed, left = _cleanup_temporaries(path)
        notes = [f"removed a temporary file a cut-off save left: {n}" for n in removed]
        notes += [f"could not remove a temporary file a cut-off save left: {n}" for n in left]
        try:
            out = _save(path, {})
        except OSError as e:
            _STATE["no_save"] = True
            _publish(config, dict(DEFAULT_CONFIG))
            return "; ".join([f"config.json could not be opened ({e}): usual settings, nothing saved this run"] + notes), START_BUSY
        kind, bad = out["kind"], out["bad"]
        _publish(config, out["settings"])
        if kind == "good":
            return ("; ".join(notes) or None), None
        notice = START_PROBLEM[kind]
        what = f"config.json was {kind}" + (f" ({', '.join(bad)})" if bad else "")
        if out["saved"]:
            if out["kept"] is not None:
                notice += f" The earlier file was kept as {out['kept'].name} in Chirpy's folder."
            notes.append(what + "; repaired")
        else:
            if kind == "missing":
                notice += " Chirpy couldn't create a new settings file."
            else:
                notice += " Chirpy couldn't save the repaired settings, so it didn't change the file."
            notes.append(what + f"; the repair could not be saved ({out['error']})")
    return "; ".join(notes), notice

def load_config_and_notice(path):

    config = read_settings(path)
    problem, notice = start(path, config)
    return config, problem, notice

def load_config(path):

    return load_config_and_notice(path)[:2]

def change(path, config, changes):

    if not isinstance(changes, dict) or not changes:
        raise ValueError("no setting to change")
    for name, value in changes.items():
        if not valid(name, value):
            raise ValueError(f"{name!r} can't be set to {value!r}")
    path = Path(path)
    with _LOCK:
        if _STATE["settings"] is None:
            _STATE["settings"] = {k: v for k, v in config.items() if k not in FORCED}
        saved, error = False, "the settings file couldn't be opened when Chirpy started"
        if not _STATE["no_save"]:
            try:
                out = _save(path, changes)
                saved, error = out["saved"], out["error"]
            except OSError as e:
                error = str(e)
        if saved:
            for name in changes:
                _RUN_ONLY.pop(name, None)
            _publish(config, out["settings"])
        else:
            _RUN_ONLY.update(changes)
            _publish(config)
    if not saved and on_problem:
        on_problem(NOT_SAVED, f"config.json: {sorted(changes)} not saved ({error})")
    return {"saved": saved, "toast": SAVED if saved else NOT_SAVED}

class Key:
    def __init__(self, ident, label, ctrl=False, masks=False, let_through=True, language=False):
        self.id = ident
        self.label = label
        self.ctrl = ctrl
        self.masks = masks
        self.let_through = let_through
        self.language = language

KEYS = {k.id: k for k in (
    Key("alt_l", "Left Alt", masks=True, language=True),
    Key("alt_r", "Right Alt", masks=True, language=True),
    Key("ctrl_r", "Right Ctrl", ctrl=True, language=True),
    Key("caps_lock", "Caps Lock", let_through=False),
    *(Key(f"f{n}", f"F{n}") for n in range(13, 25)),
)}
ORDER = list(KEYS)

CONTINUE_SECONDS = 120
_CLOSERS = "\"')]}" + "”’"

def continue_dictation(text, last_typed):

    if not text:
        return text
    before = (last_typed or "").rstrip()
    if not before or text[:1].isspace():
        return text.lstrip() if not before else text
    core = before.rstrip(_CLOSERS) or before
    if core[-1:] in ".?!":
        text = text[:1].upper() + text[1:]
    return " " + text

FEEDBACK_URL = "https://github.com/petercarter19-hue/Apps/issues/new"

def feedback_url(config=None):

    value = (config or {}).get("feedback_url") if isinstance(config, dict) else None
    value = value.strip() if isinstance(value, str) else ""
    ok = value.lower().startswith("https://") and not any(c in value for c in ' <>"' + chr(13) + chr(10) + chr(9))
    return value if ok else FEEDBACK_URL

def app_version(folder):

    try:
        lines = (Path(folder) / ".voicetool-light-from-daily").read_text(encoding="utf-8-sig").splitlines()
        build = lines[0].split()[-1] if lines else ""
        day = next((l.split(":", 1)[1].strip() for l in lines if l.lower().startswith("version:")), "")
        return f"{day} ({build})" if day and build else (day or build or "unknown")
    except Exception:
        return "unknown"

def feedback_link(url, version):

    from urllib.parse import quote
    body = "(Write your bug, comment or request here.)" + chr(10) * 2 + "---" + chr(10) + f"Chirpy version: {version}" + chr(10)
    joiner = "&" if "?" in url else "?"
    return f"{url}{joiner}title={quote('Chirpy feedback: ')}&body={quote(body)}"

def known(ident):
    return isinstance(ident, str) and ident in KEYS

def resolve(config):

    config = config or {}
    dictate, teach = config.get("dictate_key"), config.get("teach_key")
    return (dictate if known(dictate) else DEFAULT_DICTATE), (teach if known(teach) else DEFAULT_TEACH)

def label(ident):
    return KEYS[ident].label

def hold_text(ident):
    return f"Hold {label(ident)}"

def double_tap_text(ident):
    return f"Double-tap {label(ident)}"

def teach_text(ident):
    return f"Shift + tap {label(ident)}"

def teach_hint(ident):

    return f"Highlight a word first, then press Shift + {label(ident)}."

def problem(dictate, teach):

    for ident in (dictate, teach):
        if not known(ident):
            return "That key isn't one of the choices. Pick one from the list."
    return ""

def choices():
    return {"dictate": [{"id": k, "label": KEYS[k].label} for k in ORDER],
            "teach": [{"id": k, "label": "Shift + " + KEYS[k].label} for k in ORDER]}

def phrases(config):

    dictate, teach = resolve(config)
    return {"dictate_key": dictate, "teach_key": teach, "dictate": label(dictate), "hold": hold_text(dictate),
            "double": double_tap_text(dictate), "teach": teach_text(teach), "choices": choices()}

def badges(config):
    dictate, teach = resolve(config)
    return {"dictate_enabled": f"Hold or double-tap {label(dictate)}", "typed_fix": teach_text(teach)}

def features(rows, config):

    named = badges(config)
    return [(s, k, t, named.get(k, b), d) for s, k, t, b, d in rows]
