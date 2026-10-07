"""Database-backed PIN authentication and JWT authorization."""
import os
from datetime import datetime, timedelta, timezone
from typing import Optional

import bcrypt
import jwt
from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy import select
from sqlalchemy.orm import Session

from database import get_db
from models import User, UserRole

_APP_ENV = os.getenv("APP_ENV", "prod").lower()
_raw_secret = os.getenv("JWT_SECRET")
if not _raw_secret:
    if _APP_ENV in ("dev", "test"):
        _raw_secret = "factoryflow-demo-dev-secret-change-me"
    else:
        raise RuntimeError(
            "JWT_SECRET environment variable is required in production. "
            "Set APP_ENV=dev or APP_ENV=test to use a default secret locally."
        )
JWT_SECRET    = _raw_secret
JWT_ALGORITHM = "HS256"
JWT_TTL_HOURS = 1

_LOGIN_ROLES = (UserRole.biuro, UserRole.technolog, UserRole.ceo, UserRole.produkcja)

_bearer = HTTPBearer(auto_error=False)


def _valid_pin(pin: str) -> bool:
    return len(pin) == 4 and pin.isascii() and pin.isdigit()


def _pin_matches(pin: str, pin_hash: str | None) -> bool:
    if not _valid_pin(pin) or not pin_hash:
        return False
    try:
        return bcrypt.checkpw(pin.encode(), pin_hash.encode())
    except (TypeError, ValueError):
        return False


def authenticate_user(db: Session, role: str, pin: str) -> Optional[dict]:
    if not _valid_pin(pin):
        return None
    try:
        user_role = UserRole(role)
    except ValueError:
        return None

    if user_role not in _LOGIN_ROLES:
        return None

    # ponytail: role groups are tiny; add a login identifier if they grow.
    users = db.scalars(
        select(User).where(User.role == user_role, User.pin_hash.is_not(None))
    ).all()
    user = next((candidate for candidate in users if _pin_matches(pin, candidate.pin_hash)), None)
    if user is None:
        return None

    payload = {
        "sub": str(user.id),
        "exp":  datetime.now(timezone.utc) + timedelta(hours=JWT_TTL_HOURS),
    }
    token = jwt.encode(payload, JWT_SECRET, algorithm=JWT_ALGORITHM)
    return {
        "access_token": token,
        "token_type": "bearer",
        "role": user.role.value,
        "name": user.name,
        "id": user.id,
        "default_shift": user.default_shift,
    }


def get_current_user(
    credentials: HTTPAuthorizationCredentials = Depends(_bearer),
    db: Session = Depends(get_db),
) -> dict:
    """FastAPI dependency — raises 401 if token missing or invalid."""
    if not credentials:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Brak tokenu")
    try:
        payload = jwt.decode(credentials.credentials, JWT_SECRET, algorithms=[JWT_ALGORITHM])
        user_id = int(payload["sub"])
    except jwt.ExpiredSignatureError:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Token wygasł")
    except (jwt.InvalidTokenError, KeyError, TypeError, ValueError):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Nieprawidłowy token")

    user = db.scalar(
        select(User).where(
            User.id == user_id,
            User.role.in_(_LOGIN_ROLES),
            User.pin_hash.is_not(None),
        )
    )
    if user is None:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Nieprawidłowy token")
    return {"id": str(user.id), "role": user.role.value, "name": user.name}


def set_user_pin(db: Session, user_id: int, pin: str) -> User:
    """Set a unique PIN hash without accepting the secret through argv."""
    if not _valid_pin(pin):
        raise ValueError("PIN musi zawierać dokładnie 4 cyfry ASCII")

    user = db.scalar(
        select(User).where(User.id == user_id, User.role.in_(_LOGIN_ROLES))
    )
    if user is None:
        raise ValueError("Użytkownik nie istnieje lub jego rola nie może się logować")

    others = db.scalars(
        select(User).where(
            User.id != user_id,
            User.role.in_(_LOGIN_ROLES),
            User.pin_hash.is_not(None),
        )
    ).all()
    if any(_pin_matches(pin, other.pin_hash) for other in others):
        raise ValueError("Ten PIN jest już używany przez innego użytkownika")

    user.pin_hash = bcrypt.hashpw(pin.encode(), bcrypt.gensalt()).decode()
    user.pin = None
    db.commit()
    db.refresh(user)
    return user


def require_role(*roles: str):
    """FastAPI dependency factory — raises 403 if user role not in allowed list."""
    def _check(user: dict = Depends(get_current_user)) -> dict:
        if user["role"] not in roles:
            raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Brak uprawnień")
        return user
    return Depends(_check)
