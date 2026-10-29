"""Overview of a finished Claude or Codex session (.jsonl) for Skill Maker, without loading the transcript.

Prints: time span, parent session, tool use counts, skills loaded, helpers/sessions started, files written,
and (with --messages) every incoming message: the user's, the opening brief, messages from other sessions or
helpers, compaction summaries. Use it before thread_digest.py, which skips messages from other sessions.
Claude: files are those written with Write/Edit (shell writes aren't tracked). Codex: files come from patches,
skills from SKILL.md reads, and helper message bodies are encrypted (only the header is readable).
Usage: python -B session_overview.py <transcript.jsonl> [--messages] [--max 1500]
"""
import argparse
import collections
import json
import re
import sys
from pathlib import PureWindowsPath

NOISE = ("<local-command", "<command-name>", "<command-message>", "<task-notification>", "<system-reminder>")
PATCH_FILE = re.compile(r"\*\*\* (?:Add|Update|Delete) File: ([^\n\"]+?)(?:\\n|\n|\"|$)")
SKILL_READ = re.compile(r"skills[\\/]+(?:\.system[\\/]+)?([A-Za-z0-9._-]+)[\\/]+SKILL\.md", re.I)


def text_of(content):
    if isinstance(content, str):
        return content
    return "\n".join(i.get("text", "") for i in content or []
                     if isinstance(i, dict) and i.get("type") in ("text", "input_text", "output_text"))


def kind_of(text):
    if text.startswith("Background (from the session"):
        return "BRIEF"
    if text.startswith("This session is being continued"):
        return "SUMMARY"
    if "Another Claude session sent a message" in text[:200] or text.startswith(("<cross-session-message", "<agent-message")):
        return "SESSION MSG"
    return "USER"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--messages", action="store_true", help="also print every incoming message")
    ap.add_argument("--max", type=int, default=1500, help="characters per message")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    tools, skills, started, files = collections.Counter(), collections.Counter(), [], collections.Counter()
    messages, first, last, parent, codex, me = [], "", "", "", False, ""
    if args.path.endswith(".zst"):  # archived Codex session; needs: python -m pip install --user zstandard
        import io
        import zstandard
        f = io.TextIOWrapper(zstandard.ZstdDecompressor().stream_reader(open(args.path, "rb")), encoding="utf-8")
    else:
        f = open(args.path, encoding="utf-8")
    with f:
        for line in f:
            try:
                e = json.loads(line)
            except ValueError:
                continue
            stamp = e.get("timestamp", "")
            if stamp:
                first, last = first or stamp, stamp
            payload = e.get("payload") if isinstance(e.get("payload"), dict) else {}

            # Codex rollout lines
            if e.get("type") == "session_meta":
                codex, parent = True, payload.get("parent_thread_id") or ""
                continue
            ptype = payload.get("type")
            if ptype in ("function_call", "custom_tool_call"):
                name = payload.get("name", "?")
                tools[name] += 1
                body = payload.get("input") or payload.get("arguments") or ""
                for path in PATCH_FILE.findall(body):
                    files[path.strip()] += 1
                for skill in SKILL_READ.findall(body):
                    skills[skill] += 1
                if name == "spawn_agent":
                    try:
                        task = json.loads(body).get("task_name", "")
                    except ValueError:
                        task = ""
                    started.append(f"{stamp[:16]} spawn_agent: {task}")
                continue
            if ptype == "agent_message":
                header = text_of(payload.get("content")).strip()
                kind = "BRIEF" if "Message Type: NEW_TASK" in header and not me else "SESSION MSG"
                me = me or payload.get("recipient", "")
                if payload.get("recipient") == me:
                    messages.append((stamp, kind, f"from {payload.get('author')} (body encrypted): {header}"))
                continue
            if ptype == "message" and payload.get("role") == "user":
                text = text_of(payload.get("content")).strip()
                if text and not text.startswith(("# AGENTS.md", "<")):
                    messages.append((stamp, kind_of(text), text))
                continue

            # Claude lines
            content = e.get("message", {}).get("content")
            if e.get("type") == "assistant" and isinstance(content, list):
                for item in content:
                    if not (isinstance(item, dict) and item.get("type") == "tool_use"):
                        continue
                    name, inp = item.get("name", "?"), item.get("input") or {}
                    tools[name] += 1
                    if name == "Skill":
                        skills[inp.get("skill", "?")] += 1
                    elif name == "Agent" or name.endswith("start_session"):
                        started.append(f"{stamp[:16]} {name}: {inp.get('description') or inp.get('title') or ''}")
                    elif name in ("Write", "Edit", "NotebookEdit") and inp.get("file_path"):
                        files[inp["file_path"]] += 1
            elif e.get("type") == "user" and (not e.get("isMeta") or (e.get("origin") or {}).get("kind") == "peer"):
                if isinstance(content, list) and any(isinstance(i, dict) and i.get("type") == "tool_result" for i in content):
                    continue
                text = text_of(content).strip()
                if text and not text.startswith(NOISE):
                    messages.append((stamp, kind_of(text), text))

    print(f"{'Codex' if codex else 'Claude'} session. Span (UTC): {first[:16]} to {last[:16]}")
    if codex:
        print(f"Parent thread: {parent or 'none (top level)'}")
    else:
        print("Parent session: see get_session (parentSessionId) or the BRIEF message below")
    print("Tools: " + ", ".join(f"{n} {c}" for n, c in tools.most_common()))
    print("Skills loaded: " + (", ".join(f"{n} x{c}" for n, c in skills.most_common()) or "none"))
    print("Helpers/sessions started:" + ("" if started else " none"))
    for s in started:
        print("  " + s)
    scratch = [p for p in files if {"temp", "tmp", "scratchpad"} & {x.lower() for x in PureWindowsPath(p).parts}]
    how = "from patches" if codex else "with Write/Edit; shell writes not tracked"
    print(f"Files written or edited, {how} ({len(files)}, of which {len(scratch)} in temp/scratch, not listed):")
    for p, c in sorted(files.items()):
        if p not in scratch:
            print(f"  {p}  (x{c})")
    kinds = collections.Counter(k for _, k, _ in messages)
    print("Incoming messages: " + (", ".join(f"{k} {c}" for k, c in kinds.items()) or "none"))
    if args.messages:
        for stamp, kind, text in messages:
            print(f"\n{kind} {stamp[:16]}: {text[:args.max]}")


if __name__ == "__main__":
    main()
