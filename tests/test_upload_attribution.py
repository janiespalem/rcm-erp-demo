"""
Focused tests for upload_attachment — verifies that uploaded_by comes from
current_user["role"], not from a spoofable Form field.
No TestClient; calls the router function directly as async.
"""
import pytest

from models import OrderAttachment
from tests.helpers import FakeUpload, TEST_USERS, make_order, make_test_db


@pytest.fixture
def anyio_backend():
    return "asyncio"


@pytest.mark.anyio
async def test_upload_attribution_uses_jwt_role(tmp_path, monkeypatch):
    import routers.order_resources as mod
    monkeypatch.setattr(mod, "UPLOAD_ROOT", tmp_path)

    db = make_test_db()
    try:
        order = make_order(db, order_number="ATT-001")
        fake_file = FakeUpload()
        user = TEST_USERS["office"]

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

    db = make_test_db()
    try:
        order = make_order(db, order_number="ATT-001")
        fake_file = FakeUpload(name=blocked)
        user = TEST_USERS["office"]

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

    db = make_test_db()
    try:
        order = make_order(db, order_number="ATT-001")
        fake_file = FakeUpload(name="drawing.pdf")
        user = TEST_USERS["technologist"]

        att = await mod.upload_attachment(
            order_id=order.id,
            file=fake_file,
            db=db,
            current_user=user,
        )
        assert att.id is not None
    finally:
        db.close()
