"""
FactoryFlow ERP Demo — FastAPI Backend
Uruchomienie: uvicorn main:app --host 0.0.0.0 --port 8000 --reload
"""
import logging
import os
import pathlib
import time
from collections import defaultdict, deque
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, Response
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel
from sqlalchemy.orm import Session
from starlette.exceptions import HTTPException as StarletteHTTPException

from auth import login as auth_login
from database import get_db, BACKEND_DIR
from models import ApprovedMaterial
from routers import orders, catalog
from routers.quotes import router as quotes_router
from routers.order_resources import router as order_resources_router
from routers.templates import router as templates_router
from routers.analytics import router as analytics_router
from routers.settings import router as settings_router
from routers.documents import router as documents_router
from service_seed import seed_service_history_from_builtin


def _ensure_zbrojenie_materials(db: Session) -> None:
    if db.query(ApprovedMaterial).count() == 0:
        return
    defaults = [
        ("S355JR zbrojeniowy Ø12/Ø6", "zbrojenie", 4.50, "Łuk LEGO: pręty Ø12 i Ø6"),
        ("Pręt żebrowany B500SP Ø6",  "zbrojenie", 4.50, "Typowy pręt zbrojeniowy"),
        ("Pręt żebrowany B500SP Ø8",  "zbrojenie", 4.50, "Typowy pręt zbrojeniowy"),
        ("Pręt żebrowany B500SP Ø10", "zbrojenie", 4.50, "Typowy pręt zbrojeniowy"),
        ("Pręt żebrowany B500SP Ø12", "zbrojenie", 4.50, "Typowy pręt zbrojeniowy"),
        ("Drut wiązałkowy",           "zbrojenie", 6.00, "Materiał pomocniczy do montażu"),
    ]
    existing = {name for (name,) in db.query(ApprovedMaterial.name).all()}
    rows = [
        ApprovedMaterial(name=name, category=category, default_rate_pln_kg=rate, notes=notes)
        for name, category, rate, notes in defaults
        if name not in existing
    ]
    if rows:
        db.add_all(rows)
        db.commit()


# ─── App ──────────────────────────────────────────────────────────────────────

def _run_startup_tasks(db: Session) -> None:
    seed_service_history_from_builtin(db)
    _ensure_zbrojenie_materials(db)


def _startup_seed_disabled() -> bool:
    if os.getenv("DISABLE_STARTUP_SEED", "").lower() in ("1", "true", "yes"):
        return True
    if os.getenv("APP_ENV", "prod").lower() in ("test",):
        return True
    return False


@asynccontextmanager
async def lifespan(app: FastAPI):
    logger = logging.getLogger(__name__)
    if _startup_seed_disabled():
        logger.info("Startup: seed skipped")
    else:
        logger.info("Startup: seeding users and service history")
        db_provider = app.dependency_overrides.get(get_db, get_db)
        db_gen      = db_provider()
        db          = next(db_gen)
        try:
            _run_startup_tasks(db)
        finally:
            db_gen.close()
        logger.info("Startup complete.")
    yield


app = FastAPI(
    title="FactoryFlow ERP Demo",
    description="System CPQ/ERP dla Demo Manufacturing Sp. z o.o. — Demo City",
    version="0.1.0",
    lifespan=lifespan,
)

app.include_router(orders.router)
app.include_router(catalog.router)
app.include_router(quotes_router)
app.include_router(order_resources_router)
app.include_router(templates_router)
app.include_router(analytics_router)
app.include_router(settings_router)
app.include_router(documents_router)

# ─── Static mounts ────────────────────────────────────────────────────────────

app.mount("/static", StaticFiles(directory=str(BACKEND_DIR / "static")), name="static")

_VITE_DIST   = BACKEND_DIR.parent / "frontend-vite" / "dist"
_VITE_ASSETS = _VITE_DIST / "assets"
_VITE_ASSETS.mkdir(parents=True, exist_ok=True)


class ViteAssets(StaticFiles):
    async def get_response(self, path: str, scope):
        try:
            response = await super().get_response(path, scope)
        except StarletteHTTPException as exc:
            if exc.status_code == 404 and path.endswith(".js"):
                return Response(
                    "window.location.reload();\nexport default {};\n",
                    media_type="application/javascript",
                    headers={"Cache-Control": "no-store, no-cache, must-revalidate, max-age=0"},
                )
            raise
        if path.endswith((".js", ".css")):
            response.headers["Cache-Control"] = "public, max-age=31536000, immutable"
        return response


app.mount("/assets", ViteAssets(directory=str(_VITE_ASSETS)), name="vite-assets")


def _no_store_file(path: pathlib.Path) -> FileResponse:
    response = FileResponse(str(path))
    response.headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0"
    response.headers["Pragma"] = "no-cache"
    response.headers["Expires"] = "0"
    return response


_app_env_cors = os.getenv("APP_ENV", "prod").lower()
_cors_raw = os.getenv("CORS_ORIGINS", "")
if _cors_raw:
    _cors_origins = [o.strip() for o in _cors_raw.split(",") if o.strip()]
elif _app_env_cors in ("dev", "test"):
    _cors_origins = ["*"]
else:
    _cors_origins = []   # prod must explicitly set CORS_ORIGINS

app.add_middleware(
    CORSMiddleware,
    allow_origins=_cors_origins,
    allow_methods=["*"],
    allow_headers=["*"],
)

# ─── Login rate limit (in-memory, per-IP+role) ────────────────────────────────
_LOGIN_WINDOW_SEC  = 60
_LOGIN_MAX_ATTEMPTS = 10
_login_hits: dict[str, deque] = defaultdict(deque)


def _check_login_rate_limit(request: Request, role: str) -> None:
    client_ip = request.client.host if request.client else "unknown"
    key       = f"{client_ip}:{role}"
    now       = time.monotonic()
    hits      = _login_hits[key]
    while hits and hits[0] < now - _LOGIN_WINDOW_SEC:
        hits.popleft()
    if len(hits) >= _LOGIN_MAX_ATTEMPTS:
        raise HTTPException(status_code=429, detail="Zbyt wiele prób logowania. Spróbuj za chwilę.")
    hits.append(now)

# ─── Health + frontend ────────────────────────────────────────────────────────

@app.get("/favicon.ico", include_in_schema=False)
def favicon():
    return Response(status_code=204)


@app.get("/favicon.svg", include_in_schema=False)
def favicon_svg():
    path = _VITE_DIST / "favicon.svg"
    if path.exists():
        return FileResponse(str(path), media_type="image/svg+xml")
    return Response(status_code=204)


@app.get("/icons.svg", include_in_schema=False)
def icons_svg():
    path = _VITE_DIST / "icons.svg"
    if path.exists():
        return FileResponse(str(path), media_type="image/svg+xml")
    raise HTTPException(status_code=404, detail="icons.svg not found")


@app.get("/api/health")
def health():
    return {"status": "ok", "system": "FactoryFlow ERP Demo"}


class LoginRequest(BaseModel):
    role: str
    pin: str


@app.post("/api/auth/login")
def api_login(payload: LoginRequest, request: Request):
    _check_login_rate_limit(request, payload.role)
    result = auth_login(payload.role, payload.pin)
    if not result:
        raise HTTPException(status_code=401, detail="Nieprawidłowy PIN lub rola")
    return result


@app.get("/", response_class=FileResponse)
def serve_frontend():
    vite_index = BACKEND_DIR.parent / "frontend-vite" / "dist" / "index.html"
    if vite_index.exists():
        return _no_store_file(vite_index)
    raise HTTPException(status_code=500, detail="Frontend build not found — run: cd frontend-vite && npm run build")


@app.get("/kalkulator-lego", response_class=FileResponse)
def serve_kalkulator_lego():
    return FileResponse(str(BACKEND_DIR.parent / "frontend" / "masonry-kalk.html"))
