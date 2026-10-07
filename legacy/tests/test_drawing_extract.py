from pathlib import Path

from drawing_extract import (
    _drawing_no_from_filename,
    _extract_mass,
    _extract_material,
    _parse_bom_lines,
)


def test_drawing_number_falls_back_to_generic_filename():
    assert _drawing_no_from_filename(Path("DEMO-00100-002.pdf")) == "DEMO-00100-002"


def test_extracts_title_block_material_and_mass():
    lines = ["MATERIAŁ: Demo Steel A", "MASA: 18,97 [kg]"]

    assert _extract_material(lines) == "Demo Steel A"
    assert _extract_mass(lines) == 18.97


def test_parses_generic_bom_line():
    rows = _parse_bom_lines(["1 DEMO-00100-001 RHS 160x80x8 S355 2"])

    assert rows[0]["part_no"] == "DEMO-00100-001"
    assert rows[0]["qty"] == 2
