"""Print every word said in an opener voice-over recording, with the second it starts.

    python transcribe.py <recording>

Runs locally and free with faster-whisper (small.en; pip install faster-whisper numpy). The audio is decoded with
ffmpeg first, because some faster-whisper installs fail to decode .m4a files on their own.
"""
import subprocess
import sys

import numpy as np
from faster_whisper import WhisperModel

sys.stdout.reconfigure(encoding="utf-8")
if len(sys.argv) != 2:
    sys.exit(__doc__)
pcm = subprocess.run(["ffmpeg", "-v", "error", "-i", sys.argv[1], "-ac", "1", "-ar", "16000", "-f", "f32le", "-"],
                     capture_output=True, check=True).stdout
model = WhisperModel("small.en", device="cpu", compute_type="int8")
segments, _ = model.transcribe(np.frombuffer(pcm, dtype=np.float32), word_timestamps=True)
for s in segments:
    print(f"[{s.start:6.2f}-{s.end:6.2f}] {s.text.strip()}")
    print("   " + " ".join(f"{w.word.strip()}@{w.start:.2f}" for w in s.words))
