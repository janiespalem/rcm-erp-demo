import os
import sys
from pathlib import Path

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "backend"))

from drawing_extract import extract_drawing_pdf


ROOT = Path(__file__).resolve().parents[1]


def test_extract_single_part_pdf_metadata():
    result = extract_drawing_pdf(ROOT / "backend/static/drawings/PK1/PK1-00100-001.pdf")

    assert result["drawing_no"] == "PK1-00100-001"
    assert result["name"] == "PK1-00100-001"
    assert result["material"] == "1.0045 (S355JR)"
    assert result["mass_kg"] == 68.66
    assert result["profile"] == "RHS 160x80x8"
    assert result["materials_json"][0]["dim"] == "RHS 160x80x8"
    assert result["operations_json"][0]["is_preparation"] is True
    assert result["operations_json"][1]["wydział"] == "CNC"


def test_extract_assembly_pdf_bom_and_weld_stages():
    result = extract_drawing_pdf(ROOT / "backend/static/drawings/PK1/PK1-00100-000.pdf")

    assert result["drawing_no"] == "PK1-00100-000"
    assert result["name"] == "RAMA PODWOZIA PK1"
    assert result["mass_kg"] == 439.43
    assert len(result["materials_json"]) >= 31
    assert any(m["name"] == "KULA WYWROTU Część zakupowa" for m in result["materials_json"])
    assert any(m["part_no"] == "PK1-00100-001" and "RHS" in m["dim"] for m in result["materials_json"])
    assert result["operations_json"][0]["op"] == "Przygotowanie stanowiska / maszyny"
    assert [op["op"].split(":")[0] for op in result["operations_json"][1:]] == ["ETAP 1", "ETAP 2", "ETAP 3", "ETAP 4"]


def test_extract_inline_material_from_a4_part_pdf():
    result = extract_drawing_pdf(ROOT / "backend/static/drawings/PK1/PK1-00100-002.pdf")

    assert result["drawing_no"] == "PK1-00100-002"
    assert result["material"] == "1.0045 (S355JR)"
    assert result["mass_kg"] == 18.97


def test_extract_drawing_no_falls_back_to_filename(monkeypatch):
    import drawing_extract

    original_fullmatch = drawing_extract.re.fullmatch

    def no_exact_text_drawing_match(pattern, string, *args, **kwargs):
        if pattern == r"PK1-\d{5}-\d{3}":
            return None
        return original_fullmatch(pattern, string, *args, **kwargs)

    monkeypatch.setattr(drawing_extract.re, "fullmatch", no_exact_text_drawing_match)

    result = extract_drawing_pdf(ROOT / "backend/static/drawings/PK1/PK1-00100-000.pdf")

    assert result["drawing_no"] == "PK1-00100-000"
