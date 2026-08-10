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
    QuoteOut,
    QuoteStructuredCreate, ManualQuoteCreate,
    SaveAsTemplatePayload, TemplateOut,
    QuotePreviewOut,
)
from services import order_service
from services.order_service import _log_event
from services.pricing_service import calc_structured_quote, process_total
from triage import run_triage, TriageInput
from utils import DEFAULT_MARGIN_PCT
from utils import _now, DEFAULT_LABOR_RATE_PLN

router = APIRouter()

_ALL  = require_role("biuro", "technolog", "ceo")
_TECH = require_role("technolog")
_TRIAGE = require_role("biuro", "technolog")

# Statuses a saved quote moves forward to 'quoted'. 'standard' is included on
# purpose: triage only finds a template, the technolog still confirms the price —
# without this transition standard orders had no way out of their status.
_QUOTABLE_STATUSES = {OrderStatus.niestandard, OrderStatus.standard}
_TRIAGE_STATUSES = {
    OrderStatus.draft,
    OrderStatus.rejected,
    OrderStatus.standard,
    OrderStatus.niestandard,
}
_QUOTE_EDITABLE_STATUSES = {
    OrderStatus.niestandard,
    OrderStatus.standard,
    OrderStatus.quoted,
    OrderStatus.in_production,
}


def _ensure_quote_editable(order: Order) -> None:
    if order.status not in _QUOTE_EDITABLE_STATUSES:
        raise HTTPException(status_code=409, detail="Nie można edytować wyceny dla tego statusu")
    if order.status == OrderStatus.in_production and not order.is_internal:
        raise HTTPException(
            status_code=409,
            detail="Zmiana ceny zewnętrznego zlecenia wymaga ponownej akceptacji przed produkcją",
        )


def _mark_quoted(db: Session, order: Order, user: dict) -> None:
    if order.status not in _QUOTABLE_STATUSES:
        return
    old = order.status.value
    if getattr(order, "is_internal", False):
        # Zlecenie wewnętrzne: brak akceptacji biura (nie ma klienta, którego cenę
        # zatwierdzać) — zapisany koszt własny idzie od razu do produkcji.
        _log_event(db, order, "quoted", old_status=old, new_status="in_production", user=user)
        order.status = OrderStatus.in_production
        if not order.quoted_at:
            order.quoted_at = _now()
        if not order.started_at:
            order.started_at = _now()
    else:
        _log_event(db, order, "quoted", old_status=old, new_status="quoted", user=user)
        order.status = OrderStatus.quoted
        if not order.quoted_at:
            order.quoted_at = _now()


@router.post("/api/orders/{order_id}/triage", response_model=TriageResponse)
def triage_order(
    order_id: int,
    db: Session = Depends(get_db),
    current_user: dict = _TRIAGE,
):
    order = order_service.get_mutable_order_or_404(db, order_id)
    if order.status not in _TRIAGE_STATUSES:
        raise HTTPException(status_code=409, detail="Triage jest zamknięty dla zlecenia w realizacji")

    triage_input = TriageInput(
        client          = order.client,
        material        = order.material or "",
        materials_json  = order.materials_json or [],
        deadline_days   = (order.deadline - date.today()).days if order.deadline else 999,
        has_drawing     = order.has_drawing,
        order_type      = order.order_type or "remont",
        sop_name        = order.sop_name,
        template_id     = order.template_id,
        estimated_value = float(order.estimated_value or 0),
        is_internal     = bool(order.is_internal),
    )

    result = run_triage(triage_input, db)

    old_status = order.status.value if order.status else None
    order.triage_branch = result.branch
    # Zlecenie wewnętrzne: wycena jest OPCJONALNA (nikt jej nie robi, arkusz i tak
    # się drukuje). Nie trzymamy go w 'standard' za bramką wyceny — od razu do
    # produkcji. Koszt własny można policzyć opcjonalnie (panel działa też w
    # in_production). Zamknięcie: jednym krokiem "Zakończ".
    if bool(order.is_internal):
        order.status = OrderStatus.in_production
        if not order.started_at:
            order.started_at = _now()
    else:
        order.status = {
            "odrzut":      OrderStatus.rejected,
            "standard":    OrderStatus.standard,
            "niestandard": OrderStatus.niestandard,
        }[result.branch]
    if result.template_id and not order.template_id:
        order.template_id = result.template_id
    _log_event(db, order, "triage", old_status=old_status, new_status=order.status.value, user=current_user, note=result.message)
    db.commit()

    return TriageResponse(
        branch      = result.branch,
        message     = result.message,
        template_id = result.template_id,
        rule_name   = result.rule_name,
        warnings    = result.warnings or [],
    )


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
    order = order_service.get_mutable_order_or_404(db, order_id)
    _ensure_quote_editable(order)

    quote = db.query(Quote).filter(Quote.order_id == order_id).first()
    if not quote:
        quote = Quote(order_id=order_id)
        db.add(quote)

    quote.total_net        = round(payload.total_net, 2)
    quote.line_items       = []
    quote.processes_json   = []
    quote.materials_json   = []
    quote.labor_hours      = 0
    quote.material_cost    = 0
    quote.material_weight_kg = 0
    quote.material_price_per_kg = 0
    quote.weight_kg        = 0
    quote.weight_rate_pln_kg = 0
    quote.weight_netto_kg  = 0
    quote.weight_brutto_kg = 0
    quote.welding_hours    = 0
    quote.transport_cost   = 0
    quote.overhead_pct     = 0
    quote.margin_pct       = 0
    quote.pricing_method   = "reczna"
    quote.weight_basis     = "netto"
    quote.estimate_version = "manual"
    quote.last_edited_at   = _now()

    _mark_quoted(db, order, current_user)
    _log_event(
        db, order, "quote_saved", user=current_user,
        note=f"{round(payload.total_net, 2)} zł · wycena ręczna",
    )

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
    order = order_service.get_mutable_order_or_404(db, order_id)

    _ensure_quote_editable(order)

    rate_setting = db.get(Setting, "labor_rate_pln")
    labor_rate   = float(rate_setting.value) if rate_setting else DEFAULT_LABOR_RATE_PLN
    if getattr(order, "is_internal", False) and payload.method == "od_masy":
        raise HTTPException(status_code=400, detail="Zlecenie wewnętrzne nie używa wyceny od masy")

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
        weight_kg             = payload.weight_kg,
        weight_rate_pln_kg    = payload.weight_rate_pln_kg,
        method                = payload.method,
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
            "cost": process_total(p),
        }
        for p in payload.processes
    ]
    quote.pricing_method        = payload.method
    quote.weight_basis          = payload.weight_basis
    quote.materials_json        = [m.model_dump() for m in payload.materials]
    quote.material_cost         = result.material_total
    quote.material_weight_kg    = payload.material_weight_kg
    quote.material_price_per_kg = payload.material_price_per_kg
    quote.weight_netto_kg       = payload.weight_netto_kg
    quote.weight_brutto_kg      = payload.weight_brutto_kg
    quote.weight_kg             = payload.weight_kg
    quote.weight_rate_pln_kg    = payload.weight_rate_pln_kg
    quote.labor_hours           = payload.labor_hours
    quote.overhead_pct          = payload.overhead_pct
    quote.margin_pct            = payload.margin_pct
    quote.transport_cost        = payload.transport_cost or 0
    quote.show_unit_prices      = payload.show_unit_prices
    quote.total_net             = result.total_net
    quote.is_zapor              = False
    quote.estimate_version      = "v3"
    quote.last_edited_at        = _now()

    _mark_quoted(db, order, current_user)
    _log_event(
        db, order, "quote_saved", user=current_user,
        note=f"{round(total_net, 2)} zł · wycena strukturalna",
    )

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
        weight_kg             = payload.weight_kg,
        weight_rate_pln_kg    = payload.weight_rate_pln_kg,
        method                = payload.method,
    )
    return QuotePreviewOut(
        ops_total      = result.ops_total,
        material_total = result.material_total,
        extra_labor    = result.extra_labor,
        weight_total   = result.weight_total,
        base           = result.base,
        subtotal       = result.subtotal,
        total_net      = result.total_net,
        pricing_method = result.pricing_method,
        weight_basis   = payload.weight_basis,
        koszt_materialu   = result.material_total,
        koszt_robocizny   = result.ops_total + result.extra_labor,
        koszt_wytworzenia = result.material_total + result.ops_total + result.extra_labor,
    )


@router.post("/api/orders/{order_id}/save-as-template", response_model=TemplateOut, status_code=201)
def save_order_as_template(
    order_id: int,
    payload: SaveAsTemplatePayload,
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    order = order_service.get_mutable_order_or_404(db, order_id)
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
