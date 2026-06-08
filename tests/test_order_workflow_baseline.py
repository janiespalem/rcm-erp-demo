"""
Workflow-level backend tests without TestClient.

These cover cross-router/service contracts that are easy to break with small
backend changes: order lifecycle, quote persistence, audit events, and price
history creation.
"""
from datetime import date, timedelta

import pytest
from fastapi import HTTPException

from models import OrderEvent, OrderStatus, PriceHistory
from schemas import OrderCreate, ProcessItem, QuoteStructuredCreate
from services import order_service
from tests.helpers import TEST_USERS, make_test_db


def _event_types(db, order_id: int) -> list[str]:
    return [
        event.event_type
        for event in (
            db.query(OrderEvent)
            .filter(OrderEvent.order_id == order_id)
            .order_by(OrderEvent.id.asc())
            .all()
        )
    ]


def test_full_order_lifecycle_records_audit_and_price_history():
    db = make_test_db({"labor_rate_pln": "100.0"})
    try:
        order = order_service.create_order(
            db,
            OrderCreate(
                client="Test Client",
                deadline=date.today() + timedelta(days=14),
                material="S235",
                has_drawing=True,
                order_type="remont",
                sop_name="Test service",
            ),
            user=TEST_USERS["office"],
        )
        order.status = OrderStatus.niestandard
        db.commit()

        from routers.quotes import create_structured_quote

        quote = create_structured_quote(
            order_id=order.id,
            payload=QuoteStructuredCreate(
                processes=[
                    ProcessItem(name="Cutting", hours=2.0, rate_per_hour=100.0),
                    ProcessItem(name="Welding", cost=150.0),
                ],
                material_weight_kg=25.0,
                material_price_per_kg=4.0,
                overhead_pct=0.10,
                margin_pct=0.20,
            ),
            db=db,
            current_user=TEST_USERS["technologist"],
        )

        assert quote.total_net == pytest.approx(594.0)
        assert order.status == OrderStatus.quoted

        order_service.confirm_order(db, order.id, user=TEST_USERS["office"])
        order_service.start_order(db, order.id, user=TEST_USERS["technologist"])
        order_service.complete_order(db, order.id, user=TEST_USERS["technologist"])
        order_service.deliver_order(db, order.id, user=TEST_USERS["office"])

        db.refresh(order)
        assert order.status == OrderStatus.wydane
        assert order.quoted_at is not None
        assert order.started_at is not None
        assert order.completed_at is not None
        assert order.delivered_at is not None

        assert _event_types(db, order.id) == [
            "created",
            "quoted",
            "confirmed",
            "started",
            "completed",
            "delivered",
        ]

        quoted_event = (
            db.query(OrderEvent)
            .filter(OrderEvent.order_id == order.id, OrderEvent.event_type == "quoted")
            .one()
        )
        assert quoted_event.user_role == "technolog"
        assert quoted_event.user_name == "Test Technolog"

        price_history = db.query(PriceHistory).one()
        assert float(price_history.parameters_json["weight_kg"]) == pytest.approx(25.0)
        assert float(price_history.parameters_json["pln_kg"]) == pytest.approx(594.0 / 25.0)
    finally:
        db.close()


def test_lifecycle_rejects_out_of_order_transition():
    db = make_test_db()
    try:
        order = order_service.create_order(
            db,
            OrderCreate(
                client="Test Client",
                deadline=date.today() + timedelta(days=7),
                material="S235",
            ),
            user=TEST_USERS["office"],
        )

        with pytest.raises(HTTPException) as exc:
            order_service.start_order(db, order.id, user=TEST_USERS["technologist"])

        assert exc.value.status_code == 409
        db.refresh(order)
        assert order.status == OrderStatus.draft
        assert _event_types(db, order.id) == ["created"]
    finally:
        db.close()
