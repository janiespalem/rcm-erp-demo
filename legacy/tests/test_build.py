"""Always-on checks that run in every environment — no Playwright required."""
import pathlib

VITE_DIST  = pathlib.Path(__file__).resolve().parents[1] / "frontend-vite" / "dist"
VITE_INDEX = VITE_DIST / "index.html"
VITE_SRC   = VITE_DIST.parent / "src"


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


def test_frontend_does_not_persist_bearer_credentials():
    source = "\n".join(
        path.read_text(encoding="utf-8")
        for pattern in ("*.js", "*.vue")
        for path in VITE_SRC.rglob(pattern)
    )
    bundle = "\n".join(
        path.read_text(encoding="utf-8")
        for path in (VITE_DIST / "assets").glob("*.js")
    )

    assert "rcm_user" not in source
    assert "rcm_user" not in bundle
