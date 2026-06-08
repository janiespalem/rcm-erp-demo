# FactoryFlow ERP Demo

Sanitized public demo of an internal ERP/CPQ system for a small manufacturing workflow.

The original private project was built for production use. This repository keeps the architecture, workflow, migrations, tests, and UI structure, but removes or replaces all real company data, customer information, prices, drawings, generated PDFs, photos, deployment history, and internal documents.

```text
office request -> triage -> quote -> production sheet -> production -> delivery -> history
```

## What It Shows

- FastAPI backend with SQLAlchemy models, Alembic migrations, role-based access, and modular routers.
- Vue 3 + Vite frontend with role-aware navigation and production/order workspaces.
- CPQ-style quoting: simple and structured quote modes, material/labor calculations, margins, overhead, and transport.
- Production document generation through Jinja2 and WeasyPrint.
- Product/template catalog with operations, BOM-like materials, project grouping, and PDF output.
- Order workflow with triage, quote approval, production state changes, delivery, history, and analytics.
- Test coverage for backend API behavior, quote calculations, imports, uploads, workflow baseline, and settings validation.

## Demo Roles

| Role | Purpose |
| --- | --- |
| `biuro` | Creates requests, answers questions, approves quotes, tracks orders |
| `technolog` | Reviews non-standard requests, prepares quotes, manages catalog data |
| `ceo` | Reads analytics, schedules, benchmarks, and financial summaries |
| `dyrektor_produkcji` | Oversees production, catalog, materials, operations, and profitability |

Demo PINs are intentionally simple and only for local/demo use:

```text
biuro: 1111
technolog: 2222
ceo: 3333
dyrektor_produkcji: 4444
```

## Tech Stack

| Layer | Technology |
| --- | --- |
| Backend | FastAPI, SQLAlchemy |
| Database | SQLite locally, PostgreSQL via `DATABASE_URL` |
| Migrations | Alembic |
| Frontend | Vue 3 SFC, Vite |
| PDF | Jinja2, WeasyPrint, pypdf |
| Imports | openpyxl |
| Tests | pytest, FastAPI TestClient |
| Deploy shape | Render-compatible config |

## Project Structure

```text
backend/
  main.py                  FastAPI app, auth, startup, static frontend serving
  models.py                SQLAlchemy domain model
  schemas.py               Pydantic API schemas
  routers/                 Orders, quotes, catalog, templates, analytics, settings
  services/                Pricing, order workflow, templates, materials
  pdf_gen.py               PDF rendering helpers

frontend-vite/
  src/
    components/            App shell, login, order workspace, tabs, modals
    composables/           Shared Vue state and API wrappers
    utils/                 Formatting and workflow helpers

migrations/                Alembic revisions
templates/                 PDF templates
tests/                     Backend and workflow tests
```

## Local Start

Use Python 3.11.

```bash
python3.11 -m venv .venv
. .venv/bin/activate
pip install -r requirements.txt -r requirements-dev.txt

cd frontend-vite
npm ci
npm run build
cd ..

APP_ENV=dev python -m alembic upgrade head
cd backend
APP_ENV=dev uvicorn main:app --host 0.0.0.0 --port 8000 --reload
```

Open `http://localhost:8000`.

For frontend hot reload:

```bash
cd frontend-vite
npm run dev
```

## Tests

```bash
APP_ENV=test DISABLE_STARTUP_SEED=1 python -m pytest -q
```

Some smoke tests require a built frontend. Run `npm run build` in `frontend-vite/` first.

## Docker Compose

```bash
export JWT_SECRET=$(openssl rand -hex 32)
docker compose up -d
```

The container runs Alembic migrations on startup. Local SQLite data is persisted in `./data/factoryflow_demo.db`.

## Sanitization Notes

This repo intentionally excludes:

- real database files;
- generated production PDFs;
- uploaded drawings and attachments;
- company logos and photos;
- real customer/company names;
- real addresses, prices, and business documents;
- original git history;
- private deployment notes and operational docs.

If this project is used as a portfolio sample, treat it as a code and architecture demo, not as a live product clone.
