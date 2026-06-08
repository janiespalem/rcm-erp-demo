from fastapi import HTTPException
from sqlalchemy.orm import Session

from models import ApprovedMaterial
from schemas import ApprovedMaterialCreate, ApprovedMaterialPatch


def list_approved_materials(db: Session) -> list[ApprovedMaterial]:
    return (
        db.query(ApprovedMaterial)
        .filter(ApprovedMaterial.is_active.is_(True))
        .order_by(ApprovedMaterial.name)
        .all()
    )


def create_approved_material(db: Session, payload: ApprovedMaterialCreate) -> ApprovedMaterial:
    exists = db.query(ApprovedMaterial).filter(ApprovedMaterial.name == payload.name).first()
    if exists:
        raise HTTPException(status_code=409, detail=f"Materiał '{payload.name}' już istnieje")
    material = ApprovedMaterial(**payload.model_dump())
    db.add(material)
    db.commit()
    db.refresh(material)
    return material


def update_approved_material(db: Session, material_id: int, payload: ApprovedMaterialPatch) -> ApprovedMaterial:
    material = db.get(ApprovedMaterial, material_id)
    if not material:
        raise HTTPException(status_code=404, detail="Materiał nie znaleziony")
    for field, value in payload.model_dump(exclude_unset=True).items():
        setattr(material, field, value)
    db.commit()
    db.refresh(material)
    return material


def archive_approved_material(db: Session, material_id: int) -> None:
    material = db.get(ApprovedMaterial, material_id)
    if not material:
        raise HTTPException(status_code=404, detail="Materiał nie znaleziony")
    material.is_active = False
    db.commit()
