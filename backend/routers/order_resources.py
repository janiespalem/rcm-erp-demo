"""
Order sub-resources: attachments, material requests, quality cards, parameter requests.
"""
import pathlib
import uuid
from typing import List, Optional

from fastapi import APIRouter, Depends, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, Response
from sqlalchemy.orm import Session

from auth import require_role
from database import get_db
from models import (
    OrderAttachment, MaterialRequest, QualityCard, ParameterRequest,
)
from schemas import (
    AttachmentOut,
    MaterialRequestCreate, MaterialRequestOut,
    QualityCardCreate, QualityCardOut,
    ParameterRequestCreate, ParameterRequestAnswer, ParameterRequestOut,
)
from services import order_service
from utils import _now, UPLOAD_ROOT, safe_upload_filename, save_upload_file_chunked

router = APIRouter()

_ALL   = require_role("biuro", "technolog", "ceo", "dyrektor_produkcji")
_TECH  = require_role("technolog", "dyrektor_produkcji")
_BIURO = require_role("biuro", "dyrektor_produkcji")

_BLOCKED_EXTENSIONS = {".html", ".htm", ".svg", ".js"}


def _check_extension(filename: str) -> None:
    ext = pathlib.Path(filename).suffix.lower()
    if ext in _BLOCKED_EXTENSIONS:
        raise HTTPException(status_code=415, detail=f"Niedozwolony typ pliku: '{ext}'")


# ── Attachments ───────────────────────────────────────────────────────────────

@router.post("/api/orders/{order_id}/attachments", response_model=AttachmentOut, status_code=201)
async def upload_attachment(
    order_id: int,
    file: UploadFile = File(...),
    db: Session = Depends(get_db),
    current_user: dict = _ALL,
):
    order_service.get_order_or_404(db, order_id)
    _check_extension(file.filename)

    order_dir = UPLOAD_ROOT / str(order_id)
    order_dir.mkdir(exist_ok=True)
    safe_name  = f"{uuid.uuid4().hex[:8]}_{safe_upload_filename(file.filename)}"
    dest       = order_dir / safe_name
    size_bytes = await save_upload_file_chunked(file, dest)

    att = OrderAttachment(
        order_id    = order_id,
        filename    = safe_upload_filename(file.filename),
        stored_path = f"uploads/{order_id}/{safe_name}",
        size_bytes  = size_bytes,
        mime_type   = file.content_type,
        uploaded_by = current_user["role"],
    )
    db.add(att)
    db.commit()
    db.refresh(att)
    return att


@router.get("/api/orders/{order_id}/attachments", response_model=List[AttachmentOut])
def list_attachments(order_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    order_service.get_order_or_404(db, order_id)
    return db.query(OrderAttachment).filter(OrderAttachment.order_id == order_id).all()


@router.get("/api/attachments/{att_id}/download")
def download_attachment(att_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    att = db.get(OrderAttachment, att_id)
    if not att:
        raise HTTPException(status_code=404, detail="Załącznik nie znaleziony")
    file_path = pathlib.Path(__file__).parent.parent / att.stored_path
    # Guard against path traversal
    try:
        file_path.resolve().relative_to((pathlib.Path(__file__).parent.parent / "uploads").resolve())
    except ValueError:
        raise HTTPException(status_code=403, detail="Niedozwolona ścieżka")
    if not file_path.exists():
        raise HTTPException(status_code=404, detail="Plik nie istnieje")
    return FileResponse(
        path=str(file_path),
        filename=att.filename,
        media_type=att.mime_type or "application/octet-stream",
    )


@router.delete("/api/attachments/{att_id}", status_code=204)
def delete_attachment(att_id: int, db: Session = Depends(get_db), _: dict = _TECH):
    att = db.get(OrderAttachment, att_id)
    if not att:
        raise HTTPException(status_code=404, detail="Załącznik nie znaleziony")
    (pathlib.Path(__file__).parent.parent / att.stored_path).unlink(missing_ok=True)
    db.delete(att)
    db.commit()
    return Response(status_code=204)


# ── Material requests ─────────────────────────────────────────────────────────

@router.post("/api/orders/{order_id}/materials", response_model=MaterialRequestOut, status_code=201)
def create_material_request(
    order_id: int,
    payload: MaterialRequestCreate,
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    order_service.get_order_or_404(db, order_id)
    req = MaterialRequest(
        order_id    = order_id,
        client      = payload.client,
        materials   = payload.materials,
        extra_notes = payload.extra_notes,
        priority    = payload.priority,
        status      = "pending",
    )
    db.add(req)
    db.commit()
    db.refresh(req)
    return req


@router.get("/api/orders/{order_id}/materials", response_model=MaterialRequestOut)
def get_material_request(order_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    order_service.get_order_or_404(db, order_id)
    req = db.query(MaterialRequest).filter(MaterialRequest.order_id == order_id).first()
    if not req:
        raise HTTPException(status_code=404, detail="Brak zapotrzebowania materiałowego")
    return req


# ── Quality cards ─────────────────────────────────────────────────────────────

@router.post("/api/orders/{order_id}/quality", response_model=QualityCardOut, status_code=201)
def add_quality_card(
    order_id: int,
    payload: QualityCardCreate,
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    order_service.get_order_or_404(db, order_id)
    card = QualityCard(
        order_id        = order_id,
        operation_id    = payload.operation_id,
        stage_name      = payload.stage_name,
        check_linear    = payload.check_linear,
        check_geometric = payload.check_geometric,
        check_surface   = payload.check_surface,
        passed          = payload.passed,
        checked_by      = payload.checked_by,
        checked_at      = _now(),
    )
    db.add(card)
    db.commit()
    db.refresh(card)
    return card


@router.get("/api/orders/{order_id}/quality", response_model=List[QualityCardOut])
def get_quality_cards(order_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    order_service.get_order_or_404(db, order_id)
    return db.query(QualityCard).filter(QualityCard.order_id == order_id).all()


# ── Parameter requests ────────────────────────────────────────────────────────

@router.post("/api/orders/{order_id}/params", response_model=ParameterRequestOut, status_code=201)
def ask_for_params(
    order_id: int,
    payload: ParameterRequestCreate,
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    order_service.get_order_or_404(db, order_id)
    req = ParameterRequest(
        order_id      = order_id,
        question_text = payload.question_text,
        status        = "pending",
    )
    db.add(req)
    db.commit()
    db.refresh(req)
    return req


@router.patch("/api/params/{param_id}/answer", response_model=ParameterRequestOut)
def answer_param_request(
    param_id: int,
    payload: ParameterRequestAnswer,
    db: Session = Depends(get_db),
    _: dict = _BIURO,
):
    req = db.get(ParameterRequest, param_id)
    if not req:
        raise HTTPException(status_code=404, detail="Pytanie nie znalezione")
    req.answer_text = payload.answer_text
    req.status      = "answered"
    req.answered_at = _now()
    db.commit()
    db.refresh(req)
    return req


@router.get("/api/orders/{order_id}/params", response_model=List[ParameterRequestOut])
def get_param_requests(order_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    order_service.get_order_or_404(db, order_id)
    return db.query(ParameterRequest).filter(ParameterRequest.order_id == order_id).all()


@router.get("/api/params", response_model=List[ParameterRequestOut])
def get_all_param_requests(
    status: Optional[str] = None,
    db: Session = Depends(get_db),
    _: dict = _ALL,
):
    q = db.query(ParameterRequest)
    if status:
        q = q.filter(ParameterRequest.status == status)
    return q.order_by(ParameterRequest.asked_at.desc()).all()
