"""
Seed szablonów PK1 — Rama GOTOWA (PK1-00100) + Zabudowa (PK1-00200).
Idempotentny — sprawdza position_nr przed wstawieniem.
Uruchomienie: python seed_pk1_new.py
"""
import os, sys
sys.path.insert(0, os.path.dirname(__file__))

from sqlalchemy.orm import Session
from database import init_db
from models import ProductTemplate

DRAWINGS_BASE = "static/drawings/PK1"

# Rama GOTOWA — PK1-00100
# 000 = złożenie (ASM), 001-002 = rysunki wykonawcze (PDF dostępne)
# 003-031 = części (tylko SLDPRT, brak PDF)
RAMA_PARTS = [
    ("PK1", "PK1-00100-000", "Rama GOTOWA (złożenie)",   "podzespół",   f"{DRAWINGS_BASE}/PK1-00100-000.pdf"),
    ("PK1", "PK1-00100-001", "PK1-00100-001",             "prefabrykat", f"{DRAWINGS_BASE}/PK1-00100-001.pdf"),
    ("PK1", "PK1-00100-002", "PK1-00100-002",             "prefabrykat", f"{DRAWINGS_BASE}/PK1-00100-002.pdf"),
] + [
    ("PK1", f"PK1-00100-{i:03d}", f"PK1-00100-{i:03d}", "prefabrykat", None)
    for i in range(3, 32)
]

# Zabudowa — PK1-00200
ZABUDOWA_PARTS = [
    ("PK1", "PK1-00200-000", "Zabudowa (złożenie)", "podzespół", None),
] + [
    ("PK1", f"PK1-00200-{i:03d}", f"PK1-00200-{i:03d}", "prefabrykat", None)
    for i in range(1, 26)
]

ALL_PARTS = RAMA_PARTS + ZABUDOWA_PARTS


def seed_pk1(db: Session):
    added = 0
    updated = 0
    for project_code, pos_nr, name, category, drawing_path in ALL_PARTS:
        exists = db.query(ProductTemplate).filter(
            ProductTemplate.position_nr == pos_nr
        ).first()
        if exists:
            changed = False
            for field, value in {
                "project_code": project_code,
                "name": name,
                "category": category,
                "drawing_path": drawing_path,
            }.items():
                if value is not None and getattr(exists, field) != value:
                    setattr(exists, field, value)
                    changed = True
            if changed:
                updated += 1
            continue
        db.add(ProductTemplate(
            project_code = project_code,
            position_nr  = pos_nr,
            name         = name,
            category     = category,
            drawing_path = drawing_path,
            operations_json = [],
            materials_json  = [],
        ))
        added += 1

    db.commit()
    print(f"✓ Dodano {added}, zaktualizowano {updated} szablonów PK1 ({len(ALL_PARTS)} łącznie)")


if __name__ == "__main__":
    engine = init_db()
    with Session(engine) as db:
        seed_pk1(db)
    print("✅ Seed PK1 zakończony.")
