import os
import sys

from sqlalchemy import create_engine
from sqlalchemy.orm import Session

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "backend"))

from models import Base, ProductTemplate
from schemas import TemplatePatch
from services.template_service import patch_template


def test_patch_template_updates_materials_for_catalog_arkusz():
    """Katalog PK editor must persist materials used later by arkusz PDF."""
    engine = create_engine("sqlite:///:memory:", connect_args={"check_same_thread": False})
    Base.metadata.create_all(engine)

    with Session(engine) as db:
        tmpl = ProductTemplate(
            name="PK test",
            category="prefabrykat",
            operations_json=[],
            materials_json=[],
            instruction_blocks=[],
            machines_json=[],
            margin_pct=0.25,
            is_active=True,
        )
        db.add(tmpl)
        db.commit()
        db.refresh(tmpl)

        result = patch_template(
            db,
            tmpl.id,
            TemplatePatch(
                materials_json=[{"mat": "S355", "qty": 2, "unit": "szt", "dim": "50x50x5"}],
                operations_json=[{"op": "Cięcie", "wydział": "CNC", "hours": 0.5, "rate_per_hour": 90}],
                position_nr="DEMO-TEST-001",
            ),
        )

        assert result.materials_json[0]["mat"] == "S355"
        assert result.materials_json[0]["dim"] == "50x50x5"
        assert result.operations_json[0]["hours"] == 0.5
        assert result.position_nr == "DEMO-TEST-001"
