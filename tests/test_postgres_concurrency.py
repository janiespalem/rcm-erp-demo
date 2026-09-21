"""Real HTTP requests, independent transactions, migrated disposable PostgreSQL.

Set TEST_DATABASE_URL to a database named rcm_erp_test; never uses DATABASE_URL.
"""
import os
import socket
import subprocess
import sys
import threading
import time
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from datetime import date, datetime, timezone

import httpx
import pytest
import uvicorn
from fastapi import HTTPException
from sqlalchemy import create_engine, event, text, inspect
from sqlalchemy.engine import make_url
from sqlalchemy.orm import Session

from auth import get_current_user
from database import get_db
from models import ApprovedMaterial, Order, OrderEvent, OrderStatus, Quote, StockMovement
from main import app
from services import order_service
from tests.helpers import TEST_USERS
from models import ShiftReport, ShiftReportAudit, User, UserRole


@pytest.fixture(scope="module")
def pg_engine():
    url = os.environ.get("TEST_DATABASE_URL")
    if not url:
        pytest.skip("TEST_DATABASE_URL not set: PostgreSQL concurrency suite")
    parsed = make_url(url)
    if parsed.get_backend_name() != "postgresql" or parsed.database != "rcm_erp_test":
        pytest.fail("Requires dedicated PostgreSQL database rcm_erp_test")
    subprocess.run(
        [sys.executable, "-m", "alembic", "upgrade", "head"],
        env={**os.environ, "DATABASE_URL": url, "APP_ENV": "test"}, check=True,
    )
    engine = create_engine(url, pool_size=40, max_overflow=0,
                           connect_args={"options": "-c statement_timeout=15000 -c lock_timeout=10000"})
    yield engine
    engine.dispose()


@pytest.fixture
def pg(pg_engine):
    # Only this explicitly named disposable database is reset, before each case.
    with pg_engine.begin() as conn:
        conn.execute(text("TRUNCATE orders, order_counters, approved_materials, price_history RESTART IDENTITY CASCADE"))
    return pg_engine


@pytest.fixture
def http(pg):
    def session():
        with Session(pg, autoflush=False) as db:
            yield db

    app.dependency_overrides[get_db] = session
    app.dependency_overrides[get_current_user] = lambda: TEST_USERS["technologist"]
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    sock.listen(128)
    server = uvicorn.Server(uvicorn.Config(app, log_level="error", lifespan="off"))
    thread = threading.Thread(target=server.run, kwargs={"sockets": [sock]}, daemon=True)
    thread.start()
    deadline = time.monotonic() + 10
    while not server.started and time.monotonic() < deadline:
        time.sleep(.01)
    assert server.started
    try:
        with httpx.Client(base_url=f"http://127.0.0.1:{sock.getsockname()[1]}",
                          timeout=30, limits=httpx.Limits(max_connections=100)) as client:
            yield client
    finally:
        server.should_exit = True
        thread.join(10)
        sock.close()
        app.dependency_overrides.clear()


def race(count, action):
    barrier = threading.Barrier(count, timeout=20)

    def run(index):
        barrier.wait()
        return action(index)

    with ThreadPoolExecutor(max_workers=count) as executor:
        return list(executor.map(run, range(count)))


def test_shift_report_unique_creation_and_atomic_version(http, pg):
    with Session(pg) as db:
        db.execute(text("TRUNCATE shift_reports RESTART IDENTITY CASCADE"))
        if db.get(User, 2) is None:
            db.add(User(id=2, name="Synthetic technologist", role=UserRole.technolog))
        db.commit()
    replies = race(10, lambda _: http.post('/api/shift-reports', json={'report_date': '2026-09-16', 'shift': 'I'}))
    assert all(r.status_code in (200, 409) for r in replies)
    with Session(pg) as db:
        assert db.query(ShiftReport).count() == 1
        assert db.query(ShiftReportAudit).count() == 1
    report = next(r.json() for r in replies if r.status_code == 200)
    barrier = threading.Barrier(2, timeout=10)

    def synchronize(session, *_):
        if any(isinstance(obj, ShiftReport) for obj in session.dirty):
            barrier.wait()

    event.listen(Session, 'before_flush', synchronize)
    try:
        replies = race(2, lambda i: http.put(f"/api/shift-reports/{report['id']}", json={
            'version_id': report['version_id'], 'fields': dict(report['fields'], remarks=f'Writer {i}'),
        }))
    finally:
        event.remove(Session, 'before_flush', synchronize)
    assert Counter(r.status_code for r in replies) == {200: 1, 409: 1}
    winner = next(r.json() for r in replies if r.status_code == 200)
    with Session(pg) as db:
        assert db.get(ShiftReport, report['id']).fields == winner['fields']
        assert db.query(ShiftReportAudit).count() == 2
    # Repeat against an existing coalesced draft event: the losing writer must
    # not mutate that event or increment its counter.
    event.listen(Session, 'before_flush', synchronize)
    try:
        replies = race(2, lambda i: http.put(f"/api/shift-reports/{report['id']}", json={
            'version_id':winner['version_id'], 'fields':dict(winner['fields'], remarks=f'Next writer {i}'),
        }))
    finally:
        event.remove(Session, 'before_flush', synchronize)
    assert Counter(r.status_code for r in replies) == {200:1,409:1}
    winner = next(r.json() for r in replies if r.status_code == 200)
    with Session(pg) as db:
        assert db.query(ShiftReportAudit).count() == 2
        entry = db.query(ShiftReportAudit).filter_by(action='saved').one()
        assert entry.after['draft_save_count'] == 2 and entry.after['fields'] == winner['fields']


def test_shift_admin_delete_races_save_atomically(http, pg, monkeypatch):
    monkeypatch.setenv('SHIFT_REPORT_ADMIN_IPS', '127.0.0.1')
    with Session(pg) as db:
        db.execute(text("TRUNCATE shift_reports RESTART IDENTITY CASCADE"))
        if db.get(User, 2) is None:
            db.add(User(id=2, name="Synthetic technologist", role=UserRole.technolog))
        db.commit()
    report = http.post('/api/shift-reports', json={'report_date':'2026-09-17', 'shift':'II'}).json()
    barrier = threading.Barrier(2, timeout=10)

    def synchronize(session, *_):
        if any(isinstance(obj, ShiftReport) for obj in session.dirty):
            barrier.wait()

    event.listen(Session, 'before_flush', synchronize)
    try:
        replies = race(2, lambda i: http.post(f"/api/shift-reports/{report['id']}/delete", json={
            'version_id':report['version_id'], 'reason':'Concurrency test',
        }) if i else http.put(f"/api/shift-reports/{report['id']}", json={
            'version_id':report['version_id'], 'fields':dict(report['fields'], remarks='Concurrent save'),
        }))
    finally:
        event.remove(Session, 'before_flush', synchronize)
    assert sorted(r.status_code for r in replies) in ([200,409], [204,409])
    with Session(pg) as db:
        assert db.query(ShiftReportAudit).count() == 2
        persisted = db.get(ShiftReport, report['id'])
        assert persisted.version_id == report['version_id'] + 1
        assert (persisted.deleted_at is not None) == (replies[1].status_code == 204)


def test_shift_timestamps_are_independent_of_database_timezone(pg):
    from routers.shift_reports import create, CreateReport
    with Session(pg) as db:
        db.execute(text("TRUNCATE shift_reports RESTART IDENTITY CASCADE"))
        if db.get(User, 2) is None:
            db.add(User(id=2, name="Synthetic technologist", role=UserRole.technolog))
        db.commit()
        db.execute(text("SET LOCAL TIME ZONE 'Europe/Warsaw'"))
        result = create(CreateReport(report_date=date(2026, 9, 16), shift='II'), db, TEST_USERS['technologist'])
        assert abs((datetime.now(timezone.utc) - result['created_at']).total_seconds()) < 10
        audit = db.query(ShiftReportAudit).filter_by(report_id=result['id']).one()
        assert abs((datetime.now(timezone.utc) - audit.created_at).total_seconds()) < 10


def seed_order(pg, status):
    with Session(pg) as db:
        material = ApprovedMaterial(name="Concurrency test steel")
        db.add(material)
        db.flush()
        order = Order(client="Synthetic client", status=status, approved_material_id=material.id)
        db.add(order)
        db.flush()
        db.add(Quote(order_id=order.id, material_weight_kg=12.5, total_net=100))
        db.commit()
        return order.id


@pytest.mark.parametrize("existing_counter", [False, True], ids=["cold-counter", "existing-counter"])
def test_100_concurrent_order_creations(http, pg, existing_counter):
    if existing_counter:
        assert http.post("/api/orders/", json={
            "client": "Seed counter", "deadline": date.today().isoformat(),
        }).status_code == 201
    replies = race(100, lambda i: http.post("/api/orders/", json={
        "client": f"Synthetic {i}", "deadline": date.today().isoformat(),
    }))
    assert Counter(r.status_code for r in replies) == {201: 100}
    numbers = [r.json()["order_number"] for r in replies]
    assert len(set(numbers)) == 100
    with Session(pg) as db:
        assert db.query(Order).count() == 100 + int(existing_counter)
        assert db.query(OrderEvent).filter_by(event_type="created").count() == 100 + int(existing_counter)


@pytest.mark.parametrize("action,initial,final,event_type,movements", [
    ("confirm", OrderStatus.quoted, OrderStatus.in_production, "confirmed", 0),
    ("complete", OrderStatus.in_production, OrderStatus.gotowe, "completed", 1),
    ("deliver", OrderStatus.in_production, OrderStatus.wydane, "delivered", 1),
])
def test_20_concurrent_transitions(http, pg, action, initial, final, event_type, movements):
    order_id = seed_order(pg, initial)
    replies = race(20, lambda _: http.post(f"/api/orders/{order_id}/{action}"))
    assert Counter(r.status_code for r in replies) == {200: 1, 409: 19}
    with Session(pg) as db:
        order = db.get(Order, order_id)
        assert order.status == final
        events = db.query(OrderEvent).filter_by(order_id=order_id).all()
        assert [e.event_type for e in events] == [event_type]
        assert events[0].old_status == initial.value
        assert events[0].new_status == final.value
        issues = db.query(StockMovement).filter_by(order_id=order_id).all()
        assert len(issues) == movements
        if movements:
            assert float(issues[0].qty) == 12.5
            assert order.completed_at is not None
        if action == "deliver":
            assert order.delivered_at is not None


def test_20_updates_of_same_version(http, pg):
    order_id = seed_order(pg, OrderStatus.in_production)
    before = http.get(f"/api/orders/{order_id}").json()
    # Force all transactions to read the SAME row version before any UPDATE.
    # Without this barrier a fast winner could hide a non-atomic check/write.
    flush_barrier = threading.Barrier(20, timeout=20)

    def synchronize_updates(session, flush_context, instances):
        if any(isinstance(obj, Order) and obj.id == order_id for obj in session.dirty):
            flush_barrier.wait()

    event.listen(Session, "before_flush", synchronize_updates)
    try:
        replies = race(20, lambda i: http.patch(f"/api/orders/{order_id}", json={
            "version_id": before["version_id"], "notes": f"Edit {i}",
        }))
    finally:
        event.remove(Session, "before_flush", synchronize_updates)
    assert Counter(r.status_code for r in replies) == {200: 1, 409: 19}
    winner = next(r.json() for r in replies if r.status_code == 200)
    after = http.get(f"/api/orders/{order_id}").json()
    assert after["notes"] == winner["notes"]
    assert after["version_id"] == before["version_id"] + 1
    with Session(pg) as db:
        assert db.query(OrderEvent).filter_by(order_id=order_id, event_type="edited").count() == 1


def test_stale_form_after_delivery_and_missing_version_are_rejected(http, pg):
    order_id = seed_order(pg, OrderStatus.in_production)
    before = http.get(f"/api/orders/{order_id}").json()
    assert http.post(f"/api/orders/{order_id}/deliver").status_code == 200
    assert http.patch(f"/api/orders/{order_id}", json={
        "version_id": before["version_id"], "notes": "stale",
    }).status_code == 409
    assert http.patch(f"/api/orders/{order_id}", json={"notes": "no version"}).status_code == 422
    assert http.get(f"/api/orders/{order_id}").json()["notes"] is None


def test_lock_refreshes_an_order_already_loaded_in_session(pg):
    order_id = seed_order(pg, OrderStatus.in_production)
    with Session(pg) as stale, Session(pg) as current:
        cached = stale.get(Order, order_id)
        order_service.deliver_order(current, order_id, TEST_USERS["technologist"])
        assert cached.status == OrderStatus.in_production
        with pytest.raises(HTTPException) as exc:
            order_service.deliver_order(stale, order_id, TEST_USERS["technologist"])
        assert exc.value.status_code == 409


def test_failed_audit_rolls_back_delivery_and_material_issue(pg):
    order_id = seed_order(pg, OrderStatus.in_production)

    def fail_audit(conn, cursor, statement, parameters, context, executemany):
        if statement.startswith("INSERT INTO order_events"):
            raise RuntimeError("injected audit failure")

    event.listen(pg, "before_cursor_execute", fail_audit)
    try:
        with Session(pg) as db, pytest.raises(RuntimeError, match="injected audit failure"):
            order_service.deliver_order(db, order_id, TEST_USERS["technologist"])
    finally:
        event.remove(pg, "before_cursor_execute", fail_audit)
    with Session(pg) as db:
        order = db.get(Order, order_id)
        assert order.status == OrderStatus.in_production
        assert order.completed_at is None and order.delivered_at is None
        assert db.query(StockMovement).count() == 0
        assert db.query(OrderEvent).count() == 0


def test_complete_then_deliver_does_not_issue_material_twice(http, pg):
    order_id = seed_order(pg, OrderStatus.in_production)
    assert http.post(f"/api/orders/{order_id}/complete").status_code == 200
    replies = race(20, lambda _: http.post(f"/api/orders/{order_id}/deliver"))
    assert Counter(r.status_code for r in replies) == {200: 1, 409: 19}
    with Session(pg) as db:
        assert db.query(StockMovement).filter_by(order_id=order_id).count() == 1
        assert [e.event_type for e in db.query(OrderEvent).filter_by(
            order_id=order_id).order_by(OrderEvent.id)] == ["completed", "delivered"]


def test_noop_patch_consumes_version(http, pg):
    order_id = seed_order(pg, OrderStatus.in_production)
    before = http.get(f"/api/orders/{order_id}").json()
    payload = {"version_id": before["version_id"]}
    assert http.patch(f"/api/orders/{order_id}", json=payload).status_code == 200
    assert http.patch(f"/api/orders/{order_id}", json=payload).status_code == 409


def test_version_migration_preserves_existing_orders(pg):
    from importlib import import_module
    from alembic.migration import MigrationContext
    from alembic.operations import Operations

    order_id = seed_order(pg, OrderStatus.in_production)
    migration = import_module('migrations.versions.20260912_0001_order_version')
    with pg.connect() as conn:
        revision = conn.scalar(text('SELECT version_num FROM alembic_version'))

    def migrate(direction):
        # Exercise this migration's actual PostgreSQL DDL, independently of
        # later irreversible reporting migrations; never fake the ledger.
        with pg.begin() as conn:
            with Operations.context(MigrationContext.configure(conn)):
                getattr(migration, direction)()

    try:
        migrate("downgrade")
        assert "version_id" not in {c["name"] for c in inspect(pg).get_columns("orders")}
        migrate("upgrade")
        with Session(pg) as db:
            order = db.get(Order, order_id)
            assert order.client == "Synthetic client"
            assert order.version_id == 1
            assert order.status == OrderStatus.in_production
            assert order.quote.material_weight_kg == 12.5
            assert db.scalar(text('SELECT version_num FROM alembic_version')) == revision
    finally:
        migrate("upgrade")


@pytest.mark.parametrize("orders_count,quoted_count", [(33, 17), (50, 50)])
def test_postgres_report_query_budget(pg, orders_count, quoted_count):
    from tests.test_rentownosc_queries import assert_report_query_budget
    assert_report_query_budget(pg, orders_count, quoted_count)


def test_unhandled_orm_conflict_returns_409_and_rolls_back(http, pg, monkeypatch):
    from sqlalchemy.orm.exc import StaleDataError

    order_id = seed_order(pg, OrderStatus.quoted)

    def conflict(db, order_id, user=None):
        order = db.get(Order, order_id)
        order.notes = "must roll back"
        db.flush()
        raise StaleDataError("injected mapper conflict")

    monkeypatch.setattr(order_service, "confirm_order", conflict)
    response = http.post(f"/api/orders/{order_id}/confirm")
    assert response.status_code == 409
    assert "detail" in response.json()
    with Session(pg) as db:
        assert db.get(Order, order_id).notes is None
