import time

import jwt
import pytest
from fastapi import HTTPException
from fastapi.security import HTTPAuthorizationCredentials
from starlette.requests import Request

from auth import JWT_ALGORITHM, JWT_SECRET, authenticate_user, get_current_user, set_user_pin
from main import LoginRequest, api_login
from models import User, UserRole
from tests.helpers import make_test_db


def _user(db, *, name="Test Office", role=UserRole.biuro, legacy_pin=None):
    user = User(name=name, role=role, pin=legacy_pin)
    db.add(user)
    db.commit()
    db.refresh(user)
    return user


def test_authenticate_user_uses_database_pin_hash():
    db = make_test_db()
    try:
        user = _user(db)
        set_user_pin(db, user.id, "4826")

        result = authenticate_user(db, "biuro", "4826")

        assert result["role"] == "biuro"
        assert result["name"] == "Test Office"
        claims = jwt.decode(result["access_token"], JWT_SECRET, algorithms=[JWT_ALGORITHM])
        assert claims["exp"] - time.time() <= 3600
        assert authenticate_user(db, "biuro", "0000") is None
    finally:
        db.close()


def test_authenticate_user_ignores_legacy_plaintext_pin():
    db = make_test_db()
    try:
        _user(db, legacy_pin="4826")
        assert authenticate_user(db, "biuro", "4826") is None
    finally:
        db.close()


@pytest.mark.parametrize("pin", ["", "123", "12345", "１２３４", "x" * 73])
def test_authenticate_user_rejects_malformed_pin(pin):
    db = make_test_db()
    try:
        _user(db)
        assert authenticate_user(db, "biuro", pin) is None
    finally:
        db.close()


def test_get_current_user_loads_canonical_database_identity():
    db = make_test_db()
    try:
        user = _user(db)
        set_user_pin(db, user.id, "4826")
        token = authenticate_user(db, "biuro", "4826")["access_token"]

        user.name = "Updated Office"
        db.commit()
        current = get_current_user(
            HTTPAuthorizationCredentials(scheme="Bearer", credentials=token),
            db,
        )

        assert current == {"id": str(user.id), "role": "biuro", "name": "Updated Office"}
    finally:
        db.close()


def test_login_endpoint_uses_database_session():
    db = make_test_db()
    try:
        user = _user(db)
        set_user_pin(db, user.id, "4826")
        request = Request({"type": "http", "client": ("127.0.0.1", 1234)})

        result = api_login(LoginRequest(role="biuro", pin="4826"), request, db)

        assert result["name"] == "Test Office"
        with pytest.raises(HTTPException) as exc:
            api_login(LoginRequest(role="biuro", pin="0000"), request, db)
        assert exc.value.status_code == 401
    finally:
        db.close()


def test_set_user_pin_clears_plaintext_and_rejects_duplicate_pin():
    db = make_test_db()
    try:
        office = _user(db, legacy_pin="legacy")
        technologist = _user(db, name="Test Technologist", role=UserRole.technolog)

        set_user_pin(db, office.id, "4826")

        assert office.pin is None
        assert office.pin_hash.startswith("$2")
        with pytest.raises(ValueError, match="innego użytkownika"):
            set_user_pin(db, technologist.id, "4826")
    finally:
        db.close()
