"""
Quotes, Triage, and Save-as-Template endpoints.
"""
from datetime import date
from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.orm import Session

from auth import require_role
from database import get_db
from models import Order, Quote, Setting, OrderStatus, ProductTemplate
from schemas import (
    TriageResponse,
    QuoteCreate, QuoteZaporCreate, QuoteOut,
    QuoteStructuredCreate, ManualQuoteCreate,
    SaveAsTemplatePayload, TemplateOut,
    QuotePreviewOut,
)
from services import order_service
from services.order_service import _log_event
from services.pricing_service import calc_simple_quote, calc_structured_quote, calc_zapor_quote
from triage import run_triage, TriageInput
from utils import DEFAULT_MARGIN_PCT
from utils import _now, DEFAULT_LABOR_RATE_PLN

router = APIRouter()

_ALL  = require_role("biuro", "technolog", "ceo", "dyrektor_produkcji")
_TECH = require_role("technolog", "dyrektor_produkcji")
_TRIAGE = require_role("biuro", "technolog", "dyrektor_produkcji")


@router.post("/api/orders/{order_id}/triage", response_model=TriageResponse)
def triage_order(
    order_id: int,
    db: Session = Depends(get_db),
    current_user: dict = _TRIAGE,
):
    order = db.get(Order, order_id)
    if not order:
        raise HTTPException(status_code=404, detail="Zlecenie nie znalezione")

    triage_input = TriageInput(
        client          = order.client,
        material        = order.material or "",
        deadline_days   = (order.deadline - date.today()).days if order.deadline else 999,
        has_drawing     = order.has_drawing,
        order_type      = order.order_type or "remont",
        sop_name        = order.sop_name,
        template_id     = order.template_id,
        estimated_value = float(order.estimated_value or 0),
    )

    result = run_triage(triage_input, db)

    old_status = order.status.value if order.status else None
    order.triage_branch = result.branch
    new_status_val = {
        "odrzut":      OrderStatus.rejected,
        "standard":    OrderStatus.standard,
        "niestandard": OrderStatus.niestandard,
    }[result.branch]
    order.status = new_status_val
    if result.template_id and not order.template_id:
        order.template_id = result.template_id
    _log_event(db, order, "triage", old_status=old_status, new_status=result.branch, user=current_user, note=result.message)
    db.commit()

    return TriageResponse(
        branch      = result.branch,
        message     = result.message,
        template_id = result.template_id,
        rule_name   = result.rule_name,
        warnings    = result.warnings or [],
    )


@router.post("/api/orders/{order_id}/quote", response_model=QuoteOut, status_code=201)
def create_quote(
    order_id: int,
    payload: QuoteCreate,
    db: Session = Depends(get_db),
    current_user: dict = _TECH,
):
    order = order_service.get_order_or_404(db, order_id)

    rate_setting = db.get(Setting, "labor_rate_pln")
    labor_rate   = float(rate_setting.value) if rate_setting else DEFAULT_LABOR_RATE_PLN
    result       = calc_simple_quote(
        labor_hours   = payload.labor_hours,
        material_cost = payload.material_cost,
        overhead_pct  = payload.overhead_pct,
        margin_pct    = payload.margin_pct,
        labor_rate    = labor_rate,
    )

    quote = db.query(Quote).filter(Quote.order_id == order_id).first()
    if not quote:
        quote = Quote(order_id=order_id)
        db.add(quote)

    quote.line_items       = payload.line_items
    quote.labor_hours      = payload.labor_hours
    quote.material_cost    = payload.material_cost
    quote.overhead_pct     = payload.overhead_pct
    quote.margin_pct       = payload.margin_pct
    quote.total_net        = result.total_net
    quote.is_zapor         = False
    quote.estimate_version = "v1"
    quote.last_edited_at   = _now()
    if order.status == OrderStatus.niestandard:
        _log_event(db, order, "quoted", old_status="niestandard", new_status="quoted", user=current_user)
        order.status = OrderStatus.quoted
        if not order.quoted_at:
            order.quoted_at = _now()
    db.commit()
    db.refresh(quote)
    return quote


@router.post("/api/orders/{order_id}/quote/zapor", response_model=QuoteOut, status_code=201)
def create_zapor_quote(
    order_id: int,
    payload: QuoteZaporCreate,
    db: Session = Depends(get_db),
    current_user: dict = _TECH,
):
    order = order_service.get_order_or_404(db, order_id)

    result = calc_zapor_quote(
        material_cost    = payload.material_cost,
        hours_estimate   = payload.hours_estimate,
        zapor_multiplier = payload.zapor_multiplier,
    )

    quote = db.query(Quote).filter(Quote.order_id == order_id).first()
    if not quote:
        quote = Quote(order_id=order_id)
        db.add(quote)

    quote.line_items         = []
    quote.labor_hours        = payload.hours_estimate
    quote.material_cost      = payload.material_cost
    quote.overhead_pct       = 0.0
    quote.margin_pct         = result.margin_pct
    quote.total_net          = result.total_net
    quote.zapor_multiplier   = result.multiplier
    quote.is_zapor           = True
    quote.estimate_version   = "zapor"
    quote.last_edited_at   = _now()
    if order.status == OrderStatus.niestandard:
        _log_event(db, order, "quoted", old_status="niestandard", new_status="quoted", user=current_user)
        order.status = OrderStatus.quoted
        if not order.quoted_at:
            order.quoted_at = _now()
    db.commit()
    db.refresh(quote)
    return quote


@router.get("/api/orders/{order_id}/quote", response_model=QuoteOut)
def get_quote(
    order_id: int,
    db: Session = Depends(get_db),
    _: dict = _ALL,
):
    quote = db.query(Quote).filter(Quote.order_id == order_id).first()
    if not quote:
        raise HTTPException(status_code=404, detail="Brak wyceny dla tego zlecenia")
    return quote


@router.post("/api/orders/{order_id}/quote/manual", response_model=QuoteOut, status_code=201)
def create_manual_quote(
    order_id: int,
    payload: ManualQuoteCreate,
    db: Session = Depends(get_db),
    current_user: dict = _TECH,
):
    order = order_service.get_order_or_404(db, order_id)
    editable = {OrderStatus.niestandard, OrderStatus.quoted, OrderStatus.in_production}
    if order.status not in editable:
        raise HTTPException(status_code=409, detail="Nie można edytować wyceny dla tego statusu")

    quote = db.query(Quote).filter(Quote.order_id == order_id).first()
    if not quote:
        quote = Quote(order_id=order_id)
        db.add(quote)

    quote.total_net        = round(payload.total_net, 2)
    quote.labor_hours      = 0
    quote.material_cost    = 0
    quote.overhead_pct     = 0
    quote.margin_pct       = 0
    quote.estimate_version = "manual"
    quote.last_edited_at   = _now()

    if order.status == OrderStatus.niestandard:
        _log_event(db, order, "quoted", old_status="niestandard", new_status="quoted", user=current_user)
        order.status = OrderStatus.quoted
        if not order.quoted_at:
            order.quoted_at = _now()

    db.commit()
    db.refresh(quote)
    return quote


@router.post("/api/orders/{order_id}/quote/structured", response_model=QuoteOut, status_code=201)
def create_structured_quote(
    order_id: int,
    payload: QuoteStructuredCreate,
    db: Session = Depends(get_db),
    current_user: dict = _TECH,
):
    order = db.get(Order, order_id)
    if not order:
        raise HTTPException(status_code=404, detail="Zlecenie nie znalezione")

    editable_statuses = {OrderStatus.niestandard, OrderStatus.quoted, OrderStatus.in_production}
    if order.status not in editable_statuses:
        raise HTTPException(status_code=409, detail=f"Wycena niedozwolona w statusie '{order.status.value}'")

    rate_setting = db.get(Setting, "labor_rate_pln")
    labor_rate   = float(rate_setting.value) if rate_setting else DEFAULT_LABOR_RATE_PLN

    result = calc_structured_quote(
        processes             = payload.processes,
        material_cost         = payload.material_cost,
        material_weight_kg    = payload.material_weight_kg,
        material_price_per_kg = payload.material_price_per_kg,
        labor_hours           = payload.labor_hours,
        overhead_pct          = payload.overhead_pct,
        margin_pct            = payload.margin_pct,
        transport_cost        = payload.transport_cost or 0,
        labor_rate            = labor_rate,
        materials             = payload.materials,
    )
    total_net = result.total_net

    quote = db.query(Quote).filter(Quote.order_id == order_id).first()
    if not quote:
        quote = Quote(order_id=order_id)
        db.add(quote)

    quote.line_items            = []
    quote.processes_json        = [
        {
            **p.model_dump(),
            "wydział": p.department or p.name,
            "op": p.name,
            "cost": p.hours * p.rate_per_hour if (p.hours and p.rate_per_hour) else (p.cost or 0),
        }
        for p in payload.processes
    ]
    quote.materials_json        = [m.model_dump() for m in payload.materials]
    quote.material_cost         = result.material_total
    quote.material_weight_kg    = payload.material_weight_kg
    quote.material_price_per_kg = payload.material_price_per_kg
    quote.weight_netto_kg       = payload.weight_netto_kg
    quote.weight_brutto_kg      = payload.weight_brutto_kg
    quote.labor_hours           = payload.labor_hours
    quote.overhead_pct          = payload.overhead_pct
    quote.margin_pct            = payload.margin_pct
    quote.transport_cost        = payload.transport_cost or 0
    quote.show_unit_prices      = payload.show_unit_prices
    quote.total_net             = result.total_net
    quote.is_zapor              = False
    quote.estimate_version      = "v3"
    quote.last_edited_at        = _now()

    if order.status == OrderStatus.niestandard:
        _log_event(db, order, "quoted", old_status="niestandard", new_status="quoted", user=current_user)
        order.status = OrderStatus.quoted
        if not order.quoted_at:
            order.quoted_at = _now()

    db.commit()
    db.refresh(quote)
    return quote


@router.post("/api/quotes/preview", response_model=QuotePreviewOut)
def preview_structured_quote(
    payload: QuoteStructuredCreate,
    db: Session = Depends(get_db),
    _: dict = _ALL,
):
    """Stateless pricing preview — runs calc_structured_quote, returns breakdown, writes nothing."""
    rate_setting = db.get(Setting, "labor_rate_pln")
    labor_rate   = float(rate_setting.value) if rate_setting else DEFAULT_LABOR_RATE_PLN

    result = calc_structured_quote(
        processes             = payload.processes,
        material_cost         = payload.material_cost,
        material_weight_kg    = payload.material_weight_kg,
        material_price_per_kg = payload.material_price_per_kg,
        labor_hours           = payload.labor_hours,
        overhead_pct          = payload.overhead_pct,
        margin_pct            = payload.margin_pct,
        transport_cost        = payload.transport_cost or 0,
        labor_rate            = labor_rate,
        materials             = payload.materials,
    )
    return QuotePreviewOut(
        ops_total      = result.ops_total,
        material_total = result.material_total,
        extra_labor    = result.extra_labor,
        base           = result.base,
        subtotal       = result.subtotal,
        total_net      = result.total_net,
    )


@router.post("/api/orders/{order_id}/save-as-template", response_model=TemplateOut, status_code=201)
def save_order_as_template(
    order_id: int,
    payload: SaveAsTemplatePayload,
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    order = order_service.get_order_or_404(db, order_id)
    quote = db.query(Quote).filter(Quote.order_id == order_id).first()

    ops = []
    if quote and quote.processes_json:
        ops = [{"op": p["name"], "hours": 0, "cost": p["cost"]} for p in quote.processes_json]

    mats = []
    weight = float(quote.material_weight_kg or quote.weight_kg or 0) if quote else 0
    if weight > 0:
        mats = [{"mat": order.material or "Materiał", "qty_kg": weight, "unit": "kg"}]

    tmpl = ProductTemplate(
        name               = payload.name or f"Z zlecenia {order.order_number}",
        category           = payload.category or order.order_type or "remont",
        operations_json    = ops,
        materials_json     = mats,
        instruction_blocks = [],
        machines_json      = [],
        base_price_pln     = float(quote.total_net) if quote and quote.total_net else None,
        margin_pct         = float(quote.margin_pct or DEFAULT_MARGIN_PCT) if quote else DEFAULT_MARGIN_PCT,
    )
    db.add(tmpl)
    db.commit()
    db.refresh(tmpl)
    return tmpl
