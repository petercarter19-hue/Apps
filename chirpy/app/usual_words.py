

import difflib
import json
import os
import re
import threading
import time
import uuid
from pathlib import Path

VERSION = 1
MAX_PAIRS = 20000
MAX_WORDS = 8000
MAX_TEXT_WORDS = 400
MAX_HISTORY_BYTES = 2_000_000
MAX_HISTORY_ROWS = 5000
DEFAULT_AFTER = 3
MIN_LENGTH = 4
LIKE = 0.7
LOCK = threading.RLock()

_WORD = re.compile(r"[A-Za-z][A-Za-z'’]*")
_cache = {}

def history_on(config):

    return "keep_history" not in config or config["keep_history"] is True

def after(config):

    value = config.get("usual_words_after", DEFAULT_AFTER)
    return value if isinstance(value, int) and not isinstance(value, bool) and value >= 1 else DEFAULT_AFTER

def words_of(names):

    return {w.lower().replace("’", "'") for name in names for w in _WORD.findall(str(name))}

def _tokens(text):

    out = []
    for found in _WORD.finditer(text):
        a, b = found.start(), found.end()
        before = text[a - 1] if a else ""
        after_ = text[b] if b < len(text) else ""
        plain = not (before.isdigit() or (before and before in "@/\\_#$%.-:") or after_.isdigit() or (after_ and after_ in "@/\\_#$%-:")
                     or (after_ == "." and text[b + 1:b + 2].isalnum()))
        out.append((found.group(0).lower().replace("’", "'"), a, b, plain))
    return out

def _joined(text, first, second):

    return first[3] and second[3] and re.fullmatch(r"[ \t]+", text[first[2]:second[1]]) is not None

def _empty():
    return {"version": VERSION, "words": {}, "pairs": {}}

def _usable(data):
    if not isinstance(data, dict) or not isinstance(data.get("words"), dict) or not isinstance(data.get("pairs"), dict):
        return False
    count = lambda d, parts: all(isinstance(k, str) and len(k.split()) == parts and isinstance(v, int) and not isinstance(v, bool) and v >= 1
                                 for k, v in d.items())
    return count(data["words"], 1) and count(data["pairs"], 2)

def _read(path):

    try:
        data = json.loads(Path(path).read_bytes().decode("utf-8-sig"))
    except (OSError, ValueError):
        return None
    return data if _usable(data) else None

def _indexed(pairs):

    first, second = {}, {}
    for key, count in pairs.items():
        a, b = key.split()
        first.setdefault(a, []).append((b, count))
        second.setdefault(b, []).append((a, count))
    return first, second

def _load(path):

    try:
        info = os.stat(path)
    except OSError:
        _cache.pop(str(path), None)
        return None, None
    signature = (info.st_mtime_ns, info.st_size)
    hit = _cache.get(str(path))
    if hit and hit[0] == signature:
        return hit[1], hit[2]
    data = _read(path)
    index = _indexed(data["pairs"]) if data else None
    _cache[str(path)] = (signature, data, index)
    return data, index

def _add(data, text):
    tokens = _tokens(text)[:MAX_TEXT_WORDS]
    words, pairs = data["words"], data["pairs"]
    for k, token in enumerate(tokens):
        if token[3]:
            words[token[0]] = words.get(token[0], 0) + 1
        if k and _joined(text, tokens[k - 1], token):
            key = f"{tokens[k - 1][0]} {token[0]}"
            pairs[key] = pairs.get(key, 0) + 1

def _trim(data):

    for name, cap in (("pairs", MAX_PAIRS), ("words", MAX_WORDS)):
        table = data[name]
        over = len(table) - cap
        if over > 0:
            items = list(table.items())
            for index in sorted(range(len(items)), key=lambda i: (items[i][1], i))[:over]:
                del table[items[index][0]]

def _write(path, data):

    path = Path(path)
    raw = json.dumps(data, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    tmp = path.with_name(f"{path.name}.{os.getpid()}-{uuid.uuid4().hex[:8]}.tmp")
    try:
        with open(tmp, "xb") as f:
            f.write(raw)
            f.flush()
            os.fsync(f.fileno())
        for attempt in range(4):
            try:
                os.replace(tmp, path)
                return True
            except OSError:
                if attempt == 3:
                    raise
                time.sleep(0.05)
    except OSError:
        try:
            tmp.unlink()
        except OSError:
            pass
        return False

def _last_text(history, tail=65536):

    try:
        with open(history, "rb") as f:
            size = f.seek(0, 2)
            f.seek(max(0, size - tail))
            lines = f.read().decode("utf-8", errors="ignore").splitlines()
    except OSError:
        return None
    if size > tail:
        lines = lines[1:]
    for line in reversed(lines):
        try:
            row = json.loads(line)
        except ValueError:
            continue
        if isinstance(row, dict) and isinstance(row.get("text"), str):
            return row["text"]
    return None

def _history_texts(history):

    try:
        with open(history, "rb") as f:
            size = f.seek(0, 2)
            f.seek(max(0, size - MAX_HISTORY_BYTES))
            lines = f.read().decode("utf-8", errors="ignore").splitlines()
    except OSError:
        return []
    if size > MAX_HISTORY_BYTES:
        lines = lines[1:]
    texts = []
    for line in lines[-MAX_HISTORY_ROWS:]:
        try:
            row = json.loads(line)
        except ValueError:
            continue
        if isinstance(row, dict) and isinstance(row.get("text"), str) and not (isinstance(row.get("usual"), int) and row["usual"] > 0):
            texts.append(row["text"])
    return texts

def count_dictation(path, text, history=None, on=True, changed=False):

    if not on or changed or not text:
        return False
    with LOCK:
        if history is not None and _last_text(history) != text:
            return False
        data = _read(path)
        if data is None:
            data = _empty()
            texts = _history_texts(history) if history is not None else []
            for saved in texts or [text]:
                _add(data, saved)
        else:
            _add(data, text)
        _trim(data)
        return _write(path, data)

def _alike(a, b):
    return len(a) >= MIN_LENGTH and len(b) >= MIN_LENGTH and abs(len(a) - len(b)) <= 3 and difflib.SequenceMatcher(None, a, b).ratio() >= LIKE

def _usual(word, before, following, pairs, index, n):

    by_first, by_second = index
    found = {}
    if before and not pairs.get(f"{before} {word}"):
        for usual, count in by_first.get(before, ()):
            if count >= n and usual != word and _alike(word, usual) and (not following or pairs.get(f"{usual} {following}", 0) >= 1):
                found[usual] = max(found.get(usual, 0), count)
    if following and not pairs.get(f"{word} {following}"):
        for usual, count in by_second.get(following, ()):
            if count >= n and usual != word and _alike(word, usual) and (not before or pairs.get(f"{before} {usual}", 0) >= 1):
                found[usual] = max(found.get(usual, 0), count)
    ranked = sorted(found.items(), key=lambda item: (-item[1], item[0]))
    if not ranked or (len(ranked) > 1 and ranked[0][1] < 2 * ranked[1][1]):
        return None
    return ranked[0][0]

def _cased(heard, usual):
    if len(heard) > 1 and heard.isupper():
        return usual.upper()
    return usual[:1].upper() + usual[1:] if heard[:1].isupper() else usual

def apply(path, text, after=DEFAULT_AFTER, protect=(), on=True, fixes=None):

    data, index = _load(path) if on and text else (None, None)
    if not data:
        return text, 0
    words, pairs = data["words"], data["pairs"]
    tokens = _tokens(text)
    out, last, swapped = [], 0, 0
    fix_count = None
    for k, token in enumerate(tokens):
        word, a, b, plain = token
        if not plain or len(word) < MIN_LENGTH or word in protect or words.get(word, 0) >= after:
            continue
        before = tokens[k - 1][0] if k and _joined(text, tokens[k - 1], token) else None
        following = tokens[k + 1][0] if k + 1 < len(tokens) and _joined(text, token, tokens[k + 1]) else None
        usual = _usual(word, before, following, pairs, index, after)
        if not usual:
            continue
        near = (re.search(r"(\S+)\s+$", text[:a]), re.match(r"\s+(\S+)", text[b:]))
        if any(side and re.search(r"\d", side.group(1)) for side in near):
            continue
        new = _cased(text[a:b], usual)
        if fixes is not None:
            candidate = "".join(out) + text[last:a] + new + text[b:]
            if fix_count is None:
                fix_count = fixes(text)[1]
            candidate_count = fixes(candidate)[1]
            if candidate_count > fix_count:
                continue
            fix_count = candidate_count
        out.append(text[last:a])
        out.append(new)
        last = b
        swapped += 1
    out.append(text[last:])
    return "".join(out), swapped
