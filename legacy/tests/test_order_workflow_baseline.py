"""
Workflow-level backend tests without TestClient.

These cover cross-router/service contracts that are easy to break with small
backend changes: order lifecycle, quote persistence, audit events, and price
history creation.
"""
from datetime import date, timedelta

import pytest
from fastapi import HTTPException

from models import OrderEvent, OrderStatus, PriceHistory, Quote, StockMovement
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

        # confirm now sends the order straight into production (the separate
        # "start production" step was merged into confirm), so complete follows
        # directly after confirm.
        order_service.confirm_order(db, order.id, user=TEST_USERS["office"])
        db.refresh(order)
        assert order.status == OrderStatus.in_production
        assert order.started_at is not None

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
            "quote_saved",
            "confirmed",
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


def test_material_issue_uses_quote_weight_once_and_never_invents_one_kg():
    db = make_test_db()
    try:
        order = order_service.create_order(
            db,
            OrderCreate(
                client="Test Client",
                deadline=date.today() + timedelta(days=14),
                approved_material_id=7,
            ),
            user=TEST_USERS["office"],
        )
        order.status = OrderStatus.in_production
        quote = Quote(order_id=order.id, material_weight_kg=12.5)
        db.add(quote)
        db.commit()

        order_service._emit_material_rozchod(db, order)
        db.flush()
        order_service._emit_material_rozchod(db, order)
        db.commit()

        movements = db.query(StockMovement).filter_by(order_id=order.id).all()
        assert len(movements) == 1
        assert float(movements[0].qty) == pytest.approx(12.5)

        movements[0].qty = 0
        quote.material_weight_kg = 0
        db.commit()
        db.delete(movements[0])
        db.commit()
        order_service._emit_material_rozchod(db, order)
        db.commit()
        assert db.query(StockMovement).filter_by(order_id=order.id).count() == 0
    finally:
        db.close()


def test_quote_unblocks_standard_order():
    """A 'standard' order must leave its status when the technolog saves a quote —
    before this transition existed, standard orders were stuck forever."""
    db = make_test_db({"labor_rate_pln": "100.0"})
    try:
        order = order_service.create_order(
            db,
            OrderCreate(
                client="Test Client",
                deadline=date.today() + timedelta(days=14),
                material="S235",
                has_drawing=True,
                order_type="catalog",
            ),
            user=TEST_USERS["office"],
        )
        order.status = OrderStatus.standard
        db.commit()

        from routers.quotes import create_structured_quote

        create_structured_quote(
            order_id=order.id,
            payload=QuoteStructuredCreate(
                processes=[ProcessItem(name="Cutting", hours=1.0, rate_per_hour=100.0)],
                material_weight_kg=10.0,
                material_price_per_kg=4.0,
                overhead_pct=0.10,
                margin_pct=0.20,
            ),
            db=db,
            current_user=TEST_USERS["technologist"],
        )

        db.refresh(order)
        assert order.status == OrderStatus.quoted
        assert order.quoted_at is not None

        order_service.confirm_order(db, order.id, user=TEST_USERS["office"])
        db.refresh(order)
        assert order.status == OrderStatus.in_production
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

        # A draft order cannot jump straight to completion — production is only
        # reachable via confirm (which requires a quote first).
        with pytest.raises(HTTPException) as exc:
            order_service.complete_order(db, order.id, user=TEST_USERS["technologist"])

        assert exc.value.status_code == 409
        db.refresh(order)
        assert order.status == OrderStatus.draft
        assert _event_types(db, order.id) == ["created"]
    finally:
        db.close()


def test_create_order_persists_intake_materials_json():
    db = make_test_db()
    try:
        order = order_service.create_order(
            db,
            OrderCreate(
                client="Test Client",
                deadline=date.today() + timedelta(days=7),
                material="S235",
                materials_json=[
                    {"name": "S235", "qty_kg": 12.5},
                    {"name": "Hardox", "qty_kg": 3.0},
                ],
            ),
            user=TEST_USERS["office"],
        )

        assert order.material == "S235"
        assert order.materials_json == [
            {"name": "S235", "qty_kg": 12.5},
            {"name": "Hardox", "qty_kg": 3.0},
        ]
    finally:
        db.close()
