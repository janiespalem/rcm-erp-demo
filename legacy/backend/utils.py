"""Shared constants and helper functions used across routers."""
import os
import pathlib
import tempfile
from datetime import datetime, timezone
from typing import Optional

from fastapi import HTTPException, UploadFile

BACKEND_DIR = pathlib.Path(__file__).parent

UPLOAD_ROOT           = BACKEND_DIR / "uploads"
UPLOAD_ROOT.mkdir(exist_ok=True)
MAX_UPLOAD_BYTES      = 100 * 1024 * 1024   # 100 MB
UPLOAD_CHUNK_BYTES    = 1024 * 1024

DEFAULT_LABOR_RATE_PLN = 90.0
DEFAULT_OVERHEAD_PCT   = 0.10
DEFAULT_MARGIN_PCT     = 0.25


def _now() -> datetime:
    return datetime.now(timezone.utc).replace(tzinfo=None)


def safe_upload_filename(filename: Optional[str]) -> str:
    raw_name = pathlib.Path(filename or "plik").name
    safe_name = "".join(ch if ch.isalnum() or ch in "._- " else "_" for ch in raw_name).strip()
    safe_name = safe_name or "plik"
    while len(safe_name.encode("utf-8")) > 240:
        safe_name = safe_name[:-1]
    return safe_name or "plik"


async def save_upload_file_chunked(file: UploadFile, dest: pathlib.Path) -> int:
    total_bytes = 0
    temp_path: pathlib.Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb",
            dir=dest.parent,
            prefix=f".{dest.name}.",
            delete=False,
        ) as out:
            temp_path = pathlib.Path(out.name)
            while True:
                chunk = await file.read(UPLOAD_CHUNK_BYTES)
                if not chunk:
                    break
                total_bytes += len(chunk)
                if total_bytes > MAX_UPLOAD_BYTES:
                    raise HTTPException(status_code=413, detail="Plik za duży (max 100 MB)")
                out.write(chunk)
        os.replace(temp_path, dest)
    except Exception:
        if temp_path:
            temp_path.unlink(missing_ok=True)
        raise
    return total_bytes
