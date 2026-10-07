"""
Order sub-resources: attachments, material requests, quality cards, parameter requests.
"""
import os
import pathlib
import re
import uuid
import zipfile
from typing import List, Optional

from fastapi import APIRouter, Depends, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, Response
from sqlalchemy.orm import Session

from auth import require_role
from database import get_db
from models import (
    OrderAttachment, ParameterRequest,
)
from schemas import (
    AttachmentOut,
    ParameterRequestCreate, ParameterRequestAnswer, ParameterRequestOut,
)
from services import order_service
from services.order_service import _log_event
from utils import _now, UPLOAD_ROOT, safe_upload_filename, save_upload_file_chunked

router = APIRouter()

_ALL   = require_role("biuro", "technolog", "ceo")
_TECH  = require_role("technolog")
_BIURO = require_role("biuro", "technolog")

_ATTACHMENT_MIME = {
    ".pdf": "application/pdf",
    ".dxf": "image/vnd.dxf",
    ".dwg": "image/vnd.dwg",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".png": "image/png",
    ".xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
}


def _expected_attachment_mime(filename: str | None) -> tuple[str, str]:
    extension = pathlib.Path(filename or "").suffix.lower()
    mime_type = _ATTACHMENT_MIME.get(extension)
    if mime_type is None:
        raise HTTPException(status_code=415, detail=f"Niedozwolony typ pliku: '{extension}'")
    return extension, mime_type


def _verified_attachment_mime(path: pathlib.Path, filename: str | None) -> str:
    extension, mime_type = _expected_attachment_mime(filename)
    with path.open("rb") as source:
        head = source.read(1024)
    is_valid = {
        ".pdf": head.startswith(b"%PDF-"),
        ".png": head.startswith(b"\x89PNG\r\n\x1a\n"),
        ".jpg": head.startswith(b"\xff\xd8\xff"),
        ".jpeg": head.startswith(b"\xff\xd8\xff"),
        ".dwg": re.fullmatch(rb"AC10\d{2}", head[:6]) is not None,
        ".dxf": (
            head.startswith(b"AutoCAD Binary DXF\r\n\x1a\x00")
            or head.lstrip().replace(b"\r\n", b"\n").startswith(b"0\nSECTION")
        ),
    }.get(extension)

    if extension in {".docx", ".xlsx"}:
        required_member = "word/document.xml" if extension == ".docx" else "xl/workbook.xml"
        try:
            with zipfile.ZipFile(path) as archive:
                archive.getinfo("[Content_Types].xml")
                archive.getinfo(required_member)
            is_valid = True
        except (KeyError, OSError, zipfile.BadZipFile):
            is_valid = False

    if not is_valid:
        raise HTTPException(status_code=415, detail="Zawartość pliku nie pasuje do rozszerzenia")
    return mime_type


def _attachment_path(stored_path: str) -> pathlib.Path:
    parts = pathlib.PurePosixPath(stored_path).parts
    if not parts or parts[0] != "uploads":
        raise HTTPException(status_code=403, detail="Niedozwolona ścieżka")
    path = UPLOAD_ROOT.joinpath(*parts[1:]).resolve()
    try:
        path.relative_to(UPLOAD_ROOT.resolve())
    except ValueError:
        raise HTTPException(status_code=403, detail="Niedozwolona ścieżka")
    return path


# ── Attachments ───────────────────────────────────────────────────────────────

@router.post("/api/orders/{order_id}/attachments", response_model=AttachmentOut, status_code=201)
async def upload_attachment(
    order_id: int,
    file: UploadFile = File(...),
    db: Session = Depends(get_db),
    current_user: dict = _ALL,
):
    order = order_service.get_mutable_order_or_404(db, order_id)
    original_name = safe_upload_filename(file.filename)
    _expected_attachment_mime(original_name)

    order_dir = UPLOAD_ROOT / str(order_id)
    order_dir.mkdir(exist_ok=True)
    safe_name  = f"{uuid.uuid4().hex[:8]}_{original_name}"
    dest       = order_dir / safe_name
    size_bytes = await save_upload_file_chunked(file, dest)
    try:
        mime_type = _verified_attachment_mime(dest, original_name)
    except Exception:
        dest.unlink(missing_ok=True)
        raise

    att = OrderAttachment(
        order_id    = order_id,
        filename    = original_name,
        stored_path = f"uploads/{order_id}/{safe_name}",
        size_bytes  = size_bytes,
        mime_type   = mime_type,
        uploaded_by = current_user["role"],
    )
    db.add(att)
    _log_event(db, order, "file_added", user=current_user, note=original_name)
    try:
        db.commit()
    except Exception:
        db.rollback()
        dest.unlink(missing_ok=True)
        raise
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
    file_path = _attachment_path(att.stored_path)
    if not file_path.exists():
        raise HTTPException(status_code=404, detail="Plik nie istnieje")
    mime_type = _verified_attachment_mime(file_path, att.filename)
    return FileResponse(
        path=str(file_path),
        filename=att.filename,
        media_type=mime_type,
        content_disposition_type="attachment",
        headers={
            "X-Content-Type-Options": "nosniff",
            "Content-Security-Policy": "sandbox",
        },
    )


@router.delete("/api/attachments/{att_id}", status_code=204)
def delete_attachment(att_id: int, db: Session = Depends(get_db), current_user: dict = _TECH):
    att = db.get(OrderAttachment, att_id)
    if not att:
        raise HTTPException(status_code=404, detail="Załącznik nie znaleziony")
    order = order_service.get_mutable_order_or_404(db, att.order_id)
    filename = att.filename
    path = _attachment_path(att.stored_path)
    pending_delete = path.with_name(f".{path.name}.deleting")
    if path.exists():
        os.replace(path, pending_delete)
    db.delete(att)
    _log_event(db, order, "file_removed", user=current_user, note=filename)
    try:
        db.commit()
    except Exception:
        db.rollback()
        if pending_delete.exists():
            os.replace(pending_delete, path)
        raise
    pending_delete.unlink(missing_ok=True)
    return Response(status_code=204)


# ── Parameter requests ────────────────────────────────────────────────────────

@router.post("/api/orders/{order_id}/params", response_model=ParameterRequestOut, status_code=201)
def ask_for_params(
    order_id: int,
    payload: ParameterRequestCreate,
    db: Session = Depends(get_db),
    current_user: dict = _TECH,
):
    order = order_service.get_mutable_order_or_404(db, order_id)
    req = ParameterRequest(
        order_id      = order_id,
        question_text = payload.question_text,
        status        = "pending",
    )
    db.add(req)
    _log_event(db, order, "question_asked", user=current_user, note=payload.question_text)
    db.commit()
    db.refresh(req)
    return req


@router.patch("/api/params/{param_id}/answer", response_model=ParameterRequestOut)
def answer_param_request(
    param_id: int,
    payload: ParameterRequestAnswer,
    db: Session = Depends(get_db),
    current_user: dict = _BIURO,
):
    req = (
        db.query(ParameterRequest)
        .filter(ParameterRequest.id == param_id)
        .with_for_update()
        .first()
    )
    if not req:
        raise HTTPException(status_code=404, detail="Pytanie nie znalezione")
    if req.status != "pending":
        raise HTTPException(status_code=409, detail="Pytanie ma już odpowiedź")
    order = order_service.get_mutable_order_or_404(db, req.order_id)
    req.answer_text = payload.answer_text
    req.status      = "answered"
    req.answered_at = _now()
    _log_event(db, order, "question_answered", user=current_user, note=payload.answer_text)
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
