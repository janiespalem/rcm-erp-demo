"""
Dry-run/import historycznych usług z Excela do katalogu usług.

Domyślnie tylko pokazuje wynik:
    python backend/seed_services_from_excel.py "/path/Lista.xlsx"

Zapis do bazy dopiero z flagą:
    python backend/seed_services_from_excel.py "/path/Lista.xlsx" --apply
"""
from __future__ import annotations

import argparse
import json
from decimal import Decimal
from pathlib import Path

from sqlalchemy.orm import Session

from database import init_db
from models import PriceHistory, ProductTemplate
from service_import import build_service_import_records


def _json_default(value):
    if isinstance(value, Decimal):
        return float(value)
    return str(value)


def _existing_history_keys(db: Session, source: str) -> set[tuple[str, str]]:
    keys: set[tuple[str, str]] = set()
    for rec in db.query(PriceHistory).filter(PriceHistory.source == source).all():
        params = rec.parameters_json or {}
        if isinstance(params, str):
            try:
                params = json.loads(params)
            except json.JSONDecodeError:
                params = {}
        order_number = str(params.get("source_order_number") or "").strip()
        source_row = str(params.get("source_row") or "").strip()
        if order_number or source_row:
            keys.add((order_number, source_row))
    return keys


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("xlsx_path")
    parser.add_argument("--apply", action="store_true", help="Zapisz ProductTemplate + PriceHistory do bazy")
    parser.add_argument("--limit", type=int, default=0, help="Ogranicz liczbę rekordów")
    args = parser.parse_args()

    records = build_service_import_records(args.xlsx_path)
    if args.limit:
        records = records[:args.limit]

    print(json.dumps(
        {
            "source": args.xlsx_path,
            "count": len(records),
            "apply": args.apply,
            "preview": [record.template for record in records[:5]],
        },
        ensure_ascii=False,
        indent=2,
        default=_json_default,
    ))

    if not args.apply:
        return

    engine = init_db()
    with Session(engine) as db:
        created_templates = 0
        created_history = 0
        skipped_history = 0
        source_name = Path(args.xlsx_path).name
        history_keys = _existing_history_keys(db, source_name)
        for record in records:
            duplicate = db.query(ProductTemplate).filter(
                ProductTemplate.position_nr == (record.order_number or None),
                ProductTemplate.project_code.is_(None),
            ).first()
            if not duplicate:
                db.add(ProductTemplate(**record.template))
                created_templates += 1

            params = record.price_history.get("parameters_json") or {}
            history_key = (
                str(params.get("source_order_number") or "").strip(),
                str(params.get("source_row") or "").strip(),
            )
            if history_key in history_keys:
                skipped_history += 1
                continue

            if record.price_history["total_price_historical"] is not None:
                db.add(PriceHistory(**record.price_history))
                created_history += 1
                history_keys.add(history_key)

        db.commit()
        print(json.dumps({
            "created_templates": created_templates,
            "created_price_history": created_history,
            "skipped_price_history": skipped_history,
        }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
