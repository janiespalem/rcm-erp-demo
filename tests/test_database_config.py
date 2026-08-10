import pytest

from database import _database_url_from_env
from main import health, ping
from tests.helpers import make_test_db


def test_prod_requires_database_url(monkeypatch):
    monkeypatch.delenv("DATABASE_URL", raising=False)
    monkeypatch.delenv("POSTGRES_PASSWORD", raising=False)
    monkeypatch.setenv("APP_ENV", "prod")

    with pytest.raises(RuntimeError, match="DATABASE_URL"):
        _database_url_from_env()


def test_postgres_password_is_url_encoded(monkeypatch):
    monkeypatch.delenv("DATABASE_URL", raising=False)
    monkeypatch.setenv("POSTGRES_PASSWORD", "a@b:/?#[]")
    monkeypatch.setenv("POSTGRES_HOST", "db")

    url = _database_url_from_env()

    assert "a%40b%3A%2F%3F%23%5B%5D" in url
    assert url.endswith("@db:5432/factoryflow_demo")


def test_test_env_keeps_sqlite_fallback(monkeypatch):
    monkeypatch.delenv("DATABASE_URL", raising=False)
    monkeypatch.setenv("APP_ENV", "test")

    assert _database_url_from_env().startswith("sqlite:///")


def test_health_checks_database():
    db = make_test_db()
    try:
        assert health(db)["status"] == "ok"
    finally:
        db.close()


def test_ping_skips_database():
    assert ping()["status"] == "ok"
