"""Makes the HEIC photos for the picture-speed test folder (test photos only, made up by the tests).
Each made-up scene becomes two identical HEIC files, like a phone photo that was copied.

Needs the free packages pillow and pillow-heif (pip install pillow pillow-heif). Not part of the app.
  python tools\\make-bench-heic.py <folder of png scenes> <output folder>
"""
import concurrent.futures
import os
import shutil
import sys

import pillow_heif
from PIL import Image

pillow_heif.register_heif_opener()
src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)


def make(name):
    base = os.path.splitext(name)[0]
    dest = os.path.join(out, f"IMG_{base}.heic")
    Image.open(os.path.join(src, name)).convert("RGB").save(dest, format="HEIF", quality=75)
    shutil.copyfile(dest, os.path.join(out, f"IMG_{base} (1).heic"))


names = sorted(n for n in os.listdir(src) if n.endswith(".png"))
with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
    list(pool.map(make, names))
print("made", len(names) * 2, "heic files")
