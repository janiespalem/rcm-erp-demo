import pytest
from fastapi import HTTPException

from models import OrderAttachment, OrderEvent, OrderStatus
from routers.orders import archive_order, list_orders, restore_order
from services import order_service
from schemas import OrderUpdate
from tests.helpers import TEST_USERS, make_order, make_test_db


def test_archive_is_separate_reversible_and_keeps_status():
    db = make_test_db()
    try:
        order = make_order(db, status=OrderStatus.in_production)
        with pytest.raises(HTTPException, match="Archiwizować można tylko"):
            archive_order(order.id, db=db, current_user=TEST_USERS["technologist"])

        order.status = OrderStatus.wydane
        db.commit()
        archived = archive_order(order.id, db=db, current_user=TEST_USERS["technologist"])

        assert archived.status == OrderStatus.wydane
        assert archived.archived_at is not None
        assert list_orders(db=db, archived=False, _=TEST_USERS["technologist"]) == []
        assert [o.id for o in list_orders(db=db, archived=True, _=TEST_USERS["technologist"])] == [order.id]
        assert db.query(OrderEvent).filter_by(order_id=order.id, event_type="archived").count() == 1

        restored = restore_order(order.id, db=db, current_user=TEST_USERS["technologist"])
        assert restored.archived_at is None
        assert [o.id for o in list_orders(db=db, archived=False, _=TEST_USERS["technologist"])] == [order.id]
    finally:
        db.close()


def test_archived_order_is_read_only():
    db = make_test_db()
    try:
        order = make_order(db, status=OrderStatus.wydane)
        archive_order(order.id, db=db, current_user=TEST_USERS["technologist"])

        with pytest.raises(HTTPException, match="tylko do odczytu"):
            order_service.update_order(
                db,
                order.id,
                OrderUpdate(client="Changed", version_id=order.version_id),
                user=TEST_USERS["technologist"],
            )
    finally:
        db.close()


def test_hard_delete_only_accepts_empty_draft():
    db = make_test_db()
    try:
        active = make_order(db, order_number="ACTIVE", status=OrderStatus.in_production)
        with pytest.raises(HTTPException, match="pusty szkic"):
            order_service.delete_order(db, active.id)

        draft = make_order(db, order_number="DRAFT", status=OrderStatus.draft)
        db.add(OrderAttachment(
            order_id=draft.id,
            filename="drawing.pdf",
            stored_path=f"uploads/{draft.id}/drawing.pdf",
        ))
        db.commit()
        with pytest.raises(HTTPException, match="powiązane dane"):
            order_service.delete_order(db, draft.id)
    finally:
        db.close()


def test_only_technologist_can_deliver_directly_from_production():
    db = make_test_db()
    try:
        order = make_order(db, status=OrderStatus.in_production)
        with pytest.raises(HTTPException) as exc:
            order_service.deliver_order(db, order.id, user=TEST_USERS["office"])
        assert exc.value.status_code == 403

        delivered = order_service.deliver_order(
            db,
            order.id,
            user=TEST_USERS["technologist"],
        )
        assert delivered.status == OrderStatus.wydane
    finally:
        db.close()
