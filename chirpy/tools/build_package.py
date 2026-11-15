"""Builds the Chirpy package: a folder with Setup.cmd that installs without internet or administrator rights.

    python tools/build_package.py --runtime <runtime folder> --model <model folder> --out <output folder>
                                  [--build 16d40c5d] [--version 2026.11.11]

<runtime folder>  a Python 3.13 for Windows (python.exe, pythonw.exe, Lib, tcl) with the pinned packages in
                  installer/requirements-friend.txt already installed. tools/make_runtime.ps1 makes one.
<model folder>    parakeet-tdt-0.6b-v2 (config.json, vocab.txt, encoder-model.int8.onnx, decoder_joint-model.int8.onnx).
                  See THIRD-PARTY-NOTICES.md for where the model comes from.

The package is written to <output folder>/Chirpy. It holds the app (app/), the setup scripts, the notices and license, the
runtime (python/) and the model (models/). The runtime and the model are hard-linked when they are on the same drive.
--build and --version are only the label written to VERSION.txt (shown in the installer and in Chirpy's feedback link);
the defaults are the label of the released Chirpy-Setup.exe.
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent.parent  # the chirpy folder
MODEL_FILES = ["config.json", "vocab.txt", "encoder-model.int8.onnx", "decoder_joint-model.int8.onnx"]
SETUP_FILES = ["Setup.cmd", "setup.ps1", "uninstall.ps1", "README.txt", "requirements-friend.txt"]
VC_DLLS = ("msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "concrt140.dll", "vcruntime140.dll", "vcruntime140_1.dll")


def link_or_copy(src, dest):
    dest.parent.mkdir(parents=True, exist_ok=True)
    try:
        os.link(src, dest)
    except OSError:
        shutil.copy2(src, dest)


def check_runtime(runtime):
    for need in ("python.exe", "pythonw.exe", "Lib/site-packages/webview", "Lib/site-packages/onnx_asr", "tcl"):
        if not (runtime / need).exists():
            raise FileNotFoundError(f"the runtime has no {need}")
    # Every package must come from the runtime itself, never from the builder's own user site-packages.
    check = subprocess.run([str(runtime / "python.exe"), "-s", "-c",
                            "import sys, PIL, numpy, webview, onnx_asr, pystray, pynput, sounddevice, win32api, clr_loader;"
                            "print('\\n'.join(m.__file__ for m in (PIL, numpy, webview, onnx_asr, pystray, pynput, sounddevice, clr_loader)))"],
                           capture_output=True, text=True)
    if check.returncode or any(not Path(f).resolve().is_relative_to(runtime.resolve()) for f in check.stdout.split()):
        raise RuntimeError(f"the runtime is missing packages of its own: {check.stdout} {check.stderr[-500:]}")
    # onnxruntime needs the Visual C++ runtime, which a clean Windows doesn't have: it is shipped next to python.exe.
    for dll in VC_DLLS:
        if not (runtime / dll).exists():
            raise FileNotFoundError(f"the runtime has no {dll} next to python.exe")
    if not (runtime / "Lib" / "site-packages" / "sitecustomize.py").exists():
        raise FileNotFoundError("the runtime has no sitecustomize.py that leaves the user's own packages out")


def build(runtime, model, out, build_id, version):
    runtime, model, out = Path(runtime), Path(model), Path(out)
    check_runtime(runtime)
    for name in MODEL_FILES:
        if not (model / name).is_file():
            raise FileNotFoundError(f"the model has no {name}")
    pkg = out / "Chirpy"
    if pkg.exists():
        raise FileExistsError(f"{pkg} already exists; choose an empty output folder")
    shutil.copytree(HERE / "app", pkg / "app", ignore=shutil.ignore_patterns("__pycache__"))
    for name in SETUP_FILES:
        shutil.copy2(HERE / "installer" / name, pkg / name)
    shutil.copy2(HERE / "LICENSE", pkg / "LICENSE.txt")
    shutil.copy2(HERE / "THIRD-PARTY-NOTICES.md", pkg / "THIRD-PARTY-NOTICES.md")
    (pkg / "VERSION.txt").write_text(f"Chirpy (from daily) {build_id}\nVersion: {version}\n", encoding="utf-8")
    for src in runtime.rglob("*"):
        rel = src.relative_to(runtime)
        pip = len(rel.parts) > 2 and rel.parts[:2] == ("Lib", "site-packages") and rel.parts[2].lower().startswith("pip")
        if not src.is_file() or "__pycache__" in rel.parts or rel.parts[0] == "Scripts" or pip:
            continue  # (no pip and no command-line launchers: they aren't needed, and launchers name the build folder)
        link_or_copy(src, pkg / "python" / rel)
    for name in MODEL_FILES:
        link_or_copy(model / name, pkg / "models" / "parakeet-tdt-0.6b-v2" / name)
    return pkg


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--runtime", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--build", default="16d40c5d")
    ap.add_argument("--version", default="2026.11.11")
    a = ap.parse_args()
    if not re.fullmatch(r"[0-9a-f]{8}", a.build) or not re.fullmatch(r"\d{4}\.\d{1,2}\.\d{1,2}", a.version):
        sys.exit("--build must be 8 hex digits and --version like 2026.11.11")
    print(build(a.runtime, a.model, a.out, a.build, a.version))
