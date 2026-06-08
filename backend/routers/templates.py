"""
Product templates (SOP catalog), project arkusze, drawing upload/extract.
"""
import io
import pathlib
import time
from collections import defaultdict, deque
from typing import List, Optional

from fastapi import APIRouter, Depends, File, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse, Response
from pypdf import PdfWriter
from sqlalchemy import func
from sqlalchemy.orm import Session
from types import SimpleNamespace

from database import get_db
from drawing_extract import extract_drawing_pdf
from models import Order, ProductTemplate, TechCard
from pdf_gen import generate_arkusz_pdf
from auth import require_role
from schemas import TemplateCreate, TemplateOut, TemplatePatch
from services import template_service
from utils import save_upload_file_chunked

router = APIRouter()

_ALL  = require_role("biuro", "technolog", "ceo", "dyrektor_produkcji")
_TECH = require_role("technolog", "dyrektor_produkcji")

TEMPLATES_UPLOAD_ROOT = pathlib.Path(__file__).parent.parent / "uploads" / "templates"
TEMPLATES_UPLOAD_ROOT.mkdir(parents=True, exist_ok=True)

DRAWING_EXTRACT_LIMIT  = 10
DRAWING_EXTRACT_WINDOW = 60
_drawing_extract_hits: dict[str, deque[float]] = defaultdict(deque)


def _check_drawing_extract_rate_limit(request: Request) -> None:
    client = request.client.host if request.client else "unknown"
    now    = time.monotonic()
    hits   = _drawing_extract_hits[client]
    while hits and now - hits[0] > DRAWING_EXTRACT_WINDOW:
        hits.popleft()
    if len(hits) >= DRAWING_EXTRACT_LIMIT:
        raise HTTPException(status_code=429, detail="Za dużo prób czytania PDF. Odczekaj minutę.")
    hits.append(now)


def _template_drawing_abs_path(tmpl: ProductTemplate) -> pathlib.Path:
    if not tmpl.drawing_path:
        raise HTTPException(status_code=404, detail="Brak rysunku")
    abs_path = pathlib.Path(__file__).parent.parent / tmpl.drawing_path
    if not abs_path.exists():
        raise HTTPException(status_code=404, detail="Plik nie istnieje")
    if abs_path.suffix.lower() != ".pdf":
        raise HTTPException(status_code=400, detail="Tylko PDF")
    return abs_path


def _detach_template_references(template_id: int, db: Session) -> None:
    for order in db.query(Order).filter(Order.template_id == template_id).all():
        order.template_id = None
    for card in db.query(TechCard).filter(TechCard.template_id == template_id).all():
        card.template_id = None


# ── Template CRUD ─────────────────────────────────────────────────────────────

@router.get("/api/templates", response_model=List[TemplateOut])
def list_templates(
    category: Optional[str] = None,
    project_code: Optional[str] = None,
    db: Session = Depends(get_db),
    _: dict = _ALL,
):
    return template_service.list_templates(db, category=category, project_code=project_code)


@router.post("/api/templates", response_model=TemplateOut, status_code=201)
def create_template(payload: TemplateCreate, db: Session = Depends(get_db), _: dict = _TECH):
    return template_service.create_template(db, payload)


@router.patch("/api/templates/{template_id}", response_model=TemplateOut)
def patch_template(
    template_id: int,
    payload: TemplatePatch,
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    return template_service.patch_template(db, template_id, payload)


@router.delete("/api/templates/{template_id}", status_code=204)
def delete_template(template_id: int, db: Session = Depends(get_db), _: dict = _TECH):
    tmpl = db.get(ProductTemplate, template_id)
    if not tmpl:
        raise HTTPException(status_code=404, detail="Szablon nie znaleziony")
    _detach_template_references(template_id, db)
    db.delete(tmpl)
    db.commit()


@router.patch("/api/templates/{template_id}/restore", response_model=TemplateOut)
def restore_template(template_id: int, db: Session = Depends(get_db), _: dict = _TECH):
    return template_service.restore_template(db, template_id)


# ── Drawing upload / extract ──────────────────────────────────────────────────

@router.post("/api/templates/{template_id}/drawing", status_code=200)
async def upload_template_drawing(
    template_id: int,
    file: UploadFile = File(...),
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    tmpl = db.get(ProductTemplate, template_id)
    if not tmpl:
        raise HTTPException(status_code=404, detail="Szablon nie znaleziony")
    if file.content_type not in ("application/pdf", "application/octet-stream"):
        raise HTTPException(status_code=400, detail="Tylko PDF")

    dest_dir  = TEMPLATES_UPLOAD_ROOT / str(template_id)
    dest_dir.mkdir(exist_ok=True)
    safe_name = f"rysunek_{template_id}.pdf"
    dest_path = dest_dir / safe_name
    await save_upload_file_chunked(file, dest_path)

    tmpl.drawing_path = f"uploads/templates/{template_id}/{safe_name}"
    db.commit()
    return {"drawing_path": tmpl.drawing_path}


@router.get("/api/templates/{template_id}/drawing")
def get_template_drawing(template_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    tmpl = db.get(ProductTemplate, template_id)
    if not tmpl:
        raise HTTPException(status_code=404, detail="Szablon nie znaleziony")
    abs_path = _template_drawing_abs_path(tmpl)
    return FileResponse(
        str(abs_path),
        media_type="application/pdf",
        filename=f"rysunek_{template_id}.pdf",
        content_disposition_type="inline",
    )


@router.post("/api/templates/{template_id}/drawing/extract")
def extract_template_drawing(
    template_id: int,
    request: Request,
    apply: bool = False,
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    _check_drawing_extract_rate_limit(request)
    tmpl = db.get(ProductTemplate, template_id)
    if not tmpl:
        raise HTTPException(status_code=404, detail="Szablon nie znaleziony")
    abs_path = _template_drawing_abs_path(tmpl)
    try:
        extracted = extract_drawing_pdf(abs_path)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc))
    except Exception as exc:
        raise HTTPException(status_code=422, detail=f"Nie udało się odczytać PDF: {exc}")

    if apply:
        if extracted.get("name"):
            tmpl.name = extracted["name"]
        if extracted.get("materials_json"):
            tmpl.materials_json = extracted["materials_json"]
        if extracted.get("operations_json"):
            tmpl.operations_json = extracted["operations_json"]
        note_lines = [
            f"Odczytano z PDF: {abs_path.name}",
            f"Rysunek: {extracted.get('drawing_no') or '-'}",
            f"Masa: {extracted.get('mass_kg') or '-'} kg",
        ]
        tmpl.notes = "\n".join(note_lines)
        db.commit()
        db.refresh(tmpl)

    return {"template_id": template_id, "applied": apply, "drawing_path": tmpl.drawing_path, "extracted": extracted}


# ── Projects / arkusze ────────────────────────────────────────────────────────

@router.get("/api/projects")
def list_projects(db: Session = Depends(get_db), _: dict = _ALL):
    rows = (
        db.query(ProductTemplate.project_code, func.count(ProductTemplate.id))
        .filter(ProductTemplate.project_code.isnot(None), ProductTemplate.is_active.is_(True))
        .group_by(ProductTemplate.project_code)
        .all()
    )
    return [{"project_code": r[0], "positions_count": r[1]} for r in rows]


@router.get("/api/projects/{project_code}/arkusze")
def get_project_arkusze(project_code: str, db: Session = Depends(get_db), _: dict = _TECH):
    templates = (
        db.query(ProductTemplate)
        .filter(ProductTemplate.project_code == project_code, ProductTemplate.is_active.is_(True))
        .order_by(ProductTemplate.position_nr, ProductTemplate.id)
        .all()
    )
    if not templates:
        raise HTTPException(status_code=404, detail=f"Projekt '{project_code}' nie znaleziony lub brak pozycji")

    writer = PdfWriter()
    has_real_pdf = False

    for tmpl in templates:
        fake_order = SimpleNamespace(
            id=tmpl.id,
            order_number=f"{project_code}/{tmpl.position_nr or tmpl.id}",
            client=project_code,
            deadline=None,
            material=None,
            description=tmpl.name,
            sop_name=tmpl.position_nr,
            quantity=1,
            notes=None,
            requires_visit=False,
            order_type="catalog",
            attachments=[],
        )
        fake_quote = SimpleNamespace(
            processes_json=tmpl.operations_json or [],
            material_weight_kg=None,
            weight_netto_kg=None,
            weight_brutto_kg=None,
        )
        arkusz_bytes = generate_arkusz_pdf(fake_order, tmpl, fake_quote)
        if arkusz_bytes[:4] == b"%PDF":
            writer.append(io.BytesIO(arkusz_bytes))
            has_real_pdf = True

    if not has_real_pdf:
        raise HTTPException(status_code=503, detail="WeasyPrint niedostępny — brak PDF")

    out = io.BytesIO()
    writer.write(out)
    return Response(
        content=out.getvalue(),
        media_type="application/pdf",
        headers={"Content-Disposition": f'attachment; filename="{project_code}_arkusze.pdf"'},
    )


@router.get("/api/templates/{template_id}/arkusz")
def get_template_arkusz(template_id: int, db: Session = Depends(get_db), _: dict = _TECH):
    tmpl = db.get(ProductTemplate, template_id)
    if not tmpl:
        raise HTTPException(status_code=404, detail="Szablon nie znaleziony")

    fake_order = SimpleNamespace(
        id=tmpl.id,
        order_number=tmpl.position_nr or str(tmpl.id),
        client=tmpl.project_code or "",
        deadline=None,
        material=None,
        description=tmpl.name,
        sop_name=tmpl.position_nr,
        quantity=1,
        notes=tmpl.notes,
        requires_visit=False,
        order_type="catalog",
        attachments=[],
    )
    fake_quote = SimpleNamespace(
        processes_json=tmpl.operations_json or [],
        material_weight_kg=None,
        weight_netto_kg=None,
        weight_brutto_kg=None,
    )

    arkusz_bytes = generate_arkusz_pdf(fake_order, tmpl, fake_quote)
    if arkusz_bytes[:4] != b"%PDF":
        raise HTTPException(status_code=503, detail="WeasyPrint niedostępny")
    fname = f"{tmpl.position_nr or tmpl.id}_arkusz.pdf"
    return Response(
        content=arkusz_bytes,
        media_type="application/pdf",
        headers={"Content-Disposition": f'inline; filename="{fname}"'},
    )
