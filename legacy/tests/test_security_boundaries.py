import math

import pytest
from pydantic import ValidationError
from starlette.routing import Mount

from main import app
from schemas import (
    ActualHoursUpdate,
    ManualQuoteCreate,
    OrderCreate,
    OrderUpdate,
    ParameterRequestCreate,
    QuoteStructuredCreate,
    TemplatePatch,
)
from utils import safe_upload_filename


def test_backend_static_directory_is_not_publicly_mounted():
    mounts = {route.path for route in app.routes if isinstance(route, Mount)}
    paths = {getattr(route, "path", None) for route in app.routes}

    assert "/static" not in mounts
    assert not any(path and path.startswith("/static/") for path in paths)


@pytest.mark.parametrize(
    ("schema", "payload"),
    [
        (OrderCreate, {"client": "x", "deadline": "2026-08-01", "quantity": 0}),
        (ManualQuoteCreate, {"total_net": math.inf}),
        (QuoteStructuredCreate, {"material_cost": -1}),
        (ActualHoursUpdate, {"actual_hours": -1}),
        (OrderUpdate, {"client": None}),
        (TemplatePatch, {"materials_json": None}),
        (ParameterRequestCreate, {"question_text": "   "}),
    ],
)
def test_financial_and_production_inputs_reject_invalid_numbers(schema, payload):
    with pytest.raises(ValidationError):
        schema(**payload)


def test_safe_upload_filename_fits_filesystem_component_with_prefix():
    safe = safe_upload_filename("ą" * 300 + ".pdf")
    assert len(f"12345678_{safe}".encode("utf-8")) <= 255
