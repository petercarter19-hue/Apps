"""Reading aloud on the PC: Windows' own voices (SAPI, the same engine as Narrator's classic voices), nothing to download
and nothing sent anywhere. One voice setting (config.json "voice") serves both "read this to me" and spoken answers.

Speaker.say(text) starts reading at once (replacing anything still being read); Speaker.stop() cuts it off. Both are safe
to call from any thread and return immediately: one background thread owns the voice, because Windows' speech engine
(COM) must be used from the thread that made it. Speaking is asynchronous with "purge before speak", so a new answer, or
Esc (VoiceTool calls stop()), takes effect straight away.
"""
import queue
import threading

SVS_ASYNC, SVS_PURGE_BEFORE_SPEAK = 1, 2
MAX_CHARS = 1500  # a spoken answer is read only as far as this unless the caller allows more (read-aloud does)

_voices_cache = []


def list_voices():
    """The names of the Windows voices installed on this PC (asked once, then remembered)."""
    if not _voices_cache:
        import pythoncom
        import win32com.client
        pythoncom.CoInitialize()
        try:
            tokens = win32com.client.Dispatch("SAPI.SpVoice").GetVoices()
            _voices_cache.extend(tokens.Item(i).GetDescription() for i in range(tokens.Count))
        finally:
            pythoncom.CoUninitialize()
    return list(_voices_cache)


class Speaker:
    def __init__(self, log=None, rate=1, volume=100, voice_factory=None):
        """voice_factory: for tests, makes the voice object (anything with Speak, WaitUntilDone, GetVoices, Voice, Rate, Volume)."""
        self.log, self.rate, self.volume = log, rate, volume
        self._factory = voice_factory
        self._commands = queue.Queue()
        self._thread = None
        self._lock = threading.Lock()
        self.speaking = False
        self.voice_name = None  # the voice the speech engine is using now ("" = the Windows default)
        self._default_voice = None

    def say(self, text, voice="", rate=None, limit=None):
        """voice: part of a voice's name ("" = the Windows default). rate: -10 (slow) to 10 (fast). limit: most characters."""
        text = (text or "").strip()[: (limit or MAX_CHARS)]
        if not text:
            return
        self._ensure_thread()
        self._commands.put(("say", text, voice or "", rate))

    def stop(self):
        if self._thread is not None:
            self._commands.put(("stop", None, "", None))

    def _ensure_thread(self):
        with self._lock:
            if self._thread is None:
                self._thread = threading.Thread(target=self._run, daemon=True, name="speaker")
                self._thread.start()

    def _make_voice(self):
        if self._factory:
            return self._factory()
        import win32com.client
        return win32com.client.Dispatch("SAPI.SpVoice")

    def _choose(self, voice, name):
        """Switch the speech engine to the voice whose name contains `name` (the default voice if there is none)."""
        if name == self.voice_name:
            return
        self.voice_name = name
        try:
            if name:
                tokens = voice.GetVoices()
                for i in range(tokens.Count):
                    token = tokens.Item(i)
                    if name.lower() in token.GetDescription().lower():
                        voice.Voice = token
                        return
            if self._default_voice is not None:  # no name, or no voice with that name: the Windows default
                voice.Voice = self._default_voice
        except Exception as e:
            if self.log:
                self.log.warning("couldn't pick the voice %r: %s", name, e)

    def _run(self):
        coinit = None
        try:
            if not self._factory:
                import pythoncom
                pythoncom.CoInitialize()
                coinit = pythoncom
            voice = self._make_voice()
            voice.Rate, voice.Volume = self.rate, self.volume
            self._default_voice = getattr(voice, "Voice", None)
        except Exception as e:
            if self.log:
                self.log.warning("spoken answers unavailable: %s", e)
            self._thread = None
            return
        try:
            pending = None
            while True:
                kind, text, name, rate = pending or self._commands.get()
                pending = None
                if kind == "stop":
                    voice.Speak("", SVS_ASYNC | SVS_PURGE_BEFORE_SPEAK)
                    self.speaking = False
                    continue
                self._choose(voice, name)
                voice.Rate = self.rate if rate is None else int(rate)
                voice.Speak(text, SVS_ASYNC | SVS_PURGE_BEFORE_SPEAK)  # a new answer replaces whatever is still being read
                self.speaking = True
                while not voice.WaitUntilDone(100):  # in small steps, so stop() and new answers get through
                    try:
                        pending = self._commands.get_nowait()
                    except queue.Empty:
                        continue
                    break
                else:
                    self.speaking = False  # it finished by itself
        except Exception as e:
            if self.log:
                self.log.warning("spoken answers stopped: %s", e)
        finally:
            self.speaking = False
            self._thread = None
            if coinit:
                coinit.CoUninitialize()
