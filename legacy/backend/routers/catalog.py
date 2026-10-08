from typing import List, Optional
from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.orm import Session

from auth import require_role
from database import get_db
from schemas import OperationCatalogCreate, OperationCatalogOut, OperationCatalogUpdate
from models import OperationCatalog

router = APIRouter(prefix="/api/operation-catalog", tags=["Catalog"])

_ALL  = require_role("biuro", "technolog", "ceo")
_TECH = require_role("technolog")
_DIR  = require_role("technolog")


def _auto_keywords(name: str | None, department: str | None, formula: str | None) -> str:
    explicit = (formula or "").strip()
    if explicit and explicit not in {"1", "1.0"}:
        return explicit
    text = f"{name or ''} {department or ''}".lower()
    synonyms = {
        "plazma": "blacha s235 s355 hardox cięcie palenie wyciąć",
        "cięcie": "cięcie wycięcie dociąć profil blacha",
        "piła": "profil rhs shs chs pręt rura cięcie",
        "wiercenie": "otwór gwint tuleja frezowanie rozwiercić",
        "frezowanie": "otwór gwint tuleja wiercenie",
        "spawanie": "mig mag naprawa pęknięcie rama łyżka",
        "montaż": "montaż pasowanie składanie komplet",
        "malowanie": "malowanie pomalować farba podkład",
        "kontrola": "kontrola pomiar jakość wymiary odbiór",
        "gięcie": "gięcie zagięcie prasa łuk blacha",
        "zagięcie": "gięcie zagięcie prasa łuk blacha",
    }
    words = {part for part in text.replace("/", " ").split() if len(part) >= 3}
    for key, extra in synonyms.items():
        if key in text:
            words.update(extra.split())
    return " ".join(sorted(words))

@router.post("/", response_model=OperationCatalogOut, status_code=201)
def create_catalog_operation(
    payload: OperationCatalogCreate,
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    data = payload.model_dump()
    data["formula"] = _auto_keywords(data.get("name"), data.get("department"), data.get("formula"))
    op = OperationCatalog(**data)
    db.add(op)
    db.commit()
    db.refresh(op)
    return op

@router.get("/", response_model=List[OperationCatalogOut])
def list_catalog_operations(db: Session = Depends(get_db), _: dict = _ALL):
    return db.query(OperationCatalog).order_by(OperationCatalog.name).all()


@router.get("/suggest", response_model=List[OperationCatalogOut])
def suggest_catalog_operations(
    text: Optional[str] = None,
    material: Optional[str] = None,
    db: Session = Depends(get_db),
    _: dict = _ALL,
):
    haystack = f"{text or ''} {material or ''}".lower()
    operations = db.query(OperationCatalog).order_by(OperationCatalog.name).all()
    if not haystack.strip():
        return operations[:8]

    scored: list[tuple[int, OperationCatalog]] = []
    for op in operations:
        keywords = " ".join([
            op.name or "",
            op.department or "",
            op.formula or "",
        ]).lower()
        score = 0
        for token in set(haystack.replace("/", " ").replace(",", " ").split()):
            if len(token) < 3:
                continue
            if token in keywords:
                score += 2 if token in (op.formula or "").lower() else 1
        if score:
            scored.append((score, op))

    if not scored:
        return operations[:6]
    scored.sort(key=lambda item: (-item[0], item[1].name))
    return [op for _, op in scored[:8]]


@router.patch("/{operation_id}", response_model=OperationCatalogOut)
def update_catalog_operation(
    operation_id: int,
    payload: OperationCatalogUpdate,
    db: Session = Depends(get_db),
    _: dict = _TECH,
):
    op = db.get(OperationCatalog, operation_id)
    if not op:
        raise HTTPException(status_code=404, detail="Operacja nie znaleziona")
    data = payload.model_dump(exclude_unset=True)
    if {"name", "department", "formula"} & set(data):
        data["formula"] = _auto_keywords(
            data.get("name", op.name),
            data.get("department", op.department),
            data.get("formula", op.formula),
        )
    for field, value in data.items():
        setattr(op, field, value)
    db.commit()
    db.refresh(op)
    return op


@router.delete("/{operation_id}", status_code=204)
def delete_catalog_operation(operation_id: int, db: Session = Depends(get_db), _: dict = _DIR):
    op = db.get(OperationCatalog, operation_id)
    if not op:
        raise HTTPException(status_code=404, detail="Operacja nie znaleziona")
    db.delete(op)
    db.commit()
    return None
