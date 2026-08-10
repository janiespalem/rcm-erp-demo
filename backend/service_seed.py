from __future__ import annotations

import json
from datetime import date
from pathlib import Path
from typing import Any

from sqlalchemy.orm import Session

from models import PriceHistory, ProductTemplate


SERVICE_HISTORY_SOURCE = "synthetic_service_history.json"
SERVICE_HISTORY_SEED_PATH = Path(__file__).resolve().parent / "data" / "service_history_seed.json"


def _parse_date(value: Any) -> date | None:
    if not value:
        return None
    if isinstance(value, date):
        return value
    return date.fromisoformat(str(value))


def seed_service_history_from_builtin(db: Session) -> dict[str, int]:
    """Seed optional synthetic service history idempotently."""
    if db.query(PriceHistory).filter(PriceHistory.source == SERVICE_HISTORY_SOURCE).first():
        return {"created_templates": 0, "created_price_history": 0, "skipped": 1}
    if not SERVICE_HISTORY_SEED_PATH.exists():
        return {"created_templates": 0, "created_price_history": 0, "skipped": 1}

    records = json.loads(SERVICE_HISTORY_SEED_PATH.read_text(encoding="utf-8"))
    created_templates = 0
    created_history = 0

    for record in records:
        template = dict(record["template"])
        position_nr = template.get("position_nr")
        duplicate_template = db.query(ProductTemplate).filter(
            ProductTemplate.position_nr == position_nr,
            ProductTemplate.project_code.is_(None),
        ).first()
        if not duplicate_template:
            db.add(ProductTemplate(**template))
            created_templates += 1

        history = dict(record["price_history"])
        if history.get("total_price_historical") is None:
            continue
        history["order_date"] = _parse_date(history.get("order_date"))
        db.add(PriceHistory(**history))
        created_history += 1

    db.commit()
    return {
        "created_templates": created_templates,
        "created_price_history": created_history,
        "skipped": 0,
    }
