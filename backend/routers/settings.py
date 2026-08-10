"""
System settings and approved materials catalog.
"""
from typing import List

from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.orm import Session

from auth import require_role
from database import get_db
from models import Setting
from schemas import ApprovedMaterialCreate, ApprovedMaterialOut, ApprovedMaterialPatch, SettingUpdate
from services import material_service

router = APIRouter()

_ALL = require_role("biuro", "technolog", "ceo")
_DIR = require_role("technolog")

# Numeric constraints for known setting keys: (min_inclusive, max_inclusive)
_NUMERIC_SETTINGS: dict = {
    "labor_rate_pln":       (0.01, 10_000),
    "min_order_value":      (0,    1_000_000),
    "default_overhead_pct": (0,    1),
    "default_margin_pct":   (0,    1),
}


def _validate_setting(key: str, raw: str) -> str:
    if key not in _NUMERIC_SETTINGS:
        return raw
    lo, hi = _NUMERIC_SETTINGS[key]
    try:
        v = float(raw)
    except ValueError:
        raise HTTPException(status_code=422, detail=f"Wartość '{raw}' nie jest liczbą")
    if not (lo <= v <= hi):
        raise HTTPException(
            status_code=422,
            detail=f"Wartość {v} poza dozwolonym zakresem [{lo}, {hi}] dla klucza '{key}'",
        )
    return raw


@router.get("/api/settings")
def get_settings(db: Session = Depends(get_db), _: dict = _ALL):
    return db.query(Setting).all()


@router.patch("/api/settings/{key}")
def update_setting(key: str, payload: SettingUpdate, db: Session = Depends(get_db), _: dict = _DIR):
    setting = db.get(Setting, key)
    if not setting:
        raise HTTPException(status_code=404, detail=f"Ustawienie '{key}' nie istnieje")
    setting.value = _validate_setting(key, str(payload.value))
    db.commit()
    return {"key": key, "value": setting.value}


@router.get("/api/approved-materials", response_model=List[ApprovedMaterialOut])
def list_approved_materials(db: Session = Depends(get_db), _: dict = _ALL):
    return material_service.list_approved_materials(db)


@router.post("/api/approved-materials", response_model=ApprovedMaterialOut, status_code=201)
def create_approved_material(
    payload: ApprovedMaterialCreate,
    db: Session = Depends(get_db),
    _: dict = _DIR,
):
    return material_service.create_approved_material(db, payload)


@router.patch("/api/approved-materials/{mat_id}", response_model=ApprovedMaterialOut)
def update_approved_material(
    mat_id: int,
    payload: ApprovedMaterialPatch,
    db: Session = Depends(get_db),
    _: dict = _DIR,
):
    return material_service.update_approved_material(db, mat_id, payload)


@router.delete("/api/approved-materials/{mat_id}", status_code=204)
def delete_approved_material(
    mat_id: int,
    db: Session = Depends(get_db),
    _: dict = _DIR,
):
    material_service.archive_approved_material(db, mat_id)
