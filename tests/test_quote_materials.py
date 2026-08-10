"""
Focused tests for multi-material quote lines (v3).

Section A — pure unit tests of calc_structured_quote material handling.
Section B — structured router persists/returns materials_json; old quotes still load.
"""
import pytest
from datetime import date
from sqlalchemy import create_engine
from sqlalchemy.orm import Session

from models import Base, Order, Quote, OrderStatus
from services.pricing_service import calc_structured_quote
from schemas import MaterialLine, ProcessItem, QuoteStructuredCreate

_MOCK_USER = {"id": "2", "role": "technolog", "name": "Technolog Test"}


def make_test_db() -> Session:
    engine = create_engine("sqlite:///:memory:", connect_args={"check_same_thread": False})
    Base.metadata.create_all(engine)
    return Session(engine)


def make_order(db: Session) -> Order:
    order = Order(
        client="Demo Client",
        deadline=date(2026, 6, 30),
        material="S235",
        order_type="remont",
        quantity=1,
        status=OrderStatus.niestandard,
    )
    db.add(order)
    db.commit()
    db.refresh(order)
    return order


# ─── Section A: calc_structured_quote multi-material ─────────────────────────

def test_materials_multiple_lines_sum_qty_price():
    """Two lines with kg × PLN/kg sum into material_total."""
    mats = [
        MaterialLine(name="S235", qty_kg=50.0, price_per_kg=4.0),   # 200
        MaterialLine(name="Hardox", qty_kg=10.0, price_per_kg=12.0),  # 120
    ]
    result = calc_structured_quote(processes=[], materials=mats, labor_rate=90.0)
    assert result.material_total == pytest.approx(320.0)


def test_materials_explicit_cost_fallback_per_line():
    """A line with cost > 0 uses cost; another uses qty × price."""
    mats = [
        MaterialLine(name="Odlew", cost=500.0),                      # 500 (explicit)
        MaterialLine(name="S355", qty_kg=20.0, price_per_kg=5.0),    # 100
    ]
    result = calc_structured_quote(processes=[], materials=mats, labor_rate=90.0)
    assert result.material_total == pytest.approx(600.0)


def test_materials_cost_takes_precedence_over_kg_price():
    """When both cost and kg/price are present on a line, cost wins."""
    mats = [MaterialLine(name="X", qty_kg=10.0, price_per_kg=9.0, cost=250.0)]
    result = calc_structured_quote(processes=[], materials=mats, labor_rate=90.0)
    assert result.material_total == pytest.approx(250.0)


def test_empty_materials_preserves_legacy_single_material():
    """No materials list → legacy single-material behavior is unchanged."""
    kg_price = calc_structured_quote(
        processes=[], material_weight_kg=50.0, material_price_per_kg=4.5, labor_rate=90.0
    )
    assert kg_price.material_total == pytest.approx(225.0)

    cost_fallback = calc_structured_quote(
        processes=[], material_cost=300.0, material_price_per_kg=0.0, labor_rate=90.0
    )
    assert cost_fallback.material_total == pytest.approx(300.0)


def test_materials_override_legacy_single_fields():
    """When materials is non-empty, legacy single fields are ignored."""
    result = calc_structured_quote(
        processes=[],
        materials=[MaterialLine(name="S235", qty_kg=10.0, price_per_kg=10.0)],  # 100
        material_weight_kg=999.0,
        material_price_per_kg=999.0,
        labor_rate=90.0,
    )
    assert result.material_total == pytest.approx(100.0)


def test_kalkulacja_does_not_stack_weight_component():
    """In kalkulacja, weight pricing is ignored to avoid double counting."""
    result = calc_structured_quote(
        processes=[],
        materials=[MaterialLine(name="S235", qty_kg=10.0, price_per_kg=10.0)],
        weight_kg=120.0,
        weight_rate_pln_kg=8.0,
        overhead_pct=0.0,
        margin_pct=0.0,
        labor_rate=90.0,
    )
    assert result.weight_total == pytest.approx(0.0)
    assert result.base == pytest.approx(100.0)


def test_od_masy_replaces_materials_and_operations():
    """od_masy is an alternative all-in method, not an additive component."""
    result = calc_structured_quote(
        method="od_masy",
        processes=[ProcessItem(name="Cięcie", hours=2.0, rate_per_hour=90.0)],
        materials=[MaterialLine(name="S235", qty_kg=10.0, price_per_kg=10.0)],
        weight_kg=120.0,
        weight_rate_pln_kg=8.0,
        overhead_pct=0.50,
        margin_pct=0.50,
        transport_cost=40.0,
        labor_rate=90.0,
    )
    assert result.ops_total == pytest.approx(180.0)
    assert result.material_total == pytest.approx(100.0)
    assert result.weight_total == pytest.approx(960.0)
    assert result.base == pytest.approx(960.0)
    assert result.total_net == pytest.approx(1000.0)


# ─── Section B: structured router persistence/round-trip ─────────────────────

def test_structured_quote_persists_materials_json():
    db = make_test_db()
    try:
        order = make_order(db)
        payload = QuoteStructuredCreate(
            processes=[ProcessItem(name="Cięcie", hours=1.0, rate_per_hour=90.0)],
            materials=[
                MaterialLine(name="S235", qty_kg=50.0, price_per_kg=4.0),
                MaterialLine(name="Hardox", cost=300.0),
            ],
        )
        from routers.quotes import create_structured_quote
        out = create_structured_quote(
            order_id=order.id, payload=payload, db=db, current_user=_MOCK_USER
        )

        assert isinstance(out.materials_json, list)
        assert len(out.materials_json) == 2
        assert out.materials_json[0]["name"] == "S235"
        # material_cost column stores summed material_total: 50*4 + 300 = 500
        assert float(out.material_cost) == pytest.approx(500.0)
    finally:
        db.close()


def test_old_quote_without_materials_json_still_loads():
    """A pre-existing quote with only legacy single fields returns via GET unchanged."""
    db = make_test_db()
    try:
        order = make_order(db)
        order.status = OrderStatus.quoted
        db.flush()
        quote = Quote(
            order_id           = order.id,
            material_weight_kg = 40.0,
            material_price_per_kg = 5.0,
            material_cost      = 200.0,
            total_net          = 1000.0,
            margin_pct         = 0.20,
            labor_hours        = 0,
            overhead_pct       = 0,
        )
        db.add(quote)
        db.commit()

        from routers.quotes import get_quote
        out = get_quote(order_id=order.id, db=db, _=_MOCK_USER)
        # No crash, legacy fields intact, materials_json absent/empty.
        assert float(out.material_weight_kg) == pytest.approx(40.0)
        assert not out.materials_json
    finally:
        db.close()
