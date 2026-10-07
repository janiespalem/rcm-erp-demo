"""Query budget excludes authentication and setup; includes the whole report function."""
import pytest
from sqlalchemy import event
from sqlalchemy.orm import Session

from models import Order, OrderOperation, OrderStatus, Quote
from routers.analytics import get_rentownosc
from tests.helpers import make_test_db


def assert_report_query_budget(engine, orders_count, quoted_count):
    with Session(engine) as db:
        for i in range(orders_count):
            order = Order(client=f"Synthetic {i}", status=OrderStatus.in_production)
            db.add(order)
            db.flush()
            if i < quoted_count:
                db.add(Quote(order_id=order.id, total_net=1000,
                             material_weight_kg=10, material_price_per_kg=2,
                             labor_hours=2))
                db.add(OrderOperation(order_id=order.id, actual_hours=3 if i % 2 else None))
        db.commit()

    statements = []

    def record(conn, cursor, statement, parameters, context, executemany):
        statements.append(statement)

    # Fresh identity map: cached relationships must not hide N+1.
    event.listen(engine, "before_cursor_execute", record)
    try:
        with Session(engine) as db:
            result = get_rentownosc(db)
    finally:
        event.remove(engine, "before_cursor_execute", record)
    assert len(result) == quoted_count
    assert {r["actual_hours"] for r in result} <= {None, 3.0}
    assert all(r["cena_pln"] == 1000 for r in result)
    for row in result:
        expected_cost = 20 + (3 if row["actual_hours"] else 2) * 90
        assert row["koszt_pln"] == expected_cost
        assert row["marza_pln"] == 1000 - expected_cost
        assert row["marza_pct"] == round((1000 - expected_cost) / 10, 1)
    assert len(statements) <= 4, f"Report executed {len(statements)} SQL statements"


@pytest.mark.parametrize("orders_count,quoted_count", [(0, 0), (33, 17), (50, 50)])
def test_rentownosc_at_most_four_queries(orders_count, quoted_count):
    db = make_test_db()
    engine = db.get_bind()
    db.close()
    try:
        assert_report_query_budget(engine, orders_count, quoted_count)
    finally:
        engine.dispose()
