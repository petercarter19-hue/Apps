# VoiceTool Light runs only on the packages it ships: the user's own Python packages are left out.
import site, sys
_user = site.getusersitepackages()
sys.path[:] = [p for p in sys.path if not p.startswith(_user)]
site.ENABLE_USER_SITE = False
