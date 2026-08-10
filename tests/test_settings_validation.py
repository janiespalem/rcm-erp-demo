"""
Focused tests for PATCH /api/settings/{key} validation.
Direct router function calls — no TestClient.
"""
import os
import sys

import pytest

os.environ.setdefault("APP_ENV", "test")
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "backend"))

from fastapi import HTTPException
from sqlalchemy import create_engine
from sqlalchemy.orm import Session
from sqlalchemy.pool import StaticPool

from models import Base, Setting
from schemas import SettingUpdate
from routers.settings import update_setting, _validate_setting

_MOCK_USER = {"id": "1", "role": "technolog", "name": "Dyrektor"}


def _make_db(seed: dict | None = None) -> Session:
    engine = create_engine(
        "sqlite:///:memory:",
        connect_args={"check_same_thread": False},
        poolclass=StaticPool,
    )
    Base.metadata.create_all(engine)
    db = Session(engine)
    for k, v in (seed or {}).items():
        db.add(Setting(key=k, value=v))
    db.commit()
    return db


# ─── _validate_setting unit tests ─────────────────────────────────────────────

def test_validate_unknown_key_passthrough():
    assert _validate_setting("some_unknown_key", "anything") == "anything"


def test_validate_labor_rate_valid():
    assert _validate_setting("labor_rate_pln", "90.0") == "90.0"


def test_validate_labor_rate_invalid_string():
    with pytest.raises(HTTPException) as exc:
        _validate_setting("labor_rate_pln", "abc")
    assert exc.value.status_code == 422


def test_validate_labor_rate_zero_invalid():
    with pytest.raises(HTTPException) as exc:
        _validate_setting("labor_rate_pln", "0")
    assert exc.value.status_code == 422


def test_validate_labor_rate_too_large():
    with pytest.raises(HTTPException) as exc:
        _validate_setting("labor_rate_pln", "99999")
    assert exc.value.status_code == 422


def test_validate_overhead_pct_valid():
    assert _validate_setting("default_overhead_pct", "0.10") == "0.10"


def test_validate_overhead_pct_negative():
    with pytest.raises(HTTPException) as exc:
        _validate_setting("default_overhead_pct", "-0.1")
    assert exc.value.status_code == 422


def test_validate_margin_pct_above_one():
    with pytest.raises(HTTPException) as exc:
        _validate_setting("default_margin_pct", "1.5")
    assert exc.value.status_code == 422


def test_validate_min_order_value_zero_ok():
    assert _validate_setting("min_order_value", "0") == "0"


# ─── Router integration tests ─────────────────────────────────────────────────

def test_update_setting_valid_labor_rate():
    db = _make_db({"labor_rate_pln": "80.0"})
    try:
        result = update_setting("labor_rate_pln", SettingUpdate(value="95.0"), db=db, _=_MOCK_USER)
        assert result["value"] == "95.0"
        stored = db.get(Setting, "labor_rate_pln")
        assert stored.value == "95.0"
    finally:
        db.close()


def test_update_setting_invalid_labor_rate_returns_422():
    db = _make_db({"labor_rate_pln": "80.0"})
    try:
        with pytest.raises(HTTPException) as exc:
            update_setting("labor_rate_pln", SettingUpdate(value="abc"), db=db, _=_MOCK_USER)
        assert exc.value.status_code == 422
        # Value must not have changed
        assert db.get(Setting, "labor_rate_pln").value == "80.0"
    finally:
        db.close()


def test_update_setting_negative_margin_returns_422():
    db = _make_db({"default_margin_pct": "0.20"})
    try:
        with pytest.raises(HTTPException) as exc:
            update_setting("default_margin_pct", SettingUpdate(value="-0.5"), db=db, _=_MOCK_USER)
        assert exc.value.status_code == 422
    finally:
        db.close()


def test_update_setting_unknown_key_404():
    db = _make_db()
    try:
        with pytest.raises(HTTPException) as exc:
            update_setting("nonexistent", SettingUpdate(value="123"), db=db, _=_MOCK_USER)
        assert exc.value.status_code == 404
    finally:
        db.close()
