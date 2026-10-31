"""The on-device model: every model call VoiceTool makes (tidy-up, commands, rewriting, journal, meeting notes) is
answered by a small model running on this computer, so nothing you say leaves it.

What runs: llama.cpp's llama-server.exe with Qwen3-4B-Instruct-2507 (Q4_K_M, about 2.5 GB). VoiceTool starts the server
itself in the background, listening only on 127.0.0.1, and stops it when VoiceTool quits.

Where things are (config.json; relative paths are relative to this folder):
- offline_server       the Vulkan build (uses the graphics card), runtime\\llama-vulkan\\llama-server.exe
- offline_server_cpu   the CPU build, runtime\\llama-cpu\\llama-server.exe
- offline_backend      "vulkan" or "cpu": what Setup found works on this computer
- offline_model        the .gguf file
- offline_port, offline_args

If the Vulkan server doesn't come up, VoiceTool falls back to the CPU server once (until it is restarted) and logs it.
The CPU server gets the same arguments without the graphics-card ones (-ngl, --device).

The server dies with VoiceTool: it is put in a Windows "job" that ends it when VoiceTool's process ends for any reason
(Quit, Stop VoiceTool.cmd, a crash). If the port is already in use by a server running OUR model it is reused (and left
running when VoiceTool quits, because VoiceTool didn't start it); a different program on the port is left alone and
VoiceTool starts its own server on a free port instead.

LocalClient is the one place where the requests written for OpenAI's service are adapted for this server.
"""
import inspect
import json
import os
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
CREATE_NO_WINDOW = 0x08000000
DEFAULT_SERVER = r"runtime\llama-vulkan\llama-server.exe"
DEFAULT_SERVER_CPU = r"runtime\llama-cpu\llama-server.exe"
DEFAULT_MODEL = r"models\qwen3-4b-instruct-2507\Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
DEFAULT_PORT = 8766
BASE_ARGS = ["-c", "16384", "--parallel", "1", "--no-webui"]
GPU_ARGS = ["-ngl", "99", "--device", "Vulkan0"]
GPU_FLAGS = {"-ngl", "--n-gpu-layers", "--gpu-layers", "--device", "-dev"}  # each is followed by its value
MAX_FAILED_STARTS = 2  # after this many failed server starts in a run, VoiceTool stops trying until it is restarted
UNAVAILABLE_TEXT = ("The tidy-up model won't start on this computer, so {effect}. Try restarting VoiceTool. "
                    "If it keeps happening, run Setup again to repair it.")


class ModelServerUnavailable(RuntimeError):
    """The model's program failed to start more than once in this run. VoiceTool stops retrying (each try can take a
    minute and a half) and shows one plain card instead."""


def resolve(value, base=None):
    """A path from config.json as an absolute Path: %VARIABLES% expanded, and a relative path taken from this folder."""
    path = Path(os.path.expandvars(str(value))).expanduser()
    return path if path.is_absolute() else Path(base or HERE) / path


def cpu_args(args):
    """The server's arguments without the graphics-card ones (-ngl N, --device NAME)."""
    out, skip = [], False
    for a in args:
        if skip:
            skip = False
        elif str(a) in GPU_FLAGS:
            skip = True
        else:
            out.append(a)
    return out


# ----- running on a processor only (no graphics card) -----
#
# A 4B model on a laptop's processor reads its prompt at tens of tokens a second and writes a few a second, so the same
# request that takes a second on a graphics card can take a minute. Three things keep that workable: every request has a
# bounded reply (see commands.py), the server is told how many cores to use and never to write more than a request could
# need, and the wait allowed for a request (request_timeout, the ONE place that decides) is worked out from how much
# the request has to read and write, at speeds a modest 4-core laptop reaches.
CPU_PREFILL_TOKENS_PER_SECOND = 20.0   # reading the prompt (the command router's instructions are ~1,400 tokens)
CPU_WRITE_TOKENS_PER_SECOND = 3.0      # writing the reply
CPU_DEADLINE_OVERHEAD_SECONDS = 30     # waking the model up, the request waiting behind another, slack
CPU_DEADLINE_MAX_SECONDS = 600
SERVER_PREDICT_LIMIT = 2560            # the server never writes more than this for one request (meeting notes ask for 2,500)
CPU_PROMPT_CACHE_MIB = 1024            # memory the server may keep for reusing the start of earlier prompts
CPU_STARTUP_SECONDS = 240              # a processor-only PC may need this long to read the 2.5 GB model from a slow disk
GPU_STARTUP_SECONDS = 90
MAX_CPU_THREADS = 12                   # more cores than this stops helping (writing is limited by memory speed)


def estimate_prompt_tokens(messages):
    """A quick, slightly high guess at how many tokens a request's messages are (3 characters each; pictures count 0)."""
    chars = 0
    for message in messages or []:
        content = message.get("content") if isinstance(message, dict) else None
        if isinstance(content, str):
            chars += len(content)
        elif isinstance(content, list):
            chars += sum(len(part.get("text") or "") for part in content if isinstance(part, dict))
    return -(-chars // 3)


def request_timeout(prompt_tokens, max_tokens, cpu, base=60):
    """Seconds one request to the model on this computer may take before VoiceTool gives up.
    On a graphics card (cpu False) that is `base` (offline_timeout_seconds, 60), as it always was. On a processor it is
    the time to read the prompt plus the time to write a reply of up to `max_tokens` (None: as long as the server allows),
    plus some slack, never less than `base` and never more than ten minutes."""
    if not cpu:
        return base
    write = SERVER_PREDICT_LIMIT if not max_tokens else min(int(max_tokens), SERVER_PREDICT_LIMIT)
    seconds = (CPU_DEADLINE_OVERHEAD_SECONDS + max(0, int(prompt_tokens)) / CPU_PREFILL_TOKENS_PER_SECOND
               + write / CPU_WRITE_TOKENS_PER_SECOND)
    return max(base, min(CPU_DEADLINE_MAX_SECONDS, round(seconds)))


def physical_cores():
    """How many real cores this computer has (not hyper-threads), at least 1."""
    try:
        import psutil
        count = psutil.cpu_count(logical=False)
    except Exception:
        count = None
    return max(1, count or (os.cpu_count() or 2) // 2)


def _has_flag(args, *names):
    return any(str(a) in names or str(a).split("=")[0] in names for a in args)


def cpu_server_args(args, cores=None):
    """`args` plus what a processor-only server needs, for each of these the person's own config didn't already say:
    -t (threads: the real cores, up to MAX_CPU_THREADS), -n (the most one request may write) and -cram (how much memory
    the prompt cache may use)."""
    out = list(args)
    if not _has_flag(out, "-t", "--threads"):
        out += ["-t", str(min(MAX_CPU_THREADS, cores or physical_cores()))]
    if not _has_flag(out, "-n", "--predict", "--n-predict"):
        out += ["-n", str(SERVER_PREDICT_LIMIT)]
    if not _has_flag(out, "-cram", "--cache-ram"):
        out += ["-cram", str(CPU_PROMPT_CACHE_MIB)]
    return out


def configured_backend(config=None):
    return "cpu" if str((config or {}).get("offline_backend") or "vulkan").lower() == "cpu" else "vulkan"


def _server_path(config, backend):
    if backend == "cpu":
        return resolve(config.get("offline_server_cpu") or DEFAULT_SERVER_CPU)
    return resolve(config.get("offline_server") or DEFAULT_SERVER)


def paths(config=None, backend=None):
    """(llama-server.exe, model.gguf) for this backend (default: the configured one, or the other build if only that
    one is installed), if both files exist; else None."""
    config = config or {}
    model = resolve(config.get("offline_model") or DEFAULT_MODEL)
    first = configured_backend(config)
    for b in ([backend] if backend else [first, "cpu" if first == "vulkan" else "vulkan"]):
        server = _server_path(config, b)
        if server.exists() and model.exists():
            return str(server), str(model)
    return None


def backend_for(config=None):
    """Which build `paths` would use: "vulkan" or "cpu" (the configured one unless only the other is installed)."""
    config = config or {}
    first = configured_backend(config)
    other = "cpu" if first == "vulkan" else "vulkan"
    return first if paths(config, first) or not paths(config, other) else other


def available(config=None):
    """True when llama.cpp and the model are installed (no network needed to check)."""
    return paths(config) is not None


def adapt_params(kwargs):
    """OpenAI-style request settings made right for llama-server: reasoning_effort (OpenAI only) is dropped,
    max_completion_tokens becomes max_tokens, response_format is kept (llama-server takes json_object), and
    temperature 0 is the default (these are short, exact jobs, and the small model is steadier that way)."""
    kw = dict(kwargs)
    kw.pop("reasoning_effort", None)
    if "max_completion_tokens" in kw:
        value = kw.pop("max_completion_tokens")
        if value is not None:
            kw.setdefault("max_tokens", value)
    kw.setdefault("temperature", 0)
    return kw


class _Completions:
    def __init__(self, owner):
        self._owner = owner

    def create(self, **kwargs):
        params = adapt_params(kwargs)
        if "timeout" not in params:
            wait = self._owner.deadline(params.get("messages"), params.get("max_tokens"))
            if wait is not None:
                params["timeout"] = wait
        reply = self._owner._client.chat.completions.create(**params)
        try:
            reply._voicetool_free = True  # usage.py logs these as free
        except Exception:
            pass
        return reply


class _Chat:
    def __init__(self, owner):
        self.completions = _Completions(owner)


class LocalClient:
    """Looks like the OpenAI client the rest of VoiceTool uses (chat.completions.create, with_options), but talks to the
    on-device server and adapts the requests in one place."""

    def __init__(self, client, server=None, base_timeout=60, fixed_timeout=False):
        """server: the LocalCleaner whose program this talks to (it says whether that is on a processor only).
        fixed_timeout: the caller chose the wait itself (with_options(timeout=...)), so it is never replaced."""
        self._client = client
        self._server, self._base_timeout, self._fixed_timeout = server, base_timeout, fixed_timeout
        self.chat = _Chat(self)

    def with_options(self, **options):
        return LocalClient(self._client.with_options(**options), self._server, self._base_timeout,
                           self._fixed_timeout or "timeout" in options)

    def on_cpu(self):
        try:
            return bool(self._server is not None and self._server.on_cpu())
        except Exception:
            return False

    def deadline(self, messages, max_tokens):
        """The wait to give one request, or None to leave it to the client's own timeout (the graphics card, or a wait the
        caller chose): the decision is request_timeout's."""
        if self._fixed_timeout or not self.on_cpu():
            return None
        wait = request_timeout(estimate_prompt_tokens(messages), max_tokens, True, self._base_timeout)
        try:  # a request that has to queue behind the start-up warm-up waits for that too
            return wait + max(0, int(self._server.warm_up_wait()))
        except Exception:
            return wait


def _get(port, path, timeout=2):
    """(status, parsed JSON or None) from the local server, or (None, None) if nothing answers."""
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=timeout) as reply:
            body = reply.read().decode("utf-8", "replace")
            try:
                return reply.status, json.loads(body)
            except ValueError:
                return reply.status, None
    except urllib.error.HTTPError as e:
        return e.code, None
    except Exception:
        return None, None


def _port_in_use(port):
    with socket.socket() as s:
        s.settimeout(0.5)
        return s.connect_ex(("127.0.0.1", port)) == 0


def _free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def _kill_with_me(proc, log=None):
    """Put the server in a Windows job that kills it when this program's process ends, however it ends. Returns the job
    (keep it alive: closing it is what ends the server), or None if Windows said no (then only stop() ends it)."""
    try:
        import win32api
        import win32con
        import win32job
        job = win32job.CreateJobObject(None, "")
        info = win32job.QueryInformationJobObject(job, win32job.JobObjectExtendedLimitInformation)
        info["BasicLimitInformation"]["LimitFlags"] |= win32job.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        win32job.SetInformationJobObject(job, win32job.JobObjectExtendedLimitInformation, info)
        handle = win32api.OpenProcess(win32con.PROCESS_SET_QUOTA | win32con.PROCESS_TERMINATE, False, proc.pid)
        win32job.AssignProcessToJobObject(job, handle)
        win32api.CloseHandle(handle)
        return job
    except Exception as e:
        if log:
            log.warning("the model server isn't tied to VoiceTool's process (%s); Quit still stops it", e)
        return None


class LocalCleaner:
    def __init__(self, config=None, log=None, command=None, startup_seconds=None):
        """command: for tests, a function (port) or (port, backend) -> the argument list that starts a llama-server
        stand-in. startup_seconds: how long to wait for the model to load (default: 90 s on a graphics card, 240 s on a
        processor, which reads it more slowly)."""
        self.config, self.log, self.command, self._startup_seconds = config or {}, log, command, startup_seconds
        self.proc = None  # the server we started (None if we are reusing one that was already running)
        self.port = None
        self.reused = False
        self.backend = backend_for(self.config)  # "vulkan" or "cpu"; changes once if the Vulkan server won't start
        self.fell_back = False
        self.failed_starts = 0  # failed server starts (a Vulkan start that fails and then a CPU start that works counts none)
        self.gave_up = False
        self._warm_until = 0.0  # (monotonic time) while the start-up warm-up may still be running
        self._job = None
        self._lock = threading.RLock()

    # ----- the server -----

    def _model_name(self):
        return resolve(self.config.get("offline_model") or DEFAULT_MODEL).name.lower()

    def _is_ours(self, port):
        """True if the server on this port is running our model."""
        status, props = _get(port, "/props")
        text = json.dumps(props).lower() if props else ""
        return status == 200 and self._model_name() in text

    @property
    def startup_seconds(self):
        if self._startup_seconds is not None:
            return self._startup_seconds
        return CPU_STARTUP_SECONDS if self.on_cpu() else GPU_STARTUP_SECONDS

    def _base_args(self):
        configured = self.config.get("offline_args")
        if configured is None:
            return BASE_ARGS + (GPU_ARGS if self.backend == "vulkan" else [])
        return list(configured) if self.backend == "vulkan" else cpu_args(configured)

    def on_cpu(self):
        """True when the model runs on the processor: the CPU build, or any build started without graphics-card options."""
        return self.backend == "cpu" or not _has_flag(self._base_args(), *GPU_FLAGS)

    def _args(self):
        args = self._base_args()
        return cpu_server_args(args) if self.on_cpu() else args

    def _default_command(self, port):
        found = paths(self.config, self.backend)
        if found is None:
            raise RuntimeError("the model's files aren't there")
        server, model = found
        return [server, "-m", model, "--host", "127.0.0.1", "--port", str(port), *self._args()]

    def _command_for(self, port):
        if self.command is None:
            return self._default_command(port)
        if len(inspect.signature(self.command).parameters) >= 2:
            return self.command(port, self.backend)
        return self.command(port)

    def running(self):
        """True if a server (ours or reused) is up and answering."""
        if self.port is None:
            return False
        if self.proc is not None and self.proc.poll() is not None:
            return False
        return _get(self.port, "/health", 1)[0] == 200

    def start(self):
        """Make sure a server is running; waits until it answers (about 4 s to load the model). Safe to call often.
        If the Vulkan server won't come up, the CPU one is tried once (and used from then on). After two failed starts
        in a run it stops trying and raises ModelServerUnavailable straight away (restart VoiceTool, or switch the
        tidy-up off and on, to try again)."""
        with self._lock:
            if self.running():
                return
            if self.gave_up:
                raise ModelServerUnavailable(UNAVAILABLE_TEXT.format(effect="VoiceTool can't use it"))
            try:
                self._start_with_fallback()
            except (RuntimeError, TimeoutError, OSError) as e:
                if self.gave_up:
                    raise ModelServerUnavailable(UNAVAILABLE_TEXT.format(effect="VoiceTool can't use it")) from e
                raise
            self.failed_starts = 0

    def _count_failure(self):
        self.failed_starts += 1
        if self.failed_starts >= MAX_FAILED_STARTS:
            self.gave_up = True
            if self.log:
                self.log.warning("the model server failed to start %d times; not trying again until VoiceTool restarts",
                                 self.failed_starts)

    def _start_with_fallback(self):
        try:
            self._start_once()
        except (RuntimeError, TimeoutError, OSError) as e:
            self._count_failure()
            if self.gave_up or not self._can_fall_back():
                raise
            if self.log:
                self.log.warning("the Vulkan model server didn't start (%s); switching to the CPU one", str(e)[:120])
            self.backend, self.fell_back = "cpu", True
            try:
                self._start_once()
            except (RuntimeError, TimeoutError, OSError):
                self._count_failure()
                raise

    def refresh(self):
        """Pick up changed settings (the downloader has just written which build to use) and allow starting again. A
        server that is already running is left alone."""
        with self._lock:
            self.failed_starts, self.gave_up = 0, False
            if not self.running():
                self.backend, self.fell_back = backend_for(self.config), False

    def _can_fall_back(self):
        if self.backend != "vulkan" or self.fell_back:
            return False
        if self.command is not None:
            return True
        return paths(self.config, "cpu") is not None

    def _start_once(self):
        wanted = int(self.config.get("offline_port") or DEFAULT_PORT)
        self.proc, self.reused, self._job = None, False, None
        if _port_in_use(wanted):
            deadline = time.monotonic() + self.startup_seconds
            while _get(wanted, "/health", 2)[0] == 503 and time.monotonic() < deadline:
                time.sleep(0.3)  # a server that is still loading its model
            if self._is_ours(wanted):
                self.port, self.reused = wanted, True
                if self.log:
                    self.log.info("on-device model: using the server already running on port %d", wanted)
                return
            port = _free_port()  # something else is on our port: leave it alone
            if self.log:
                self.log.info("on-device model: port %d is used by another program; using port %d", wanted, port)
        else:
            port = wanted
        started = time.perf_counter()
        self.port = port
        try:
            self.proc = subprocess.Popen(self._command_for(port), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                         creationflags=CREATE_NO_WINDOW)
        except OSError as e:  # the program is blocked, damaged or not there: a start that failed, like any other
            self.proc = None
            raise RuntimeError(f"the model program couldn't be started ({e.__class__.__name__})") from e
        self._job = _kill_with_me(self.proc, self.log)
        deadline = time.monotonic() + self.startup_seconds
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                self.proc = None
                raise RuntimeError("the model program stopped while starting")
            if _get(port, "/health", 2)[0] == 200:
                if self.log:
                    self.log.info("on-device model ready in %.1fs (port %d, %s)", time.perf_counter() - started, port,
                                  self.backend)
                return
            time.sleep(0.2)
        self.stop()
        raise TimeoutError("the model took too long to start")

    def start_in_background(self):
        """Start the server without making anyone wait for it (VoiceTool start-up, a switch turned on)."""
        self.failed_starts, self.gave_up = 0, False  # (a person turned it on, or VoiceTool just started: try afresh)
        if self.on_cpu():  # a request that arrives meanwhile may queue behind the warm-up: it is given that time too
            self._warm_until = time.monotonic() + request_timeout(1500, 1, True)

        def job():
            try:
                try:
                    self.start()
                except Exception as e:
                    if self.log:
                        self.log.warning("on-device model not started: %s", str(e)[:200])
                    return
                self.warm_up()
            finally:
                self._warm_until = 0.0
        threading.Thread(target=job, daemon=True, name="local-model").start()

    def warm_up_wait(self):
        """Seconds the start-up warm-up may still keep the server busy (0 once it is over, or on a graphics card)."""
        return max(0.0, self._warm_until - time.monotonic())

    def warm_up(self):
        """Processor only: read the command router's instructions once, right after the model loads, so the first spoken
        command doesn't pay for reading them (the server keeps what it read for the next request that starts the same
        way). It also pulls the model off the disk. Writes one token; a failure is only logged. True if it ran."""
        if not self.on_cpu():
            return False
        try:
            import commands
            self.client().chat.completions.create(model="local", messages=commands.warm_up_messages(),
                                                  max_completion_tokens=1)
            if self.log:
                self.log.info("on-device model warmed up")
            return True
        except Exception as e:
            if self.log:
                self.log.info("on-device model warm-up skipped: %s", str(e)[:120])
            return False

    def stop(self):
        """Stop the server we started. (A server we only reused is left running.)"""
        with self._lock:
            proc, self.proc, job, self._job = self.proc, None, self._job, None
            if proc is not None:
                try:
                    proc.kill()
                    proc.wait(timeout=5)
                except Exception:
                    pass
            if job is not None:
                try:
                    job.Close()
                except Exception:
                    pass

    # ----- the client everything else uses -----

    def client(self, timeout=60):
        """A LocalClient for the running server (starting it first if needed)."""
        from openai import OpenAI
        self.start()
        return LocalClient(OpenAI(base_url=f"http://127.0.0.1:{self.port}/v1", api_key="local", timeout=timeout,
                                  max_retries=0), self, timeout)
