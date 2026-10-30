"""What VoiceTool's calls cost: one line per model call in usage.jsonl.

- Calls answered by the model on this computer are free: logged with a cost of zero, never at any online price.
- Azure OpenAI (only if an Azure endpoint is set in config.json and "Keep everything on this computer" is off): billed
  per token. Priced with PRICES below (list prices, per million tokens).
- Claude (email/calendar actions, if turned on): runs on your own Claude plan, so there is no extra bill. Claude Code
  reports what the same work would cost at API prices; it's logged as "plan equivalent".
- Speech-to-text runs on this computer: free. Logged as minutes only.
"""
import json
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
FILE = HERE / "usage.jsonl"
PRICES = {  # US$ per 1M tokens: input, cached input, output
    "gpt-5.4-mini": (0.75, 0.075, 4.50),
}
_lock = threading.Lock()


def _write(entry):
    entry = {"at": time.strftime("%Y-%m-%d %H:%M:%S"), **entry}
    try:
        with _lock, open(FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry) + "\n")
    except Exception:
        pass  # never let cost tracking break dictation


def azure(reply, model, purpose):
    """Log a chat reply's tokens and cost. A reply from the on-device model (marked by local_cleanup_win.LocalClient)
    is logged as free."""
    try:
        u = reply.usage
        cached = getattr(getattr(u, "prompt_tokens_details", None), "cached_tokens", 0) or 0
        tin, tout = u.prompt_tokens or 0, u.completion_tokens or 0
        if getattr(reply, "_voicetool_free", False):
            _write({"service": "local", "model": "on-device", "purpose": purpose, "input": tin, "cached": 0,
                    "output": tout, "usd": 0, "paid_by": "free (this PC)"})
            return
        p_in, p_cached, p_out = PRICES.get(model, (0, 0, 0))
        cost = ((tin - cached) * p_in + cached * p_cached + tout * p_out) / 1_000_000
        _write({"service": "azure", "model": model, "purpose": purpose, "input": tin, "cached": cached,
                "output": tout, "usd": round(cost, 6), "paid_by": "Azure credits"})
    except Exception:
        pass


def claude(usd, model, purpose="action"):
    _write({"service": "claude", "model": model, "purpose": purpose, "usd": round(usd or 0, 6),
            "paid_by": "Claude plan (no extra bill)"})


def speech(seconds, kind):
    _write({"service": "speech", "purpose": kind, "seconds": round(seconds, 1), "usd": 0, "paid_by": "free (this PC)"})
