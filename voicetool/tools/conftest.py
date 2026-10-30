"""Lets the tests in this folder import the app, which lives in the src folder next to this one."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))
