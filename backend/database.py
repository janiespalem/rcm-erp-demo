import os
import pathlib
from urllib.parse import quote

from sqlalchemy import create_engine, event
from sqlalchemy.orm import sessionmaker

# --- Konfiguracja bazy ---
BACKEND_DIR = pathlib.Path(__file__).parent

def _normalize_database_url(url: str) -> str:
    if url.startswith("postgres://"):
        url = url.replace("postgres://", "postgresql://", 1)
    if url.startswith("postgresql://"):
        url = url.replace("postgresql://", "postgresql+psycopg://", 1)
    return url


def _database_url_from_env() -> str:
    raw = os.getenv("DATABASE_URL")
    if raw:
        return _normalize_database_url(raw)
    password = os.getenv("POSTGRES_PASSWORD")
    if password:
        user = quote(os.getenv("POSTGRES_USER", "factoryflow_demo"), safe="")
        password = quote(password, safe="")
        host = os.getenv("POSTGRES_HOST", "db")
        port = os.getenv("POSTGRES_PORT", "5432")
        database = quote(os.getenv("POSTGRES_DB", "factoryflow_demo"), safe="")
        return f"postgresql+psycopg://{user}:{password}@{host}:{port}/{database}"
    if os.getenv("APP_ENV", "prod").lower() in ("dev", "test"):
        return f"sqlite:///{BACKEND_DIR / 'factoryflow_demo.db'}"
    raise RuntimeError("DATABASE_URL or POSTGRES_PASSWORD is required in production; refusing SQLite fallback.")


DATABASE_URL = _database_url_from_env()
_connect_args = {"check_same_thread": False} if DATABASE_URL.startswith("sqlite") else {}
engine = create_engine(
    DATABASE_URL,
    connect_args=_connect_args,
    pool_pre_ping=True,
    pool_recycle=300,
)

@event.listens_for(engine, "connect")
def _enable_sqlite_foreign_keys(dbapi_connection, connection_record) -> None:
    if not DATABASE_URL.startswith("sqlite"):
        return
    cursor = dbapi_connection.cursor()
    cursor.execute("PRAGMA foreign_keys=ON")
    cursor.close()

SessionLocal = sessionmaker(autocommit=False, autoflush=False, bind=engine)

def init_db():
    """Return configured engine. Schema changes are managed by Alembic."""
    return engine

# Wyciągnięte z main.py jako współdzielona zależność
def get_db():
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
