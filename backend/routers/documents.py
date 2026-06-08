"""
PDF and document generation endpoints.
"""
from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import Response
from sqlalchemy.orm import Session

from auth import require_role
from database import get_db
from models import Order, Quote, ProductTemplate
from pdf_gen import generate_arkusz_pdf, generate_arkusze_by_operation_zip, generate_oferta_pdf, get_content_type

router = APIRouter()

_ALL  = require_role("biuro", "technolog", "ceo", "dyrektor_produkcji")
_TECH = require_role("technolog", "dyrektor_produkcji")


def _get_order_context(db: Session, order_id: int) -> tuple[Order, ProductTemplate | None, Quote | None]:
    order = db.get(Order, order_id)
    if not order:
        raise HTTPException(status_code=404, detail="Zlecenie nie znalezione")
    template = db.get(ProductTemplate, order.template_id) if order.template_id else None
    quote    = db.query(Quote).filter(Quote.order_id == order_id).first()
    return order, template, quote


def _safe_filename(order: Order) -> str:
    return (order.order_number or str(order.id)).replace("/", "-")


@router.get("/api/orders/{order_id}/pdf")
def get_order_pdf(order_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    order, template, quote = _get_order_context(db, order_id)
    try:
        pdf_bytes    = generate_arkusz_pdf(order, template, quote)
        content_type = get_content_type(order)
    except OSError as e:
        raise HTTPException(status_code=500, detail=f"Błąd generowania PDF: {e}")
    ext = "pdf" if "pdf" in content_type else "html"
    return Response(
        content=pdf_bytes,
        media_type=content_type,
        headers={"Content-Disposition": f"attachment; filename=Arkusz_{_safe_filename(order)}.{ext}"},
    )


@router.get("/api/orders/{order_id}/pdf/split")
def get_order_operation_pdfs(order_id: int, db: Session = Depends(get_db), _: dict = _TECH):
    order, template, quote = _get_order_context(db, order_id)
    try:
        zip_bytes = generate_arkusze_by_operation_zip(order, template, quote)
    except OSError as e:
        raise HTTPException(status_code=500, detail=f"Błąd generowania arkuszy operacji: {e}")
    return Response(
        content=zip_bytes,
        media_type="application/zip",
        headers={"Content-Disposition": f"attachment; filename=Arkusze_operacji_{_safe_filename(order)}.zip"},
    )


@router.get("/api/orders/{order_id}/oferta")
def get_order_oferta(order_id: int, db: Session = Depends(get_db), _: dict = _ALL):
    order, template, quote = _get_order_context(db, order_id)
    try:
        pdf_bytes    = generate_oferta_pdf(order, template, quote)
        content_type = get_content_type(order)
    except OSError as e:
        raise HTTPException(status_code=500, detail=f"Błąd generowania Oferty: {e}")
    ext = "pdf" if "pdf" in content_type else "html"
    return Response(
        content=pdf_bytes,
        media_type=content_type,
        headers={"Content-Disposition": f"attachment; filename=Oferta_{_safe_filename(order)}.{ext}"},
    )
