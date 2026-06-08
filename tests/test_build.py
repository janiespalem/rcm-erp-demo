"""Always-on checks that run in every environment — no Playwright required."""
import pathlib

VITE_DIST  = pathlib.Path(__file__).resolve().parents[1] / "frontend-vite" / "dist"
VITE_INDEX = VITE_DIST / "index.html"


def test_vite_build_artifact_exists():
    """frontend-vite/dist/index.html must exist before deploy/serve."""
    assert VITE_INDEX.exists(), (
        "frontend-vite/dist/index.html not found. "
        "Run: cd frontend-vite && npm run build"
    )
    content = VITE_INDEX.read_text(encoding="utf-8")
    assert "<div id=" in content or "<script" in content, (
        "index.html looks empty or truncated"
    )
