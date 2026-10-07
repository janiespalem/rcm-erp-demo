"""
All pricing calculations live here — single source of truth for formula logic.
Routers call these functions and persist the results; they do not implement math.
"""
from dataclasses import dataclass
from typing import List

from utils import DEFAULT_LABOR_RATE_PLN, DEFAULT_OVERHEAD_PCT, DEFAULT_MARGIN_PCT

@dataclass
class StructuredQuoteResult:
    ops_total:      float
    material_total: float
    extra_labor:    float
    weight_total:   float
    base:           float
    subtotal:       float
    total_net:      float
    pricing_method: str = "kalkulacja"


def _material_line_total(line) -> float:
    """One material row: explicit cost if provided (>0), else qty_kg × price_per_kg."""
    cost = float(getattr(line, "cost", 0) or 0)
    if cost > 0:
        return cost
    qty = float(getattr(line, "qty_kg", 0) or 0)
    price = float(getattr(line, "price_per_kg", 0) or 0)
    return qty * price


def process_total(process) -> float:
    """Use legacy fixed cost only when hours/rate were not supplied at all."""
    supplied = getattr(process, "model_fields_set", set())
    if "hours" in supplied or "rate_per_hour" in supplied:
        return float(process.hours or 0) * float(process.rate_per_hour or 0)
    return float(process.cost or 0)


def calc_structured_quote(
    processes: List,   # objects with .hours, .rate_per_hour, .cost attributes
    material_cost: float = 0.0,
    material_weight_kg: float = 0.0,
    material_price_per_kg: float = 0.0,
    labor_hours: float = 0.0,
    overhead_pct: float = DEFAULT_OVERHEAD_PCT,
    margin_pct: float = DEFAULT_MARGIN_PCT,
    transport_cost: float = 0.0,
    labor_rate: float = DEFAULT_LABOR_RATE_PLN,
    materials: List = None,   # objects with .qty_kg, .price_per_kg, .cost; multi-material v3
    weight_kg: float = 0.0,
    weight_rate_pln_kg: float = 0.0,
    method: str = "kalkulacja",
) -> StructuredQuoteResult:
    ops_total = sum(process_total(process) for process in processes)
    if materials:
        # Multi-material: sum each line. Legacy single fields are ignored.
        material_total = sum(_material_line_total(m) for m in materials)
    else:
        # Legacy single-material behavior — preserved exactly.
        material_total = (
            material_weight_kg * material_price_per_kg
            if material_price_per_kg > 0
            else material_cost
        )
    extra_labor = labor_hours * labor_rate
    method = method or "kalkulacja"
    weight_total = (weight_kg or 0) * (weight_rate_pln_kg or 0)
    if method == "od_masy":
        # PLN/kg is an all-in commercial shortcut, not another line item.
        base = weight_total
        subtotal = base
        total_net = round(base + transport_cost, 2)
    else:
        weight_total = 0.0
        base = ops_total + material_total + extra_labor
        subtotal = base * (1 + overhead_pct)
        total_net = round(subtotal * (1 + margin_pct) + transport_cost, 2)
    return StructuredQuoteResult(
        ops_total=ops_total,
        material_total=material_total,
        extra_labor=extra_labor,
        weight_total=weight_total,
        base=base,
        subtotal=subtotal,
        total_net=total_net,
        pricing_method=method,
    )
