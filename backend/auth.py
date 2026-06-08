"""
JWT auth — minimal, no DB users table needed.
Hardcoded users match the frontend USERS list.
Secret pulled from env var JWT_SECRET.
PINs stored as bcrypt hashes — never plaintext.
"""
import os
from datetime import datetime, timedelta, timezone
from typing import Optional

import bcrypt
import jwt
from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

_APP_ENV = os.getenv("APP_ENV", "prod").lower()
_raw_secret = os.getenv("JWT_SECRET")
if not _raw_secret:
    if _APP_ENV in ("dev", "test"):
        _raw_secret = "demo-dev-secret-change-me"
    else:
        raise RuntimeError(
            "JWT_SECRET environment variable is required in production. "
            "Set APP_ENV=dev or APP_ENV=test to use a default secret locally."
        )
JWT_SECRET    = _raw_secret
JWT_ALGORITHM = "HS256"
JWT_TTL_HOURS = 12

_USERS = [
    {
        "id": 1, "name": "Biuro", "role": "biuro",
        # bcrypt hash of "1111"
        "pin_hash": "$2b$12$ratSwRnh/khB8/Cz77tbVejScVvo7b/qi/GZ3sJTZJ8amTEQ0lt4m",
    },
    {
        "id": 2, "name": "Technolog", "role": "technolog",
        # bcrypt hash of "2222"
        "pin_hash": "$2b$12$YgKZF1sb4Otch/pHholMX.txdzM8N/NOr7M6s3KEZQhZ3UGZtSB3C",
    },
    {
        "id": 3, "name": "CEO", "role": "ceo",
        # bcrypt hash of "3333"
        "pin_hash": "$2b$12$wXvfsk7.rKFiZJ.z2CC4jOs/80RfaHcfkt36zBMNWEyyVMjNz6yCW",
    },
    {
        "id": 4, "name": "Dyrektor Produkcji", "role": "dyrektor_produkcji",
        # bcrypt hash of "4444"
        "pin_hash": "$2b$12$.ZNEKnH90g7rNEg8P7IKs.DPlRzm448L7r1BMaAjVjJDuxpNYrUoq",
    },
]

_bearer = HTTPBearer(auto_error=False)


def login(role: str, pin: str) -> Optional[dict]:
    user = next((u for u in _USERS if u["role"] == role), None)
    if not user:
        return None
    if not bcrypt.checkpw(pin.encode(), user["pin_hash"].encode()):
        return None
    payload = {
        "sub":  str(user["id"]),
        "role": user["role"],
        "name": user["name"],
        "exp":  datetime.now(timezone.utc) + timedelta(hours=JWT_TTL_HOURS),
    }
    token = jwt.encode(payload, JWT_SECRET, algorithm=JWT_ALGORITHM)
    return {"access_token": token, "token_type": "bearer", "role": user["role"], "name": user["name"]}


def get_current_user(credentials: HTTPAuthorizationCredentials = Depends(_bearer)) -> dict:
    """FastAPI dependency — raises 401 if token missing or invalid."""
    if not credentials:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Brak tokenu")
    try:
        payload = jwt.decode(credentials.credentials, JWT_SECRET, algorithms=[JWT_ALGORITHM])
        return {"id": payload["sub"], "role": payload["role"], "name": payload["name"]}
    except jwt.ExpiredSignatureError:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Token wygasł")
    except jwt.InvalidTokenError:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Nieprawidłowy token")


def require_role(*roles: str):
    """FastAPI dependency factory — raises 403 if user role not in allowed list."""
    def _check(user: dict = Depends(get_current_user)) -> dict:
        if user["role"] not in roles:
            raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Brak uprawnień")
        return user
    return Depends(_check)
