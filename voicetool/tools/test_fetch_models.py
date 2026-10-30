"""Tests for fetch_models.py (the downloader Setup and the Finish setup button use). Not shipped.

All downloads here come from a tiny web server on this computer (127.0.0.1) that misbehaves on purpose: it cuts the
connection part-way through a file, ignores resume requests, sends damaged bytes, or says "not found". Nothing here
touches the real network, the real screen or any real VoiceTool folder.

Run:  .venv\\Scripts\\python.exe -m pytest test_fetch_models.py -q      (or any Python with pytest)
"""
import hashlib
import http.server
import io
import json
import os
import re
import subprocess
import sys
import threading
import zipfile
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))
import fetch_models as fm  # noqa: E402

HERE = Path(__file__).resolve().parent.parent / "src"  # the app code (the tests live in tools\)
BODY = os.urandom(3_000_000)
GOOD = hashlib.sha256(BODY).hexdigest()


class Server:
    """Serves named blobs. Behaviors per path: drops (list of byte counts: connection cut after that many bytes on
    successive requests), ignore_range, corrupt_times, status (always answer with this error)."""

    def __init__(self, files):
        self.files = files
        self.behave = {}
        self.log = []  # (path, Range header or None)
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def do_GET(self):
                name = self.path.lstrip("/")
                rng = self.headers.get("Range")
                outer.log.append((name, rng))
                b = outer.behave.setdefault(name, {})
                if b.get("status"):
                    self.send_response(b["status"])
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                data = outer.files[name]
                if b.get("corrupt_times", 0) > 0:
                    b["corrupt_times"] -= 1
                    data = bytes([data[0] ^ 0xFF]) + data[1:]
                start = 0
                m = re.match(r"bytes=(\d+)-", rng or "")
                if m and not b.get("ignore_range"):
                    start = int(m.group(1))
                    if start >= len(data):
                        self.send_response(416)
                        self.send_header("Content-Length", "0")
                        self.end_headers()
                        return
                    self.send_response(206)
                    self.send_header("Content-Range", f"bytes {start}-{len(data) - 1}/{len(data)}")
                else:
                    self.send_response(200)
                chunk = data[start:]
                self.send_header("Content-Length", str(len(chunk)))
                self.end_headers()
                drops = b.get("drops", [])
                if drops:
                    cut = drops.pop(0)
                    self.wfile.write(chunk[:cut])
                    self.wfile.flush()
                    self.close_connection = True
                    self.connection.close()  # cut mid-file
                    return
                self.wfile.write(chunk)

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.httpd.server_address[1]
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def url(self, name):
        return f"http://127.0.0.1:{self.port}/{name}"

    def close(self):
        self.httpd.shutdown()


def make_zip(marker="llama-server.exe"):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr(marker, b"MZ fake server")
        z.writestr("ggml.dll", b"fake dll")
    return buf.getvalue()


@pytest.fixture
def server():
    z = make_zip()
    s = Server({"big.bin": BODY, "pack.zip": z, "small.txt": b"hello"})
    s.zip = z
    yield s
    s.close()


def item(server, name="big.bin", **kw):
    data = server.files[name]
    d = {"id": name, "label": "the test file", "kind": "file", "name": name, "url": server.url(name),
         "sha256": hashlib.sha256(data).hexdigest(), "size": len(data), "dest": r"models\test\\" + name}
    d["dest"] = r"models\test" + "\\" + name
    d.update(kw)
    return d


def fetcher(tmp_path, items, **kw):
    kw.setdefault("delay_scale", 0)
    return fm.Fetcher(tmp_path / "app", manifest=items, **kw)


# ----- the real file list -----

def test_real_manifest_is_complete_and_sane():
    ids = [i["id"] for i in fm.MANIFEST]
    assert ids[:4] == ["parakeet-config", "parakeet-vocab", "parakeet-decoder", "parakeet-encoder"]
    assert {"llama-vulkan", "llama-cpu", "qwen"} <= set(ids)
    for i in fm.MANIFEST:
        assert re.fullmatch(r"[0-9a-f]{64}", i["sha256"]) and i["size"] > 0
        assert i["url"].startswith("https://")
        assert "\\" in i["dest"]
    urls = {i["id"]: i["url"] for i in fm.MANIFEST}
    assert "ggml-org/llama.cpp/releases/download/b11388/" in urls["llama-vulkan"]
    assert "istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/0bbb45a3365852604aef28b538a8f066f4ccaa85/" in urls["parakeet-encoder"]


def test_no_personal_text_in_the_downloader():
    text = (HERE / "fetch_models.py").read_text(encoding="utf-8").lower()
    for bad in ("x:\\", "x:/", "c:\\users\\"):
        assert bad not in text


# ----- resume, retry, hash -----

def test_connection_drop_resumes_with_range_and_finishes(server, tmp_path):
    server.behave["big.bin"] = {"drops": [700_000, 1_100_000]}  # cut twice, then let it finish
    f = fetcher(tmp_path, [item(server)])
    assert f.run() is True
    dest = tmp_path / "app" / "models" / "test" / "big.bin"
    assert dest.read_bytes() == BODY
    ranges = [r for n, r in server.log if n == "big.bin"]
    assert ranges[0] is None and len(ranges) == 3
    assert ranges[1] == "bytes=700000-"  # the retry asked only for the rest
    assert ranges[2] == f"bytes={700_000 + 1_100_000}-"
    assert not (tmp_path / "app" / "_downloads").exists()  # tidied up


def test_server_that_ignores_resume_still_works(server, tmp_path):
    server.behave["big.bin"] = {"drops": [500_000], "ignore_range": True}
    f = fetcher(tmp_path, [item(server)])
    assert f.run() is True
    assert (tmp_path / "app" / "models" / "test" / "big.bin").read_bytes() == BODY


def test_leftover_partial_file_is_resumed_in_a_later_run(server, tmp_path):
    cache = tmp_path / "cache"
    cache.mkdir()
    (cache / "big.bin.part").write_bytes(BODY[:1_234_567])
    f = fm.Fetcher(tmp_path / "app", cache=cache, manifest=[item(server)], delay_scale=0)
    assert f.run() is True
    assert [r for n, r in server.log] == ["bytes=1234567-"]
    assert (cache / "big.bin").read_bytes() == BODY  # a given cache folder keeps the download


def test_damaged_download_is_discarded_and_fetched_again(server, tmp_path):
    server.behave["big.bin"] = {"corrupt_times": 1}
    f = fetcher(tmp_path, [item(server)])
    assert f.run() is True
    assert (tmp_path / "app" / "models" / "test" / "big.bin").read_bytes() == BODY
    assert len([1 for n, r in server.log if n == "big.bin"]) == 2
    assert [r for n, r in server.log] == [None, None]  # the second try started from zero, not from the bad bytes


def test_always_damaged_gives_up_with_plain_message_and_installs_nothing(server, tmp_path):
    server.behave["big.bin"] = {"corrupt_times": 99}
    f = fetcher(tmp_path, [item(server)])
    assert f.run() is False
    assert "safety check" in f.failed[0][1]
    assert not (tmp_path / "app" / "models" / "test" / "big.bin").exists()


def test_file_not_found_fails_fast_with_plain_message(server, tmp_path):
    server.behave["big.bin"] = {"status": 404}
    f = fetcher(tmp_path, [item(server)])
    assert f.run() is False
    assert "no longer has this file" in f.failed[0][1]
    assert len(server.log) == 1  # no pointless retries


def test_server_error_is_retried_then_succeeds(server, tmp_path):
    f = fetcher(tmp_path, [item(server, "small.txt")])
    server.behave["small.txt"] = {"status": 503}
    seen = {"n": 0}
    real_wait = f._wait

    def flip(*a, **k):
        seen["n"] += 1
        server.behave["small.txt"] = {}
        return real_wait(*a, **k)

    f._wait = flip
    assert f.run() is True and seen["n"] == 1


def test_gives_up_after_the_attempt_limit_when_nothing_moves(server, tmp_path):
    server.behave["small.txt"] = {"status": 503}
    f = fetcher(tmp_path, [item(server, "small.txt")], max_attempts=3)
    assert f.run() is False
    assert "problem" in f.failed[0][1]
    assert len(server.log) == 3


def test_no_internet_message(tmp_path):
    bad = {"id": "x", "label": "the test file", "kind": "file", "name": "x.bin",
           "url": "http://no-such-host.invalid/x.bin", "sha256": "0" * 64, "size": 10, "dest": r"models\x.bin"}
    f = fetcher(tmp_path, [bad], max_attempts=2)
    assert f.run() is False
    assert "couldn't reach the internet" in f.failed[0][1]


# ----- what is already here -----

def test_existing_correct_file_is_not_downloaded_again(server, tmp_path):
    f = fetcher(tmp_path, [item(server)])
    dest = tmp_path / "app" / "models" / "test"
    dest.mkdir(parents=True)
    (dest / "big.bin").write_bytes(BODY)
    assert f.run() is True
    assert server.log == []


def test_existing_wrong_file_is_replaced(server, tmp_path):
    f = fetcher(tmp_path, [item(server)])
    dest = tmp_path / "app" / "models" / "test"
    dest.mkdir(parents=True)
    (dest / "big.bin").write_bytes(BODY[:-1] + b"X")
    assert f.run() is True
    assert (dest / "big.bin").read_bytes() == BODY


def test_zip_is_unpacked_once_and_marked(server, tmp_path):
    z = {"id": "z", "label": "the engine", "kind": "zip", "name": "pack.zip", "url": server.url("pack.zip"),
         "sha256": hashlib.sha256(server.zip).hexdigest(), "size": len(server.zip), "dest": r"runtime\llama-x",
         "marker": "llama-server.exe"}
    f = fetcher(tmp_path, [z])
    assert f.run() is True
    out = tmp_path / "app" / "runtime" / "llama-x"
    assert (out / "llama-server.exe").exists() and (out / "ggml.dll").exists()
    assert f.run() is True and len(server.log) == 1


def test_zip_with_path_escape_is_refused(tmp_path):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr("../evil.txt", b"x")
        zf.writestr("llama-server.exe", b"x")
    s = Server({"bad.zip": buf.getvalue()})
    try:
        z = {"id": "z", "label": "the engine", "kind": "zip", "name": "bad.zip", "url": s.url("bad.zip"),
             "sha256": hashlib.sha256(buf.getvalue()).hexdigest(), "size": len(buf.getvalue()),
             "dest": r"runtime\a", "marker": "llama-server.exe"}
        f = fetcher(tmp_path, [z])
        assert f.run() is False
        assert not (tmp_path / "app" / "runtime" / "evil.txt").exists() and not (tmp_path / "app" / "evil.txt").exists()
    finally:
        s.close()


def test_not_enough_disk_space_is_reported_before_downloading(server, tmp_path, monkeypatch):
    monkeypatch.setattr(fm.shutil, "disk_usage", lambda p: type("U", (), {"free": 1000})())
    f = fetcher(tmp_path, [item(server)])
    with pytest.raises(fm.FetchError, match="enough free space"):
        f.run()
    assert server.log == []


def test_cancel_stops_and_keeps_the_partial_file(server, tmp_path):
    f = fetcher(tmp_path, [item(server)])
    f.state.cancel.set()
    with pytest.raises(fm.Cancelled):
        f.run()


# ----- progress numbers and words -----

def test_time_left_and_sizes_are_plain():
    assert fm.fmt_size(2_497_281_120) == "2.5 GB" and fm.fmt_size(33_282_695) == "33 MB"
    assert fm.fmt_eta(None).startswith("working out")
    assert fm.fmt_eta(20) == "less than a minute left" and fm.fmt_eta(300) == "about 5 minutes left"
    assert fm.fmt_eta(60 * 60 * 3) == "about 3.0 hours left"


def test_status_line_says_what_is_downloading(server, tmp_path):
    f = fetcher(tmp_path, [item(server)])
    seen = []
    real = f.state.set
    f.state.set = lambda s: (seen.append(s), real(s))
    f.run()
    assert any(re.fullmatch(r"Downloading the test file, \d+ of 3 MB\.\.\.", s) for s in seen)


# ----- the graphics card and config.json -----

SAMPLE = """load_backend: loaded RPC backend from ggml-rpc.dll
ggml_vulkan: Found 2 Vulkan devices:
Available devices:
  Vulkan0: AMD Radeon RX 9070 XT (16304 MiB, 15000 MiB free)
  Vulkan1: Microsoft Basic Render Driver (4096 MiB, 4096 MiB free)
"""


def test_gpu_parsing_picks_real_cards_only():
    assert fm.parse_devices(SAMPLE) == [(0, "AMD Radeon RX 9070 XT (16304 MiB, 15000 MiB free)", 16304)]
    assert fm.parse_devices("Available devices:\n  CPU: x\n") == []
    two = "  Vulkan0: Intel(R) UHD Graphics (2048 MiB, 2000 MiB free)\n  Vulkan1: NVIDIA GeForce RTX 3060 (12288 MiB, 11000 MiB free)\n"
    assert [d[0] for d in fm.parse_devices(two)] == [0, 1]


def test_detect_gpu_with_fake_server_outputs(tmp_path, monkeypatch):
    class R:
        def __init__(self, out):
            self.stdout, self.stderr = out, ""
    monkeypatch.setattr(fm.subprocess, "run", lambda *a, **k: R(SAMPLE))
    assert fm.detect_gpu(tmp_path / "llama-server.exe")[0] == 0
    two = "  Vulkan0: Intel(R) UHD Graphics (2048 MiB, 2000 MiB free)\n  Vulkan1: NVIDIA GeForce RTX 3060 (12288 MiB, 11000 MiB free)\n"
    monkeypatch.setattr(fm.subprocess, "run", lambda *a, **k: R(two))
    assert fm.detect_gpu(tmp_path / "llama-server.exe") == (1, "NVIDIA GeForce RTX 3060 (12288 MiB, 11000 MiB free)")
    # a graphics card plus the processor's built-in graphics (which reports shared memory, so more "memory"): the card wins
    both = ("  Vulkan0: AMD Radeon RX 9070 XT (16304 MiB, 15419 MiB free)\n"
            "  Vulkan1: Intel(R) RaptorLake-S Mobile Graphics Controller (32626 MiB, 48100 MiB free)\n")
    monkeypatch.setattr(fm.subprocess, "run", lambda *a, **k: R(both))
    assert fm.detect_gpu(tmp_path / "llama-server.exe")[0] == 0
    monkeypatch.setattr(fm.subprocess, "run", lambda *a, **k: R("Available devices:\n"))
    assert fm.detect_gpu(tmp_path / "llama-server.exe") is None

    def boom(*a, **k):
        raise OSError("not a program")
    monkeypatch.setattr(fm.subprocess, "run", boom)
    assert fm.detect_gpu(tmp_path / "llama-server.exe") is None


def test_configure_keeps_user_choices_and_adds_missing_defaults(tmp_path, monkeypatch):
    root = tmp_path / "app"
    (root / "runtime" / "llama-vulkan").mkdir(parents=True)
    (root / "runtime" / "llama-vulkan" / "llama-server.exe").write_bytes(b"x")
    defaults = tmp_path / "defaults.json"
    defaults.write_text(json.dumps({"dictate_key": "alt_l", "first_run": True, "use_style": True, "new_option": 5}))
    (root / "config.json").write_text(json.dumps({"dictate_key": "f9", "first_run": False, "old_gpu_path": "x"}))
    monkeypatch.setattr(fm, "detect_gpu", lambda server: (1, "Some GPU"))
    assert fm.configure(root, defaults) == "vulkan"
    c = json.loads((root / "config.json").read_text())
    assert c["dictate_key"] == "f9" and c["first_run"] is False  # the user's choices win
    assert c["new_option"] == 5 and c["use_style"] is True  # new defaults are added
    assert c["offline_backend"] == "vulkan" and c["offline_args"][-2:] == ["--device", "Vulkan1"]
    assert c["offline_server"] == r"runtime\llama-vulkan\llama-server.exe"
    assert c["speech_model_path"] == r"models\parakeet-tdt-0.6b-v2"
    monkeypatch.setattr(fm, "detect_gpu", lambda server: None)
    assert fm.configure(root) == "cpu"
    assert json.loads((root / "config.json").read_text())["offline_backend"] == "cpu"


def test_configure_without_the_engine_leaves_backend_unset_and_survives_a_damaged_file(tmp_path):
    root = tmp_path / "app"
    root.mkdir()
    (root / "config.json").write_text("{ not json")
    assert fm.configure(root) is None
    c = json.loads((root / "config.json").read_text())
    assert "offline_backend" not in c and c["offline_model"].endswith("Q4_K_M.gguf")
    kept = list(root.glob("config.json.unreadable-*"))  # the user's damaged file is kept, not thrown away
    assert len(kept) == 1 and kept[0].read_text() == "{ not json"


def test_configure_reads_a_config_with_a_byte_order_mark(tmp_path):
    root = tmp_path / "app"
    root.mkdir()
    (root / "config.json").write_bytes(b"\xef\xbb\xbf" + json.dumps({"dictate_key": "f9", "first_run": False}).encode())
    fm.configure(root, detect=False)
    c = json.loads((root / "config.json").read_text(encoding="utf-8"))
    assert c["dictate_key"] == "f9" and c["first_run"] is False
    assert not list(root.glob("config.json.unreadable-*"))


# ----- the command line (what Setup and the app button run) -----

def run_cli(tmp_path, items, *args, env_extra=None):
    manifest = tmp_path / "manifest.json"
    manifest.write_text(json.dumps(items))
    env = dict(os.environ, VOICETOOL_FETCH_DELAY_SCALE="0", VOICETOOL_FETCH_HIDDEN="1", **(env_extra or {}))
    return subprocess.run([sys.executable, str(HERE / "fetch_models.py"), "--root", str(tmp_path / "app"),
                           "--manifest", str(manifest), *args], capture_output=True, text=True, env=env, timeout=120)


def test_cli_exit_codes_and_final_words(server, tmp_path):
    server.behave["big.bin"] = {"drops": [900_000]}
    r = run_cli(tmp_path, [item(server)])
    assert r.returncode == 0, r.stdout + r.stderr
    assert r.stdout.strip().splitlines()[-1] == "All set."
    assert run_cli(tmp_path, [item(server)], "--check").returncode == 0
    (tmp_path / "app" / "models" / "test" / "big.bin").unlink()
    chk = run_cli(tmp_path, [item(server)], "--check")
    assert chk.returncode == 1 and "missing: big.bin" in chk.stdout
    server.behave["big.bin"] = {"status": 404}
    bad = run_cli(tmp_path, [item(server)])
    assert bad.returncode == 1
    last = bad.stdout.strip().splitlines()[-1]
    assert last.startswith("Couldn't finish: ") and "Finish setup" in last and "dictation still works" in last


def test_cli_window_mode_runs_hidden_and_exits_zero(server, tmp_path):
    pytest.importorskip("tkinter")
    server.behave["big.bin"] = {"drops": [400_000]}
    r = run_cli(tmp_path, [item(server)], "--window")
    assert r.returncode == 0, r.stdout + r.stderr
    assert (tmp_path / "app" / "models" / "test" / "big.bin").read_bytes() == BODY
    server.behave["big.bin"] = {"status": 404}
    (tmp_path / "app" / "models" / "test" / "big.bin").unlink()
    assert run_cli(tmp_path, [item(server)], "--window").returncode == 1


def test_second_copy_at_the_same_time_is_turned_away(server, tmp_path):
    import msvcrt
    root = tmp_path / "app"
    root.mkdir()
    held = open(root / ".fetch_models.lock", "a+")
    msvcrt.locking(held.fileno(), msvcrt.LK_NBLCK, 1)
    try:
        r = run_cli(tmp_path, [item(server)])
        assert r.returncode == 2 and "already downloading" in r.stdout
    finally:
        held.close()


def test_finish_setup_repairs_a_missing_or_damaged_speech_model_file_and_leaves_the_rest_alone(tmp_path, server):
    """Setup's "Finish setup" runs this downloader: a lost or damaged speech-model file is fetched again, nothing else is."""
    root = tmp_path / "app"
    items = [item(server, "small.txt", id="parakeet-vocab", dest=r"models\parakeet-tdt-0.6b-v2\vocab.txt"),
             item(server, "big.bin", id="parakeet-encoder", dest=r"models\parakeet-tdt-0.6b-v2\encoder-model.int8.onnx")]
    first = fm.Fetcher(root, manifest=items, delay_scale=0)
    assert first.run() is True
    folder = root / "models" / "parakeet-tdt-0.6b-v2"
    (folder / "encoder-model.int8.onnx").unlink()  # (the model's biggest file is gone)
    (folder / "vocab.txt").write_bytes(b"damaged")  # (and another is damaged)
    server.log.clear()
    second = fm.Fetcher(root, manifest=items, delay_scale=0)
    assert [i["id"] for i in items if not second.present(i)] == ["parakeet-vocab", "parakeet-encoder"]
    assert second.run() is True
    assert (folder / "vocab.txt").read_bytes() == b"hello" and (folder / "encoder-model.int8.onnx").read_bytes() == BODY
    third = fm.Fetcher(root, manifest=items, delay_scale=0)
    server.log.clear()
    assert third.run() is True and server.log == []  # everything present: nothing is downloaded again


# ----- HTTPS goes through Windows' own certificate verifier (truststore), with certifi as the fallback

def test_downloads_build_their_tls_context_through_truststore_and_pass_it_to_urlopen(monkeypatch, tmp_path, server):
    import ssl
    import types
    made = []
    context = ssl.create_default_context()
    fake = types.ModuleType("truststore")
    fake.SSLContext = lambda protocol: made.append(protocol) or context
    monkeypatch.setitem(sys.modules, "truststore", fake)
    monkeypatch.setattr(fm, "_context", None)
    assert fm.ssl_context() is context and made == [ssl.PROTOCOL_TLS_CLIENT]
    assert fm.ssl_context() is context and len(made) == 1  # (built once)
    seen = []
    real = fm.urllib.request.urlopen
    monkeypatch.setattr(fm.urllib.request, "urlopen", lambda req, **kw: seen.append(kw.get("context")) or real(req, **kw))
    f = fetcher(tmp_path, [item(server, "small.txt")])
    assert f.run() is True  # plain http://127.0.0.1 still works with a context passed in
    assert seen and all(c is context for c in seen)
    assert (tmp_path / "app" / "models" / "test" / "small.txt").read_bytes() == b"hello"


def test_the_real_truststore_is_used_when_it_is_installed_and_certificates_stay_on():
    import ssl
    fm._context = None
    ctx = fm.ssl_context()
    fm._context = None
    assert type(ctx).__module__.startswith("truststore")
    assert ctx.verify_mode == ssl.CERT_REQUIRED and ctx.check_hostname is True


def test_without_truststore_certifis_list_is_used_and_without_both_the_default_context_still_verifies(monkeypatch):
    import ssl
    monkeypatch.setitem(sys.modules, "truststore", None)  # (an import of it now raises ImportError)
    monkeypatch.setattr(fm, "_context", None)
    ctx = fm.ssl_context()
    assert ctx.verify_mode == ssl.CERT_REQUIRED and ctx.check_hostname is True and not type(ctx).__module__.startswith("truststore")
    monkeypatch.setitem(sys.modules, "certifi", None)
    monkeypatch.setattr(fm, "_context", None)
    ctx = fm.ssl_context()
    assert ctx.verify_mode == ssl.CERT_REQUIRED and ctx.check_hostname is True
    source = (HERE / "fetch_models.py").read_text(encoding="utf-8")
    assert "CERT_NONE" not in source and "_create_unverified_context" not in source and "check_hostname = False" not in source


def test_a_certificate_failure_is_explained_in_plain_words_not_as_a_dropped_connection():
    import ssl
    e = fm.urllib.error.URLError(ssl.SSLCertVerificationError(1, "certificate verify failed: unable to get local issuer certificate"))
    assert "doesn't trust the download site's security certificate" in fm.plain_error(e)
