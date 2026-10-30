"""Words VoiceTool has learned it mishears, and their fixes.

corrections.json is a list of {"heard": "cooper nettys", "meant": "Kubernetes", "added": "2026-10-22", "source": "..."}.
Fixes are applied to whole words or phrases, ignoring case, longest first, before and after the tidy-up.

They are learned two ways:
- "fix that": select the wrong words, hold the command key, say "that should be ...". Learned at once.
- automatically: when the tidy-up turns the same heard words into one of your words.txt words twice, that pair is
  learned. Only fixes that land on a word you listed are learned, so ordinary tidy-up edits never become rules.
"""
import difflib

from spelling import americanize
import json
import re
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
FILE = HERE / "corrections.json"
PENDING = HERE / "corrections_pending.json"  # pairs seen once, waiting for a second sighting
AUTO_LEARN_AFTER = 2
MAX_FIX_WORDS = 5

# Words people say for symbols ("jane dot doe at example dot com"). The tidy-up turns them into . and @, so they
# vanish from its words; a learned fix must never swallow them.
SPOKEN_SYMBOLS = {"at", "dot", "period", "point", "comma", "colon", "semicolon", "underscore", "dash", "hyphen", "slash",
                  "backslash", "hash", "hashtag", "plus", "equals"}

_lock = threading.Lock()


def _heard(text):
    """The heard side as it's matched: spaces tidied and no punctuation at the ends. A fix saved as "Zef fir." would
    otherwise swallow the full stop that ends the sentence."""
    return " ".join(str(text).split()).strip(".,!?;:\"“”()").strip()
_cache = {"mtime": None, "pairs": []}


def _read(path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError):
        return default


def load():
    """[(heard, meant)], longest heard first. Also accepts a plain {"heard": "meant"} file."""
    try:
        info = FILE.stat()
        mtime = (info.st_mtime_ns, info.st_size)  # (the size too: two quick saves can share one timestamp)
    except FileNotFoundError:
        return []
    if mtime != _cache["mtime"]:
        data = _read(FILE, [])
        if isinstance(data, dict):
            pairs = list(data.items())
        else:
            pairs = [(d.get("heard") or d.get("wrote") or d.get("from"), d.get("meant") or d.get("to"))
                     for d in data if isinstance(d, dict)]
        pairs = [(_heard(h), m.strip()) for h, m in pairs if h and m]
        pairs = [(h, m) for h, m in pairs if h and h.lower() != m.lower()]
        _cache.update(mtime=mtime, pairs=sorted(pairs, key=lambda p: -len(p[0])))
    return _cache["pairs"]


def apply_counted(text):
    """(text with the learned fixes applied, how many replacements were made). Spelling changes aren't counted."""
    text = americanize(text)  # the speech model sometimes writes British spellings
    n = 0
    for heard, meant in load():
        # A function, so a backslash in "meant" (C:\Users) is pasted as typed, not read as a template escape.
        text, k = re.subn(r"(?<!\w)" + r"\s+".join(map(re.escape, heard.split())) + r"(?!\w)", lambda m, meant=meant: meant,
                          text, flags=re.IGNORECASE)
        n += k
    return text, n


def apply(text):
    return apply_counted(text)[0]


def describe():
    """For the tidy-up prompt: 'cooper nettys -> Kubernetes; ...'."""
    return "; ".join(f"{h} -> {m}" for h, m in load()[:60])


def problem(heard, meant):
    """Why a fix can't be saved, in plain words, or "" if it can. The same rules for adding and editing."""
    heard, meant = _heard(heard), " ".join(meant.split())
    if not heard or not meant:
        return "Fill in both sides."
    if heard.lower() == meant.lower():
        return "The two sides must be different."
    if len(heard.split()) > MAX_FIX_WORDS:
        return f'"It wrote" can be {MAX_FIX_WORDS} words at most.'
    return ""


def _rows():
    data = _read(FILE, [])
    return [{"heard": h, "meant": m} for h, m in data.items()] if isinstance(data, dict) else data


def add(heard, meant, source):
    heard, meant = _heard(heard), " ".join(meant.split())
    if problem(heard, meant):
        return False
    with _lock:
        data = [d for d in _rows() if (d.get("heard") or "").lower() != heard.lower()]  # newest fix for a phrase wins
        data.append({"heard": heard, "meant": meant, "added": time.strftime("%Y-%m-%d"), "source": source})
        FILE.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    return True


def edit(old_heard, heard, meant, source):
    """Change a fix in place, both sides, in one locked write. Another fix for the new heard words is replaced, as
    when adding. If the old fix is gone (forgotten meanwhile), the edited one is added."""
    heard, meant = _heard(heard), " ".join(meant.split())
    if problem(heard, meant):
        return False
    old = " ".join(str(old_heard).split()).lower()
    with _lock:
        data = _rows()
        at = next((i for i, d in enumerate(data) if (d.get("heard") or "").lower() == old), len(data))
        keep = [d for i, d in enumerate(data) if i != at and (d.get("heard") or "").lower() != heard.lower()]
        at -= sum(1 for i, d in enumerate(data) if i < at and (d.get("heard") or "").lower() == heard.lower())
        keep.insert(at, {"heard": heard, "meant": meant, "added": time.strftime("%Y-%m-%d"), "source": source})
        FILE.write_text(json.dumps(keep, indent=2, ensure_ascii=False), encoding="utf-8")
    return True


def _words(text):
    return re.findall(r"[\w'’-]+", text)


def learn_from(raw, cleaned, known):
    """Spot heard -> known-word fixes the tidy-up made; learn a pair once it has been seen AUTO_LEARN_AFTER times."""
    known_lower = {k.lower(): k for k in known}
    a, b = _words(raw), _words(cleaned)
    learned = []
    for op, i1, i2, j1, j2 in difflib.SequenceMatcher(a=[w.lower() for w in a], b=[w.lower() for w in b]).get_opcodes():
        if op != "replace" or i2 - i1 > 4 or j2 - j1 > 3:
            continue
        if any(w.lower() in SPOKEN_SYMBOLS for w in a[i1:i2]):  # "at example dot" -> "Example" ate the @ and the .
            continue
        heard, meant = " ".join(a[i1:i2]), " ".join(b[j1:j2])
        target = known_lower.get(meant.lower())
        if not target:
            continue
        if any(h.lower() == heard.lower() and m.lower() == target.lower() for h, m in load()):
            continue  # already a saved fix: re-learning would overwrite its label and date and move it to the end
        with _lock:
            pending = _read(PENDING, {})
            key = f"{heard.lower()}\t{target}"
            pending[key] = pending.get(key, 0) + 1
            ready = pending[key] >= AUTO_LEARN_AFTER
            if ready:
                pending.pop(key)
            PENDING.write_text(json.dumps(pending, indent=2, ensure_ascii=False), encoding="utf-8")
        if ready and add(heard, target, "auto"):
            learned.append((heard, target))
    return learned


def remember_word(word):
    """Add a fixed word to words.txt so the tidy-up spells it right too."""
    path = HERE / "words.txt"
    existing = path.read_text(encoding="utf-8").splitlines() if path.exists() else []
    if word.lower() not in {w.strip().lower() for w in existing}:
        with open(path, "a", encoding="utf-8") as f:
            f.write(("" if not existing or existing[-1] == "" else "\n") + word + "\n")
