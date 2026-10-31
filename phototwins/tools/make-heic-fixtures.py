"""Makes the two HEIC test photos once. The free image reader inside PhotoTwins can read HEIC but not write it,
so these are made here from the made-up scene the tests export (never from a real photo), then kept in tests\\fixtures.

Needs the free packages pillow and pillow-heif (pip install pillow pillow-heif). Not part of the app.

  1. PHOTOTWINS_FIXTURE_SOURCES=<folder under test-work> dotnet test tests\\PhotoTwins.Tests --filter Heic_fixture_sources
  2. python tools\\make-heic-fixtures.py <that folder> tests\\PhotoTwins.Tests\\fixtures
"""
import os
import sys

import pillow_heif
from PIL import Image

pillow_heif.register_heif_opener()
src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
im = Image.open(os.path.join(src, "scene1.png")).convert("RGB")

exif = Image.Exif()
exif[0x010F] = "Apple"
exif[0x0110] = "iPhone 15 Pro"
sub = exif.get_ifd(0x8769)
sub[0x9003] = "2026:10:12 14:03:09"
sub[0x9291] = "07"
gps = exif.get_ifd(0x8825)
gps[1] = "N"
gps[2] = (34.0, 44.0, 10.0)
gps[3] = "W"
gps[4] = (86.0, 35.0, 5.0)

im.save(os.path.join(out, "scene1-with-info.heic"), format="HEIF", quality=80, exif=exif.tobytes())
im.save(os.path.join(out, "scene1-no-info.heic"), format="HEIF", quality=80)
print("made", os.listdir(out))
