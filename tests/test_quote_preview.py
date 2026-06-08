"""
Focused tests for POST /api/quotes/preview.

Section A — pure unit tests of calc_structured_quote (no DB, no HTTP).
Section B — direct router function tests (no TestClient, no HTTP stack, no hanging).
"""
import pytest
from sqlalchemy.orm import Session

from models import Quote, OrderEvent
from services.pricing_service import calc_structured_quote
from schemas import ProcessItem, QuoteStructuredCreate, QuoteZaporCreate, SaveAsTemplatePayload, ManualQuoteCreate
from tests.helpers import TEST_USERS, make_order, make_test_db


# ─── Helpers ─────────────────────────────────────────────────────────────────

_MOCK_USER = TEST_USERS["technologist"]


def _call_preview(payload: QuoteStructuredCreate, db: Session):
    from routers.quotes import preview_structured_quote
    return preview_structured_quote(payload=payload, db=db, _=_MOCK_USER)


# ─── Section A: calc_structured_quote unit tests ─────────────────────────────

def test_calc_structured_empty_processes():
    result = calc_structured_quote(processes=[], labor_rate=90.0)
    assert result.ops_total == 0.0
    assert result.material_total == 0.0
    assert result.extra_labor == 0.0
    assert result.base == 0.0
    assert result.total_net == 0.0


def test_calc_structured_ops_hours_rate():
    p = ProcessItem(name="Cięcie", hours=2.0, rate_per_hour=90.0)
    result = calc_structured_quote(processes=[p], labor_rate=90.0)
    assert result.ops_total == pytest.approx(180.0)
    assert result.material_total == 0.0


def test_calc_structured_ops_cost_fallback():
    p = ProcessItem(name="Spawanie", cost=250.0)
    result = calc_structured_quote(processes=[p], labor_rate=90.0)
    assert result.ops_total == pytest.approx(250.0)


def test_calc_structured_material_kg_price():
    result = calc_structured_quote(
        processes=[],
        material_weight_kg=50.0,
        material_price_per_kg=4.5,
        labor_rate=90.0,
    )
    assert result.material_total == pytest.approx(225.0)


def test_calc_structured_material_cost_fallback():
    result = calc_structured_quote(
        processes=[],
        material_cost=300.0,
        material_price_per_kg=0.0,
        labor_rate=90.0,
    )
    assert result.material_total == pytest.approx(300.0)


def test_calc_structured_extra_labor():
    result = calc_structured_quote(processes=[], labor_hours=3.0, labor_rate=100.0)
    assert result.extra_labor == pytest.approx(300.0)


def test_calc_structured_transport_added_after_margin():
    result_no_transport = calc_structured_quote(processes=[], labor_rate=90.0)
    result_with_transport = calc_structured_quote(
        processes=[], transport_cost=200.0, labor_rate=90.0
    )
    assert result_with_transport.total_net == pytest.approx(
        result_no_transport.total_net + 200.0, abs=0.01
    )


def test_calc_structured_full_formula():
    p = ProcessItem(name="Frezowanie", hours=4.0, rate_per_hour=85.0)
    result = calc_structured_quote(
        processes             = [p],
        material_weight_kg    = 20.0,
        material_price_per_kg = 5.0,
        labor_hours           = 1.0,
        overhead_pct          = 0.10,
        margin_pct            = 0.20,
        transport_cost        = 150.0,
        labor_rate            = 80.0,
    )
    base         = 4.0*85 + 20.0*5.0 + 1.0*80   # 520
    expected_net = round(base * 1.10 * 1.20 + 150.0, 2)  # 836.4

    assert result.ops_total      == pytest.approx(4.0 * 85.0)
    assert result.material_total == pytest.approx(20.0 * 5.0)
    assert result.extra_labor    == pytest.approx(1.0 * 80.0)
    assert result.base           == pytest.approx(base)
    assert result.subtotal       == pytest.approx(base * 1.10)
    assert result.total_net      == pytest.approx(expected_net)


# ─── Section B: direct router function tests (no TestClient) ─────────────────

def test_preview_router_empty_payload():
    db = make_test_db()
    try:
        result = _call_preview(QuoteStructuredCreate(), db)
        assert hasattr(result, 'total_net')
        assert hasattr(result, 'ops_total')
        assert hasattr(result, 'material_total')
        assert hasattr(result, 'extra_labor')
        assert hasattr(result, 'base')
        assert hasattr(result, 'subtotal')
        assert result.total_net == 0.0
    finally:
        db.close()


def test_preview_router_with_processes():
    db = make_test_db()
    try:
        payload = QuoteStructuredCreate(
            processes=[ProcessItem(name="Toczenie", hours=5.0, rate_per_hour=80.0)],
            material_weight_kg=30.0,
            material_price_per_kg=4.0,
            overhead_pct=0.10,
            margin_pct=0.20,
            transport_cost=0.0,
        )
        result = _call_preview(payload, db)
        # ops=400, mat=120, extra_labor=0, base=520, subtotal=572, total_net=686.4
        assert result.ops_total      == pytest.approx(400.0)
        assert result.material_total == pytest.approx(120.0)
        assert result.base           == pytest.approx(520.0)
        assert result.total_net      == pytest.approx(686.4, abs=0.01)
    finally:
        db.close()


def test_preview_router_uses_db_labor_rate():
    """labor_rate comes from Setting("labor_rate_pln") = 90.0 in the test DB."""
    db = make_test_db()
    try:
        result = _call_preview(QuoteStructuredCreate(labor_hours=2.0), db)
        assert result.extra_labor == pytest.approx(180.0)  # 2.0 × 90.0
    finally:
        db.close()


def test_preview_router_does_not_write_db():
    db = make_test_db()
    try:
        payload = QuoteStructuredCreate(
            processes=[ProcessItem(name="Test", hours=1.0, rate_per_hour=100.0)]
        )
        _call_preview(payload, db)
        assert db.query(Quote).count() == 0
    finally:
        db.close()


# ─── Section C: zapor quote audit attribution ─────────────────────────────────

def test_zapor_quote_logs_user_attribution():
    """create_zapor_quote must record user_role and user_name in OrderEvent."""
    db = make_test_db()
    try:
        order = make_order(db)
        payload = QuoteZaporCreate(
            material_cost=500.0,
            hours_estimate=4.0,
            zapor_multiplier=1.5,
        )
        user = TEST_USERS["technologist"]

        from routers.quotes import create_zapor_quote
        create_zapor_quote(order_id=order.id, payload=payload, db=db, current_user=user)

        event = db.query(OrderEvent).filter(OrderEvent.order_id == order.id).first()
        assert event is not None, "OrderEvent not created"
        assert event.user_role == "technolog"
        assert event.user_name == "Test Technolog"
    finally:
        db.close()


# ─── Section D: save-as-template v3 material weight ──────────────────────────

def test_save_as_template_uses_material_weight_kg():
    """save_order_as_template must read material_weight_kg (v3) not just weight_kg."""
    db = make_test_db()
    try:
        order = make_order(db)
        order.material = "S235"
        db.flush()

        quote = Quote(
            order_id           = order.id,
            material_weight_kg = 42.5,
            total_net          = 1000.0,
            margin_pct         = 0.20,
            labor_hours        = 0,
            material_cost      = 0,
            overhead_pct       = 0,
        )
        db.add(quote)
        db.commit()

        from routers.quotes import save_order_as_template
        tmpl = save_order_as_template(
            order_id=order.id,
            payload=SaveAsTemplatePayload(),
            db=db,
            _=_MOCK_USER,
        )

        assert len(tmpl.materials_json) == 1
        assert tmpl.materials_json[0]["qty_kg"] == pytest.approx(42.5)
        assert tmpl.materials_json[0]["mat"] == "S235"
    finally:
        db.close()


# ─── Section E: structured and manual quote audit attribution ─────────────────

def test_structured_quote_logs_user_attribution():
    db = make_test_db()
    try:
        order = make_order(db)
        user = TEST_USERS["technologist"]
        payload = QuoteStructuredCreate(
            processes=[ProcessItem(name="Toczenie", hours=2.0, rate_per_hour=90.0)],
        )
        from routers.quotes import create_structured_quote
        create_structured_quote(order_id=order.id, payload=payload, db=db, current_user=user)

        event = db.query(OrderEvent).filter(OrderEvent.order_id == order.id).first()
        assert event is not None, "OrderEvent not created"
        assert event.user_role == "technolog"
        assert event.user_name == "Test Technolog"
    finally:
        db.close()


def test_manual_quote_logs_user_attribution():
    db = make_test_db()
    try:
        order = make_order(db)
        user = TEST_USERS["director"]
        payload = ManualQuoteCreate(total_net=2500.0)
        from routers.quotes import create_manual_quote
        create_manual_quote(order_id=order.id, payload=payload, db=db, current_user=user)

        event = db.query(OrderEvent).filter(OrderEvent.order_id == order.id).first()
        assert event is not None, "OrderEvent not created"
        assert event.user_role == "dyrektor_produkcji"
        assert event.user_name == "Test Director"
    finally:
        db.close()
