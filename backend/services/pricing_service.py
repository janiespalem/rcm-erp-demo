"""
All pricing calculations live here — single source of truth for formula logic.
Routers call these functions and persist the results; they do not implement math.
"""
from dataclasses import dataclass
from typing import List

from utils import DEFAULT_LABOR_RATE_PLN, DEFAULT_OVERHEAD_PCT, DEFAULT_MARGIN_PCT

ZAPOR_MULTIPLIER_DEFAULT = 3.5


@dataclass
class SimpleQuoteResult:
    labor_cost:  float
    subtotal:    float
    total_net:   float


@dataclass
class StructuredQuoteResult:
    ops_total:      float
    material_total: float
    extra_labor:    float
    base:           float
    subtotal:       float
    total_net:      float


@dataclass
class ZaporQuoteResult:
    multiplier: float
    total_net:  float
    margin_pct: float


def calc_simple_quote(
    labor_hours: float,
    material_cost: float,
    overhead_pct: float = DEFAULT_OVERHEAD_PCT,
    margin_pct: float = DEFAULT_MARGIN_PCT,
    labor_rate: float = DEFAULT_LABOR_RATE_PLN,
) -> SimpleQuoteResult:
    labor_cost = labor_hours * labor_rate
    subtotal   = (material_cost + labor_cost) * (1 + overhead_pct)
    total_net  = round(subtotal * (1 + margin_pct), 2)
    return SimpleQuoteResult(
        labor_cost=labor_cost,
        subtotal=subtotal,
        total_net=total_net,
    )


def _material_line_total(line) -> float:
    """One material row: explicit cost if provided (>0), else qty_kg × price_per_kg."""
    cost = float(getattr(line, "cost", 0) or 0)
    if cost > 0:
        return cost
    qty = float(getattr(line, "qty_kg", 0) or 0)
    price = float(getattr(line, "price_per_kg", 0) or 0)
    return qty * price


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
) -> StructuredQuoteResult:
    ops_total = sum(
        p.hours * p.rate_per_hour if (p.hours and p.rate_per_hour) else (p.cost or 0)
        for p in processes
    )
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
    base        = ops_total + material_total + extra_labor
    subtotal    = base * (1 + overhead_pct)
    total_net   = round(subtotal * (1 + margin_pct) + transport_cost, 2)
    return StructuredQuoteResult(
        ops_total=ops_total,
        material_total=material_total,
        extra_labor=extra_labor,
        base=base,
        subtotal=subtotal,
        total_net=total_net,
    )


def calc_zapor_quote(
    material_cost: float,
    hours_estimate: float,
    zapor_multiplier: float = ZAPOR_MULTIPLIER_DEFAULT,
) -> ZaporQuoteResult:
    multiplier = max(1.0, float(zapor_multiplier))
    total_net  = round(material_cost * hours_estimate * multiplier, 2)
    margin_pct = round(multiplier - 1, 4)
    return ZaporQuoteResult(
        multiplier=multiplier,
        total_net=total_net,
        margin_pct=margin_pct,
    )
