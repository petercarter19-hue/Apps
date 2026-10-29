"""Find the transcript file (.jsonl) for a Claude or Codex session.

Accepts a Claude app session id (local_...), a Claude CLI session id (a uuid), a Codex session id,
or words from a session title (Claude app titles and Codex thread names). Prints title, ids, cwd, path and
size for each match. Archived Codex sessions are compressed (.jsonl.zst): decompress to scratch before reading.
Usage: python -B find_transcript.py <local_id | uuid | title words>
"""
import glob
import json
import os
import re
import sys
from pathlib import Path

HOME = Path.home()
APP_DATA = Path(os.environ.get("LOCALAPPDATA", HOME / "AppData" / "Local")) / "Claude-Data"
CLAUDE_PROJECTS = HOME / ".claude" / "projects"
CODEX_SESSIONS = HOME / ".codex" / "sessions"
CODEX_ARCHIVE = HOME / ".codex" / "archived_sessions"
CODEX_INDEX = HOME / ".codex" / "session_index.jsonl"
UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", re.I)


def app_sessions():
    """Yield (metadata path, metadata dict) for every Claude desktop app session."""
    for path in glob.glob(str(APP_DATA / "*" / "claude-code-sessions" / "**" / "local_*.json"), recursive=True):
        try:
            with open(path, encoding="utf-8") as f:
                yield path, json.load(f)
        except (OSError, ValueError):
            continue


def claude_transcripts(cli_id):
    return glob.glob(str(CLAUDE_PROJECTS / "*" / f"{cli_id}.jsonl"))


def codex_transcripts(session_id):
    return (glob.glob(str(CODEX_SESSIONS / "**" / f"rollout-*{session_id}*.jsonl"), recursive=True)
            + glob.glob(str(CODEX_ARCHIVE / f"rollout-*{session_id}*.jsonl*")))


def codex_titles():
    """Latest thread name for each Codex session id."""
    names = {}
    try:
        with open(CODEX_INDEX, encoding="utf-8") as f:
            for line in f:
                try:
                    row = json.loads(line)
                except ValueError:
                    continue
                names[row.get("id")] = row.get("thread_name") or ""
    except OSError:
        pass
    return names


def show(title, local_id, cli_id, cwd, paths):
    print(f"{title or '(no title)'}\n  app id: {local_id or '-'}  cli id: {cli_id or '-'}  cwd: {cwd or '-'}")
    if not paths:
        print("  transcript: not found")
    for p in paths:
        note = "  ARCHIVED, compressed: session_overview.py reads it; decompress to scratch for other tools" if p.endswith(".zst") else ""
        print(f"  transcript: {p}  ({os.path.getsize(p) / 1e6:.1f} MB){note}")


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    sys.stdout.reconfigure(encoding="utf-8")
    query = " ".join(sys.argv[1:]).strip()
    found = 0

    if query.startswith("local_") or UUID.match(query):
        for _, meta in app_sessions():
            if query in (meta.get("sessionId"), meta.get("cliSessionId")):
                cli = meta.get("cliSessionId")
                show(meta.get("title"), meta.get("sessionId"), cli, meta.get("cwd"), claude_transcripts(cli) if cli else [])
                found += 1
        if not found and UUID.match(query):
            paths = claude_transcripts(query)
            if paths:
                show("(Claude CLI session)", None, query, None, paths)
                found += 1
            paths = codex_transcripts(query)
            if paths:
                show(codex_titles().get(query) or "(Codex session)", None, query, None, paths)
                found += 1
    else:
        words = query.lower().split()
        for _, meta in app_sessions():
            title = (meta.get("title") or "").lower()
            if all(w in title for w in words):
                cli = meta.get("cliSessionId")
                show(meta.get("title"), meta.get("sessionId"), cli, meta.get("cwd"), claude_transcripts(cli) if cli else [])
                found += 1
        for sid, title in codex_titles().items():
            if all(w in title.lower() for w in words):
                show(f"{title} (Codex)", None, sid, None, codex_transcripts(sid))
                found += 1

    if not found:
        print("No match. For Claude, try search_session_transcripts (include_archived: true) for a topic;")
        print("for Codex, grep the .jsonl files under the sessions folder for a word from the session.")
        sys.exit(1)


if __name__ == "__main__":
    main()
