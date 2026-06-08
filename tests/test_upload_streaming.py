import sys
import os

import pytest
from fastapi import HTTPException

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "backend"))

import utils as utils_module
from utils import save_upload_file_chunked


@pytest.fixture
def anyio_backend():
    return "asyncio"


class FakeUpload:
    def __init__(self, chunks):
        self.chunks = list(chunks)
        self.read_sizes = []

    async def read(self, size=-1):
        self.read_sizes.append(size)
        if not self.chunks:
            return b""
        return self.chunks.pop(0)


@pytest.mark.anyio
async def test_save_upload_file_chunked_reads_in_chunks(tmp_path):
    upload = FakeUpload([b"a" * 3, b"b" * 2])
    dest = tmp_path / "upload.bin"

    size = await save_upload_file_chunked(upload, dest)

    assert size == 5
    assert dest.read_bytes() == b"aaabb"
    assert all(read_size == utils_module.UPLOAD_CHUNK_BYTES for read_size in upload.read_sizes)


@pytest.mark.anyio
async def test_save_upload_file_chunked_deletes_partial_file_on_limit(tmp_path, monkeypatch):
    monkeypatch.setattr(utils_module, "MAX_UPLOAD_BYTES", 4)
    upload = FakeUpload([b"a" * 3, b"b" * 2])
    dest = tmp_path / "upload.bin"

    with pytest.raises(HTTPException) as exc_info:
        await save_upload_file_chunked(upload, dest)

    assert exc_info.value.status_code == 413
    assert not dest.exists()
