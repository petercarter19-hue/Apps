"""Read it to me: select text anywhere, hold the Command key and say "read this to me". Esc stops it.
Shared by the Mac and the PC; the platform part is a small object with list_voices() and speak(text, voice, rate)
(the Mac uses `say`, the PC Windows' own SAPI voices).

Voices: the computer's own voices (free). A natural Azure OpenAI voice can be used instead when a text-to-speech
model is deployed on Azure and named in config.json "tts_deployment" (off by default; it is never used when
everything is kept on this computer).
"""
import re
import tempfile
from pathlib import Path

RATES = {"slow": 160, "normal": 190, "fast": 230}  # words per minute
MAX_CHARS = 20000  # about 20 minutes of reading


def clean_for_speech(text):
    """Make selected text pleasant to hear: no Markdown symbols, links read as their words, tidy spacing."""
    text = re.sub(r"```.*?```", " (code block skipped) ", str(text), flags=re.S)
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)          # [label](url) -> label
    text = re.sub(r"https?://\S+", "a link", text)
    text = re.sub(r"^[#>\-*•\s]+", "", text, flags=re.M)            # headings, quotes, bullets
    text = re.sub(r"[*_`#]{1,3}", "", text)
    text = re.sub(r"\n{2,}", ". ", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text[:MAX_CHARS]


def natural_voice_available(config):
    return bool(config.get("tts_deployment"))


def azure_speech_file(client, config, text):
    """Natural voice: an mp3 from the Azure OpenAI text-to-speech deployment (only when one is configured)."""
    reply = client.audio.speech.create(model=config["tts_deployment"], voice=config.get("tts_voice", "alloy"),
                                       input=text[:4000])
    path = Path(tempfile.mkstemp(prefix="voicetool-read-", suffix=".mp3")[1])
    path.write_bytes(reply.content)
    return path
