"""Dry-run check for the installed copy of VoiceTool (NOT shipped; not part of the zip).

Run it with the INSTALLED copy's Python, from anywhere:

    <install>\\.venv\\Scripts\\python.exe -I dryrun_check.py <install folder>

It never touches the screen, the keyboard, the clipboard or the speakers:
  1. Windows speech (SAPI) writes a short sentence to a WAV file (it is never played).
  2. The installed Transcriber turns the WAV into text, and the installed Cleaner (the on-device model, started
     for real from the install folder) tidies it up. No keyboard hook, no window, no tray icon.
  3. A few spoken commands go through the command router on the same model.
  4. It proves nothing leaves the computer and nothing Azure- or Claude-related runs: AzureCliCredential is patched to
     raise, no claude.exe is started, every network connection is to 127.0.0.1, and llama-server runs from the install
     folder, listening on 127.0.0.1 only.
Exit code 0 = everything passed.
"""
import os
import socket
import subprocess
import sys
import tempfile
import time
import wave
from pathlib import Path

INSTALL = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parent
SENTENCE = "Please send the report to Sam on Monday morning."
results = []


def check(name, ok, detail=""):
    results.append(bool(ok))
    print(("PASS  " if ok else "FAIL  ") + name + (f"  ({detail})" if detail else ""), flush=True)
    return ok


def make_wav(path):
    script = Path(tempfile.gettempdir()) / "vt_sapi_to_wav.ps1"
    script.write_text(
        "Add-Type -AssemblyName System.Speech\n"
        "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer\n"
        "$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, "
        "[System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)\n"
        f"$s.SetOutputToWaveFile('{path}', $fmt)\n"
        "$s.Rate = -1\n"
        f"$s.Speak('{SENTENCE}')\n"
        "$s.Dispose()\n", encoding="utf-8")
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)], check=True, timeout=120)
    script.unlink()


def read_wav(path):
    import numpy as np
    with wave.open(str(path), "rb") as w:
        assert w.getframerate() == 16000 and w.getnchannels() == 1 and w.getsampwidth() == 2
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
    return data.astype(np.float32) / 32768.0


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def main():
    os.chdir(INSTALL)
    sys.path.insert(0, str(INSTALL))
    check("running from the installed copy's own Python", Path(sys.executable).resolve().is_relative_to(INSTALL),
          sys.executable)

    # --- watchers installed BEFORE the app code loads ---
    import azure.identity

    class NoAzure:
        def __init__(self, *a, **k):
            raise RuntimeError("AzureCliCredential must not be used")
    azure.identity.AzureCliCredential = NoAzure

    started = []
    real_popen_init = subprocess.Popen.__init__

    def spy(self, args, *a, **k):
        started.append(args if isinstance(args, str) else " ".join(map(str, args)))
        return real_popen_init(self, args, *a, **k)
    subprocess.Popen.__init__ = spy

    connects, lookups = [], []
    real_connect, real_connect_ex, real_gai = socket.socket.connect, socket.socket.connect_ex, socket.getaddrinfo

    def spy_connect(self, address):
        connects.append(address)
        return real_connect(self, address)

    def spy_connect_ex(self, address):
        connects.append(address)
        return real_connect_ex(self, address)

    def spy_gai(host, *a, **k):
        lookups.append(host)
        return real_gai(host, *a, **k)
    socket.socket.connect, socket.socket.connect_ex, socket.getaddrinfo = spy_connect, spy_connect_ex, spy_gai

    import psutil
    me = psutil.Process()
    before_claude = {p.pid for p in psutil.process_iter(["name"]) if (p.info["name"] or "").lower() == "claude.exe"}

    # --- 1. the sentence, as a WAV file (never played) ---
    wav = Path(tempfile.gettempdir()) / "vt_dryrun_sentence.wav"
    make_wav(wav)
    audio = read_wav(wav)
    check("Windows speech wrote a WAV file of the sentence", len(audio) > 16000, f"{len(audio) / 16000:.1f} s")

    # --- 2. the installed app code, headless ---
    import voicetool as vt
    check("importing voicetool did not start the app (no keyboard hook, no tray icon)",
          not any(t.__class__.__name__ == "KeyboardHook" for t in __import__("threading").enumerate()))
    cfg = vt.CONFIG
    check("config: offline_only is on and no Azure endpoint is set",
          cfg.get("offline_only") is True and not cfg.get("azure_openai_base_url"))
    cfg["offline_port"] = free_port()  # never any port another program (or a real VoiceTool) might be using

    t0 = time.perf_counter()
    transcriber = vt.Transcriber()
    check("Transcriber loaded the speech model from the install folder",
          vt.speech_model_folder() is not None and Path(vt.speech_model_folder()).resolve().is_relative_to(INSTALL),
          f"{time.perf_counter() - t0:.1f} s")
    text = transcriber(audio)
    low = text.lower()
    words_hit = [w for w in ("please", "send", "report", "sam", "monday", "morning") if w in low]
    check("Transcriber heard the sentence", len(words_hit) >= 5 and "report" in low and "monday" in low,
          f"heard: {text!r}")

    cleaner = vt.Cleaner()
    t0 = time.perf_counter()
    messy = "um so please send the the report to sam on monday uh morning"
    tidy = cleaner(messy)
    check("Cleaner (the on-device model) tidied a messy sentence",
          bool(tidy) and "report" in tidy.lower() and "monday" in tidy.lower() and "um " not in tidy.lower()
          and len(tidy) < len(messy) + 20, f"{time.perf_counter() - t0:.1f} s: {tidy!r}")
    tidy2 = cleaner(text)
    check("Cleaner handled the transcribed sentence", bool(tidy2) and "report" in tidy2.lower(), f"{tidy2!r}")

    # --- 3. the command router on the same model ---
    import commands
    client = cleaner._client()
    cases = [("make this shorter", {"selection": "I just wanted to reach out and let you know that I will be a little bit late."}, {"rewrite"}),
             ("write a thank-you note to Sam", {}, {"write"}),
             ("open Notepad", {}, {"open"}),
             ("mute", {}, {"system"}),
             ("note: buy milk", {}, {"note"}),
             ("read this to me", {"selection": "Hello there, this is some selected text."}, {"read"})]
    good = 0
    for said, ctx, want in cases:
        t0 = time.perf_counter()
        try:
            kind, out, extra = commands.route(client, cfg.get("cleanup_model") or "local", said,
                                              dict({"app": "notepad.exe", "title": "Untitled - Notepad"}, **ctx))
        except Exception as e:
            kind, out = f"error: {type(e).__name__}: {e}", ""
        ok = kind in want
        good += ok
        print(f"      route {said!r:42} -> {kind}{'' if ok else '   (wanted ' + '/'.join(want) + ')'}  [{time.perf_counter() - t0:.1f} s]", flush=True)
    check("Command router gave sensible routes", good >= 5, f"{good} of {len(cases)}")

    # --- 4. nothing leaves the computer, nothing Azure or Claude ---
    server = [p for p in psutil.process_iter(["name", "exe", "cmdline"]) if (p.info["name"] or "").lower() == "llama-server.exe"
              and p.info["exe"] and Path(p.info["exe"]).resolve().is_relative_to(INSTALL)]
    check("llama-server is running from the install folder", len(server) == 1,
          server[0].info["exe"] if server else "not found")
    if server:
        p = server[0]
        cmd = " ".join(p.info["cmdline"] or [])
        check("llama-server was started with --host 127.0.0.1", "--host 127.0.0.1" in cmd)
        # (psutil reports this kind of listening socket as status LISTEN or NONE, depending on the Windows build)
        listening = [c for c in p.net_connections(kind="inet") if not c.raddr and c.status in ("LISTEN", "NONE")]
        check("llama-server listens only on 127.0.0.1",
              bool(listening) and all(c.laddr.ip in ("127.0.0.1", "::1") for c in listening)
              and any(c.laddr.port == cfg["offline_port"] for c in listening),
              ", ".join(f"{c.laddr.ip}:{c.laddr.port}" for c in listening))
        check("llama-server uses the model from the install folder", str(INSTALL).lower() in cmd.lower())
        check("backend in use", True, "vulkan (graphics card)" if "--device" in cmd and cleaner.local and
              getattr(cleaner.local, "backend", "") == "vulkan" else f"{getattr(cleaner.local, 'backend', '?')}")
    loop = ("127.0.0.1", "::1", "localhost")
    outside = [a for a in connects if not (isinstance(a, tuple) and str(a[0]) in loop)]
    check("every network connection from the app went to 127.0.0.1", not outside, f"{len(connects)} connections; outside: {outside[:3]}")
    check("no internet name was looked up", all(str(h) in loop for h in lookups), f"lookups: {sorted(set(map(str, lookups)))[:5]}")
    check("AzureCliCredential was never used (it is patched to raise)", getattr(cleaner, "_credential", None) is None)
    claude_cmds = [c for c in started if "claude" in c.lower()]
    after_claude = {p.pid for p in psutil.process_iter(["name"]) if (p.info["name"] or "").lower() == "claude.exe"}
    kids = [c.name().lower() for c in me.children(recursive=True)]
    check("no claude.exe was started by the app", not claude_cmds and "claude.exe" not in kids,
          f"programs started: {sorted(set(os.path.basename(c.split()[0].strip(chr(34))) for c in started))}")
    check("the number of claude.exe processes on the computer did not change because of us", after_claude <= before_claude | set())

    # --- clean up: stop the server we started ---
    try:
        if cleaner.local:
            cleaner.local.stop()
    except Exception as e:
        print("note: stopping the server:", e)
    time.sleep(1.5)
    left = [p for p in psutil.process_iter(["name", "exe"]) if (p.info["name"] or "").lower() == "llama-server.exe"
            and p.info["exe"] and Path(p.info["exe"]).resolve().is_relative_to(INSTALL)]
    check("llama-server stopped when asked", not left)
    try:
        wav.unlink()
    except OSError:
        pass
    print(f"\n{sum(results)} of {len(results)} checks passed", flush=True)
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
