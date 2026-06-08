import os
import sys
from pathlib import Path

# Must be set before any backend module is imported.
# APP_ENV=test → auth.py allows default dev secret.
# DISABLE_STARTUP_SEED=1 → lifespan skips seeding (prevents hang when
# seeding 1600-row JSON during TestClient startup).
os.environ.setdefault("APP_ENV", "test")
os.environ.setdefault("DISABLE_STARTUP_SEED", "1")

BACKEND_DIR = Path(__file__).resolve().parent / "backend"
if str(BACKEND_DIR) not in sys.path:
    sys.path.insert(0, str(BACKEND_DIR))
