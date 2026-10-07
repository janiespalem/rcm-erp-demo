from typing import Optional

from fastapi import HTTPException
from sqlalchemy.orm import Session

from models import ProductTemplate
from schemas import TemplateCreate


TEMPLATE_PATCH_FIELDS = (
    "name",
    "operations_json",
    "materials_json",
    "instruction_blocks",
    "machines_json",
    "base_price_pln",
    "notes",
    "margin_pct",
    "category",
    "project_code",
    "position_nr",
)


def list_templates(
    db: Session,
    category: Optional[str] = None,
    project_code: Optional[str] = None,
) -> list[ProductTemplate]:
    query = db.query(ProductTemplate).filter(ProductTemplate.is_active.is_(True))
    if category:
        query = query.filter(ProductTemplate.category == category)
    if project_code:
        query = query.filter(ProductTemplate.project_code == project_code)
    return query.all()


def create_template(db: Session, payload: TemplateCreate) -> ProductTemplate:
    template = ProductTemplate(**payload.model_dump())
    db.add(template)
    db.commit()
    db.refresh(template)
    return template


def get_template_or_404(db: Session, template_id: int) -> ProductTemplate:
    template = db.get(ProductTemplate, template_id)
    if not template:
        raise HTTPException(status_code=404, detail="Szablon nie znaleziony")
    return template


def patch_template(db: Session, template_id: int, payload) -> ProductTemplate:
    template = get_template_or_404(db, template_id)
    patch_data = payload.model_dump(exclude_unset=True)
    for field in TEMPLATE_PATCH_FIELDS:
        if field in patch_data:
            setattr(template, field, patch_data[field])
    db.commit()
    db.refresh(template)
    return template


def archive_template(db: Session, template_id: int) -> None:
    template = get_template_or_404(db, template_id)
    if not template.is_active:
        raise HTTPException(status_code=409, detail="Szablon już zarchiwizowany")
    template.is_active = False
    db.commit()


def restore_template(db: Session, template_id: int) -> ProductTemplate:
    template = get_template_or_404(db, template_id)
    template.is_active = True
    db.commit()
    db.refresh(template)
    return template
