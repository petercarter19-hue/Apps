

import difflib

from spelling import americanize
import light_files
import json
import re
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
FILE = HERE / "corrections.json"
PENDING = HERE / "corrections_pending.json"
USUAL = HERE / "usual_words.json"
AUTO_LEARN_AFTER = 2
NUMBER = "<number>"
_NUMBER = re.compile(r"[$€£]?\d+(?:[.,:/-]\d+)*%?")
_EDGE = ".,!?;:\"“”()"
MAX_FIX_WORDS = 5

SPOKEN_SYMBOLS = {"at", "dot", "period", "point", "comma", "colon", "semicolon", "underscore", "dash", "hyphen", "slash",
                  "backslash", "hash", "hashtag", "plus", "equals"}

_lock = threading.Lock()

def _heard(text):

    return " ".join(str(text).split()).strip(".,!?;:\"“”()").strip()
_cache = {"mtime": None, "pairs": [], "rules": []}

def _read(path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError):
        return default

def _edge(word):
    return str(word).strip().strip(_EDGE).strip()

def kind_of(word):

    return NUMBER if _NUMBER.fullmatch(_edge(word)) else ""

def _side(word):

    return kind_of(word) or _edge(word).lower()

def _phrase(heard):
    return r"(?<!\w)" + r"\s+".join(map(re.escape, heard.split())) + r"(?!\w)"

def _neighbors(text, start, end):

    before = re.search(r"(\S+)\s+$", text[:start])
    after = re.match(r"\s+(\S+)", text[end:])
    before = before.group(1) if before and not before.group(1).endswith(tuple(",.;:!?")) else ""
    return _edge(before), _edge(after.group(1) if after else "")

def _fits(before, after, text, found):

    got = dict(zip(("before", "after"), _neighbors(text, found.start(), found.end())))
    for side, want in (("before", before), ("after", after)):
        if want and (kind_of(got[side]) != NUMBER if want == NUMBER else _side(got[side]) != want):
            return False
    return True

def _context_rule(row):

    match = row.get("match")
    if not isinstance(match, dict):
        return None
    heard, meant, before, after = match.get("heard"), match.get("meant"), match.get("before", ""), match.get("after", "")
    if not all(isinstance(v, str) for v in (heard, meant, before, after)):
        return None
    heard, meant = _heard(heard), " ".join(meant.split())
    before, after = _side(before), _side(after)
    if not heard or not meant or heard.lower() == meant.lower() or not (before or after):
        return None
    return heard, meant, before, after

def _read_rows():
    return light_files.read_fixes(FILE)

def load():

    try:
        mtime = FILE.stat().st_mtime
    except FileNotFoundError:
        return []
    if mtime != _cache["mtime"]:
        pairs, plain, context = [], [], []
        for d in _read_rows():
            heard, meant = d.get("heard") or d.get("wrote") or d.get("from"), d.get("meant") or d.get("to")
            if not isinstance(heard, str) or not isinstance(meant, str):
                continue
            heard, meant = _heard(heard), meant.strip()
            if not heard or not meant or heard.lower() == meant.lower():
                continue
            pairs.append((heard, meant))
            if "match" not in d:
                plain.append((heard, meant, "", ""))
            elif (rule := _context_rule(d)):
                context.append(rule)
        longest = lambda r: -len(r[0])
        _cache.update(mtime=mtime, pairs=sorted(pairs, key=lambda p: -len(p[0])), rules=sorted(context, key=longest) + sorted(plain, key=longest))
    return _cache["pairs"]

def rules():

    return _cache.get("rules", []) if load() else []

def apply_counted(text):

    text = americanize(text)
    n = 0
    for heard, meant, before, after in rules():
        hits = [0]

        def swap(found, meant=meant, before=before, after=after, hits=hits):
            if (before or after) and not _fits(before, after, found.string, found):
                return found.group(0)
            hits[0] += 1
            return meant
        text = re.sub(_phrase(heard), swap, text, flags=re.IGNORECASE)
        n += hits[0]
    return text, n

def protected_words():

    return {w.lower() for _, meant in load() for w in re.findall(r"[A-Za-z][A-Za-z'’]*", meant)}

def around(heard, meant, recent=""):

    h, m = _heard(heard).split(), " ".join(str(meant).split()).split()
    same = lambda a, b: _edge(a).lower() == _edge(b).lower()
    i = 0
    while i < min(len(h), len(m)) and same(h[i], m[i]):
        i += 1
    j = 0
    while j < min(len(h), len(m)) - i and same(h[-1 - j], m[-1 - j]):
        j += 1
    core_h, core_m = h[i:len(h) - j], m[i:len(m) - j]
    if not core_h or not core_m:
        return None
    before, after = (h[i - 1] if i else ""), (h[len(h) - j] if j else "")
    if not (before or after) and recent:
        found = list(re.finditer(_phrase(" ".join(core_h)), recent, re.IGNORECASE))
        if len(found) == 1:
            left, right = _neighbors(recent, found[0].start(), found[0].end())
            before, after = kind_of(left), kind_of(right)
    sides = {k: v for k, v in (("before", _side(before)), ("after", _side(after))) if v}
    if NUMBER in sides.values():
        sides = {k: v for k, v in sides.items() if v == NUMBER}
    return {"heard": " ".join(core_h), "meant": " ".join(core_m), **sides} if sides else None

def context_summary(row):

    if "match" not in row:
        return ""
    rule = _context_rule(row)
    if not rule:
        return "Inactive because its saved condition cannot be read."

    def label(side):
        return "a number" if side == NUMBER else f"“{side}”"

    heard, meant, before, after = rule
    if before == after == NUMBER:
        condition = "between two numbers"
    elif before and after:
        condition = f"between {label(before)} and {label(after)}"
    elif before:
        condition = f"when {label(before)} comes right before it"
    else:
        condition = f"when {label(after)} comes right after it"
    return f"“{heard}” becomes “{meant}” {condition}."

def last_dictation_text(path, tail=65536):

    try:
        with open(path, "rb") as f:
            size = f.seek(0, 2)
            f.seek(max(0, size - tail))
            lines = f.read().decode("utf-8", errors="ignore").splitlines()
    except OSError:
        return ""
    if size > tail:
        lines = lines[1:]
    for line in reversed(lines):
        try:
            row = json.loads(line)
        except ValueError:
            continue
        if isinstance(row, dict) and isinstance(row.get("text"), str) and row["text"].strip():
            return row["text"]
    return ""

def _row(heard, meant, source, match):
    row = {"heard": heard, "meant": meant, "added": time.strftime("%Y-%m-%d"), "source": source}
    if isinstance(match, dict) and match:
        row["match"] = match
    return row

def _edited_match(old, heard, meant, selected):

    if not old or "match" not in old:
        return selected
    previous = _context_rule(old)
    if not previous:

        return {"heard": heard, "meant": meant}
    _, _, before, after = previous
    carried = {"heard": heard, "meant": meant}
    if before:
        carried["before"] = before
    if after:
        carried["after"] = after
    if selected:
        carried.update(selected)
    return carried

def apply(text):
    return apply_counted(text)[0]

def describe():

    return "; ".join(f"{h} -> {m}" for h, m in load()[:60])

def problem(heard, meant):

    heard, meant = _heard(heard), " ".join(meant.split())
    if not heard or not meant:
        return "Fill in both sides."
    if heard.lower() == meant.lower():
        return "The two sides must be different."
    if len(heard.split()) > MAX_FIX_WORDS:
        return f'"It wrote" can be {MAX_FIX_WORDS} words at most.'
    return ""

def _same(row, heard):
    return str(row.get("heard") or "").lower() == str(heard).lower()

def _fixes(change):

    out = light_files.change_fixes(FILE, change)
    _cache["mtime"] = None
    return {"saved": out["saved"], "failed": not out["saved"], "kept": out["kept"], "error": out["error"],
            "reason": "" if out["saved"] else light_files.NOT_SAVED}

def add_saved(heard, meant, source, match=None):

    heard, meant = _heard(heard), " ".join(meant.split())
    why = problem(heard, meant)
    if why:
        return {"saved": False, "failed": False, "kept": None, "error": None, "reason": why}

    def change(rows):
        data = [d for d in rows if not _same(d, heard)]
        data.append(_row(heard, meant, source, match))
        return data
    return _fixes(change)

def add(heard, meant, source, match=None):
    return add_saved(heard, meant, source, match)["saved"]

def edit_saved(old_heard, heard, meant, source, match=None, expected_context=None):

    heard, meant = _heard(heard), " ".join(meant.split())
    why = problem(heard, meant)
    if why:
        return {"saved": False, "failed": False, "kept": None, "error": None, "reason": why}
    old = " ".join(str(old_heard).split())
    stale = "This fix changed. Reopen Words & fixes to see the current rule."

    def change(rows):
        at = next((i for i, d in enumerate(rows) if _same(d, old)), len(rows))
        original = rows[at] if at < len(rows) else {}
        if expected_context is not None and context_summary(original) != expected_context:
            raise ValueError(stale)
        carried = _edited_match(original, heard, meant, match)
        keep = [d for i, d in enumerate(rows) if i != at and not _same(d, heard)]
        at -= sum(1 for i, d in enumerate(rows) if i < at and _same(d, heard))
        keep.insert(at, _row(heard, meant, source, carried))
        return keep
    result = _fixes(change)
    if result["error"] == stale:
        result.update(failed=False, reason=stale)
    return result

def edit(old_heard, heard, meant, source, match=None):
    return edit_saved(old_heard, heard, meant, source, match)["saved"]

def delete_saved(heard):
    return _fixes(lambda rows: [d for d in rows if not _same(d, heard)])

def _words(text):
    return re.findall(r"[\w'’-]+", text)

def learn_from(raw, cleaned, known):

    known_lower = {k.lower(): k for k in known}
    a, b = _words(raw), _words(cleaned)
    learned = []
    for op, i1, i2, j1, j2 in difflib.SequenceMatcher(a=[w.lower() for w in a], b=[w.lower() for w in b]).get_opcodes():
        if op != "replace" or i2 - i1 > 4 or j2 - j1 > 3:
            continue
        if any(w.lower() in SPOKEN_SYMBOLS for w in a[i1:i2]):
            continue
        heard, meant = " ".join(a[i1:i2]), " ".join(b[j1:j2])
        target = known_lower.get(meant.lower())
        if not target:
            continue
        if any(h.lower() == heard.lower() and m.lower() == target.lower() for h, m in load()):
            continue
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

    out = light_files.add_word(HERE / "words.txt", word)
    return {"saved": out["saved"], "kept": out["kept"], "error": out["error"]}
