"""
PDF and document generation endpoints.
"""
import math

from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import Response
from sqlalchemy.orm import Session

from auth import require_role
from database import get_db
from models import Order, Quote, ProductTemplate, Setting
from pdf_gen import (
    DocumentGenerationError,
    _safe_filename_part,
    generate_arkusz_pdf,
    generate_arkusze_by_operation_zip,
    generate_oferta_pdf,
    get_content_type,
)

router = APIRouter()

_ALL  = require_role("biuro", "technolog", "ceo")
_TECH = require_role("technolog")


def _get_order_context(db: Session, order_id: int) -> tuple[Order, ProductTemplate | None, Quote | None]:
    order = db.get(Order, order_id)
    if not order:
        raise HTTPException(status_code=404, detail="Zlecenie nie znalezione")
    template = db.get(ProductTemplate, order.template_id) if order.template_id else None
    quote    = db.query(Quote).filter(Quote.order_id == order_id).first()
    return order, template, quote


def _read_setting(db: Session, key: str, default: str) -> str:
    row = db.get(Setting, key)
    return row.value if row and row.value else default


def _get_company_context(db: Session) -> dict:
    return {
        "name":    _read_setting(db, "company_name",    "DemoFab"),
        "address": _read_setting(db, "company_address", "ul. Przykładowa 10, 00-001 Warszawa"),
        "nip":     _read_setting(db, "company_nip",     "000-000-00-00"),
        "regon":   _read_setting(db, "company_regon",   "000000000"),
        "tagline": _read_setting(db, "company_tagline", "Manufacturing workflow demo"),
    }


def _get_vat_rate(db: Session) -> float:
    try:
        vat_rate = float(_read_setting(db, "vat_rate", "0.23"))
    except (TypeError, ValueError):
        raise HTTPException(status_code=422, detail="Nieprawidłowa stawka VAT")
    if not math.isfinite(vat_rate) or not 0 <= vat_rate <= 1:
        raise HTTPException(status_code=422, detail="Nieprawidłowa stawka VAT")
    return vat_rate


def _safe_filename(order: Order) -> str:
    return _safe_filename_part(order.order_number or order.id, str(order.id))


@router.get("/api/orders/{order_id}/pdf")
def get_order_pdf(order_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    order, template, quote = _get_order_context(db, order_id)
    company = _get_company_context(db)
    try:
        pdf_bytes    = generate_arkusz_pdf(order, template, quote, company=company)
        content_type = get_content_type(order)
    except (OSError, DocumentGenerationError) as e:
        raise HTTPException(status_code=422, detail=f"Błąd generowania PDF: {e}")
    ext = "pdf" if "pdf" in content_type else "html"
    return Response(
        content=pdf_bytes,
        media_type=content_type,
        headers={"Content-Disposition": f"attachment; filename=Arkusz_{_safe_filename(order)}.{ext}"},
    )


@router.get("/api/orders/{order_id}/pdf/split")
def get_order_operation_pdfs(order_id: int, db: Session = Depends(get_db), _: dict = _TECH):
    order, template, quote = _get_order_context(db, order_id)
    company = _get_company_context(db)
    try:
        zip_bytes = generate_arkusze_by_operation_zip(order, template, quote, company=company)
    except (OSError, DocumentGenerationError) as e:
        raise HTTPException(status_code=422, detail=f"Błąd generowania arkuszy operacji: {e}")
    return Response(
        content=zip_bytes,
        media_type="application/zip",
        headers={"Content-Disposition": f"attachment; filename=Arkusze_operacji_{_safe_filename(order)}.zip"},
    )


@router.get("/api/orders/{order_id}/oferta")
def get_order_oferta(order_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    order, template, quote = _get_order_context(db, order_id)
    company = _get_company_context(db)
    vat_rate = _get_vat_rate(db)
    try:
        pdf_bytes    = generate_oferta_pdf(order, template, quote, company=company, vat_rate=vat_rate)
        content_type = get_content_type(order)
    except (OSError, DocumentGenerationError) as e:
        raise HTTPException(status_code=422, detail=f"Błąd generowania Oferty: {e}")
    ext = "pdf" if "pdf" in content_type else "html"
    return Response(
        content=pdf_bytes,
        media_type=content_type,
        headers={"Content-Disposition": f"attachment; filename=Oferta_{_safe_filename(order)}.{ext}"},
    )
