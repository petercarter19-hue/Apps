"""Runs Chirpy's own tests: copies app/ and tests/test_app.py into a fresh temporary folder and runs pytest there.

    python tools/run_tests.py            (use the runtime's python.exe, with pytest installed in it)

The tests use fakes: no microphone, no speech model, no keys pressed, no paste, no registry, no window on screen. Every file the
app writes during them stays in the temporary folder, which is removed at the end.
"""
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent.parent  # the chirpy folder


def main():
    with tempfile.TemporaryDirectory(prefix="chirpy-tests-") as tmp:
        inside = Path(tmp) / "app"
        shutil.copytree(HERE / "app", inside, ignore=shutil.ignore_patterns("__pycache__"))
        shutil.copy(HERE / "tests" / "test_app.py", inside / "test_light_inside.py")
        run = subprocess.run([sys.executable, "-s", "-m", "pytest", "-q", "-p", "no:cacheprovider", "test_light_inside.py",
                              "--basetemp", str(Path(tmp) / "t")], cwd=inside)
        return run.returncode


if __name__ == "__main__":
    sys.exit(main())
