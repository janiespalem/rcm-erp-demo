from types import SimpleNamespace

import pytest
from fastapi import HTTPException

import pdf_gen
from routers.documents import _get_vat_rate
from tests.helpers import make_test_db


def test_pdf_generation_rejects_html_fallback(monkeypatch):
    monkeypatch.setattr(pdf_gen, "_html_to_pdf", lambda _html: None)
    order = SimpleNamespace(attachments=[], materials_json=[])

    with pytest.raises(pdf_gen.DocumentGenerationError, match="poprawnym PDF"):
        pdf_gen.generate_arkusz_pdf(order, None)


def test_pdf_generation_fails_when_configured_drawing_is_missing(monkeypatch):
    monkeypatch.setattr(pdf_gen, "_html_to_pdf", lambda _html: b"%PDF-1.4\n%%EOF")
    order = SimpleNamespace(attachments=[], materials_json=[])
    template = SimpleNamespace(drawing_path="uploads/templates/missing.pdf", materials_json=[])

    with pytest.raises(pdf_gen.DocumentGenerationError, match="Brak rysunku"):
        pdf_gen.generate_arkusz_pdf(order, template)


def test_arkusz_bom_prefers_order_materials():
    order = SimpleNamespace(
        attachments=[],
        materials_json=[{"name": "ORDER-S355", "qty_kg": 2}],
    )
    template = SimpleNamespace(
        materials_json=[{"name": "TEMPLATE-OLD", "qty": 9}],
        drawing_path=None,
    )
    quote = SimpleNamespace(processes_json=[], materials_json=[])

    html = pdf_gen._render_html(order, template, quote)

    assert "ORDER-S355" in html
    assert "TEMPLATE-OLD" not in html


@pytest.mark.parametrize("value", ["nan", "inf", "-0.01", "1.01", "bad"])
def test_vat_rate_rejects_invalid_values(value):
    db = make_test_db({"vat_rate": value})
    try:
        with pytest.raises(HTTPException) as exc:
            _get_vat_rate(db)
        assert exc.value.status_code == 422
    finally:
        db.close()
