"""
Focused tests for upload_attachment — verifies that uploaded_by comes from
current_user["role"], not from a spoofable Form field.
No TestClient; calls the router function directly as async.
"""
import os
import sys
import pathlib
import zipfile
from unittest.mock import Mock

import pytest

os.environ.setdefault("APP_ENV", "test")
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "backend"))

from sqlalchemy import create_engine
from sqlalchemy.orm import Session
from sqlalchemy.pool import StaticPool

from models import Base, Order, OrderAttachment, OrderStatus


@pytest.fixture
def anyio_backend():
    return "asyncio"


def _make_db() -> Session:
    engine = create_engine(
        "sqlite:///:memory:",
        connect_args={"check_same_thread": False},
        poolclass=StaticPool,
    )
    Base.metadata.create_all(engine)
    return Session(engine)


def _make_order(db: Session) -> Order:
    order = Order(order_number="ATT-001", client="Klient", status=OrderStatus.niestandard)
    db.add(order)
    db.commit()
    db.refresh(order)
    return order


class _FakeUpload:
    def __init__(
        self,
        name: str = "test.pdf",
        content: bytes = b"%PDF-1.4\n%%EOF",
        content_type: str = "application/pdf",
    ):
        self.filename    = name
        self.content_type = content_type
        self._data       = content

    async def read(self, size=-1):
        data, self._data = self._data, b""
        return data


@pytest.mark.anyio
async def test_upload_attribution_uses_jwt_role(tmp_path, monkeypatch):
    import routers.order_resources as mod
    monkeypatch.setattr(mod, "UPLOAD_ROOT", tmp_path)

    db = _make_db()
    try:
        order = _make_order(db)
        fake_file = _FakeUpload()
        user = {"id": "5", "role": "biuro", "name": "Test Office"}

        att = await mod.upload_attachment(
            order_id=order.id,
            file=fake_file,
            db=db,
            current_user=user,
        )

        assert att.uploaded_by == "biuro"
        # Verify it's persisted
        stored = db.get(OrderAttachment, att.id)
        assert stored.uploaded_by == "biuro"
    finally:
        db.close()


# ─── Extension blocking ───────────────────────────────────────────────────────

@pytest.mark.parametrize("blocked", ["evil.html", "script.js", "image.svg", "page.htm"])
@pytest.mark.anyio
async def test_blocked_extension_raises_415(tmp_path, monkeypatch, blocked):
    import routers.order_resources as mod
    from fastapi import HTTPException
    monkeypatch.setattr(mod, "UPLOAD_ROOT", tmp_path)

    db = _make_db()
    try:
        order = _make_order(db)
        fake_file = _FakeUpload(name=blocked)
        user = {"id": "5", "role": "biuro", "name": "Test Office"}

        with pytest.raises(HTTPException) as exc:
            await mod.upload_attachment(
                order_id=order.id,
                file=fake_file,
                db=db,
                current_user=user,
            )
        assert exc.value.status_code == 415
    finally:
        db.close()


@pytest.mark.anyio
async def test_allowed_extension_passes(tmp_path, monkeypatch):
    import routers.order_resources as mod
    monkeypatch.setattr(mod, "UPLOAD_ROOT", tmp_path)

    db = _make_db()
    try:
        order = _make_order(db)
        fake_file = _FakeUpload(name="drawing.pdf")
        user = {"id": "5", "role": "technolog", "name": "T"}

        att = await mod.upload_attachment(
            order_id=order.id,
            file=fake_file,
            db=db,
            current_user=user,
        )
        assert att.id is not None
    finally:
        db.close()


@pytest.mark.anyio
async def test_upload_rejects_html_disguised_as_pdf(tmp_path, monkeypatch):
    import routers.order_resources as mod
    from fastapi import HTTPException
    monkeypatch.setattr(mod, "UPLOAD_ROOT", tmp_path)

    db = _make_db()
    try:
        order = _make_order(db)
        fake_file = _FakeUpload(name="invoice.pdf", content=b"<html><script>bad()</script></html>")

        with pytest.raises(HTTPException) as exc:
            await mod.upload_attachment(
                order_id=order.id,
                file=fake_file,
                db=db,
                current_user={"id": "5", "role": "biuro", "name": "Test Office"},
            )

        assert exc.value.status_code == 415
        assert not any(path.is_file() for path in tmp_path.rglob("*"))
    finally:
        db.close()


@pytest.mark.anyio
async def test_upload_derives_mime_from_verified_content(tmp_path, monkeypatch):
    import routers.order_resources as mod
    monkeypatch.setattr(mod, "UPLOAD_ROOT", tmp_path)

    db = _make_db()
    try:
        order = _make_order(db)
        fake_file = _FakeUpload(content_type="text/html")

        att = await mod.upload_attachment(
            order_id=order.id,
            file=fake_file,
            db=db,
            current_user={"id": "5", "role": "biuro", "name": "Test Office"},
        )

        assert att.mime_type == "application/pdf"
    finally:
        db.close()


@pytest.mark.anyio
async def test_upload_removes_file_when_database_commit_fails(tmp_path, monkeypatch):
    import routers.order_resources as mod
    monkeypatch.setattr(mod, "UPLOAD_ROOT", tmp_path)

    db = _make_db()
    try:
        order = _make_order(db)
        db.commit = Mock(side_effect=RuntimeError("database unavailable"))
        db.rollback = Mock(wraps=db.rollback)

        with pytest.raises(RuntimeError, match="database unavailable"):
            await mod.upload_attachment(
                order_id=order.id,
                file=_FakeUpload(),
                db=db,
                current_user={"id": "5", "role": "biuro", "name": "Test Office"},
            )

        assert not any(path.is_file() for path in tmp_path.rglob("*"))
        db.rollback.assert_called_once()
    finally:
        db.close()


@pytest.mark.anyio
async def test_download_rechecks_content_and_sets_safe_headers(tmp_path, monkeypatch):
    import routers.order_resources as mod
    from fastapi import HTTPException
    monkeypatch.setattr(mod, "UPLOAD_ROOT", tmp_path)

    db = _make_db()
    try:
        order = _make_order(db)
        att = await mod.upload_attachment(
            order_id=order.id,
            file=_FakeUpload(),
            db=db,
            current_user={"id": "5", "role": "biuro", "name": "Test Office"},
        )

        response = mod.download_attachment(att.id, db=db, _={})
        assert response.media_type == "application/pdf"
        assert response.headers["x-content-type-options"] == "nosniff"
        assert response.headers["content-disposition"].startswith("attachment;")

        mod._attachment_path(att.stored_path).write_bytes(b"<html>bad</html>")
        with pytest.raises(HTTPException) as exc:
            mod.download_attachment(att.id, db=db, _={})
        assert exc.value.status_code == 415
    finally:
        db.close()


@pytest.mark.parametrize(
    "filename,content,mime_type",
    [
        ("file.pdf", b"%PDF-1.4\n", "application/pdf"),
        ("file.png", b"\x89PNG\r\n\x1a\n", "image/png"),
        ("file.jpg", b"\xff\xd8\xff\xe0", "image/jpeg"),
        ("file.dwg", b"AC1027", "image/vnd.dwg"),
        ("file.dxf", b"0\r\nSECTION\r\n", "image/vnd.dxf"),
    ],
)
def test_verified_attachment_signatures(tmp_path, filename, content, mime_type):
    import routers.order_resources as mod

    path = tmp_path / filename
    path.write_bytes(content)

    assert mod._verified_attachment_mime(path, filename) == mime_type


@pytest.mark.parametrize(
    "filename,required_member,mime_type",
    [
        (
            "file.docx",
            "word/document.xml",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ),
        (
            "file.xlsx",
            "xl/workbook.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ),
    ],
)
def test_verified_ooxml_containers(tmp_path, filename, required_member, mime_type):
    import routers.order_resources as mod

    path = tmp_path / filename
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("[Content_Types].xml", "<Types/>")
        archive.writestr(required_member, "<document/>")

    assert mod._verified_attachment_mime(path, filename) == mime_type
