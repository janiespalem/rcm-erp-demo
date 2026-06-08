"""
Lightweight SolidWorks PDF text extraction.

This is intentionally local and deterministic: no OCR, no external API calls.
It reads selectable text embedded in exported SolidWorks PDFs and returns a
preview that can be applied to ProductTemplate after human review.
"""
from __future__ import annotations

import re
import logging
from pathlib import Path
from typing import Any

from utils import DEFAULT_LABOR_RATE_PLN

logger = logging.getLogger(__name__)

MAX_EXTRACT_PAGES = 20

PROFILE_PATTERNS = [
    re.compile(r"\b(?:RHS|SHS|CHS)\s+[0-9]+(?:[,.][0-9]+)?x[0-9]+(?:[,.][0-9]+)?x[0-9]+(?:[,.][0-9]+)?\b", re.IGNORECASE),
    re.compile(r"\bROD\s+[0-9]+(?:[,.][0-9]+)?mm(?:,\s*L=[0-9]+(?:[,.][0-9]+)?mm)?\b", re.IGNORECASE),
    re.compile(r"\bBL\s+[0-9]+(?:[,.][0-9]+)?mm\b", re.IGNORECASE),
]


def _clean(value: str | None) -> str:
    if not value:
        return ""
    return re.sub(r"\s+", " ",value).strip()


def _parse_float(value: str | None) -> float | None:
    if not value:
        return None
    try:
        return float(value.replace(",", "."))
    except ValueError:
        return None


def _extract_title_value(lines: list[str], title: str) -> str | None:
    title_upper = title.upper()
    for idx, line in enumerate(lines):
        text = _clean(line)
        upper = text.upper()
        if upper == title_upper and idx + 1 < len(lines):
            return _clean(lines[idx + 1])
        if upper.startswith(title_upper):
            inline = _clean(text[len(title):])
            if inline:
                return inline
    joined = " ".join(_clean(line) for line in lines)
    pattern = rf"{re.escape(title)}\s+(.+?)\s+(?:NUMER RYSUNKU|MATERIAŁ:|MASA:|NIE SKALOWAĆ!)"
    match = re.search(pattern, joined, re.IGNORECASE)
    if match:
        return _clean(match.group(1))
    return None


def _drawing_no_from_filename(pdf_path: Path) -> str | None:
    match = re.search(r"\bPK1-\d{5}-\d{3}\b", pdf_path.stem, re.IGNORECASE)
    return match.group(0).upper() if match else None


def _extract_material(lines: list[str]) -> str | None:
    joined = "\n".join(lines)
    inline_match = re.search(r"MATERIAŁ:\s*([^\n|]+?)(?:\s+wg\b|\n|$)", joined, re.IGNORECASE)
    if inline_match:
        return _clean(inline_match.group(1))
    for idx, line in enumerate(lines):
        text = line.strip()
        if text.startswith("MATERIAŁ:"):
            inline = _clean(text.replace("MATERIAŁ:", "", 1))
            if inline:
                return inline
            if idx + 1 < len(lines):
                return _clean(lines[idx + 1])
    return None


def _extract_mass(lines: list[str]) -> float | None:
    joined = "\n".join(lines)
    match = re.search(r"MASA:\s*([0-9]+(?:[,.][0-9]+)?)\s*\[?kg\]?", joined, re.IGNORECASE)
    return _parse_float(match.group(1)) if match else None


def _extract_profile(lines: list[str]) -> str | None:
    joined = " | ". join(lines)
    for patterns in PROFILE_PATTERNS:
        match = patterns.search(joined)
        if match:
            return _clean(match.group(0))
    return None


def _parse_bom_lines(lines: list[str]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    pattern = re.compile(
        r"^(?P<item>\d+)\s+"
        r"(?P<body>.+?)\s+"
        r"(?P<material>(?:\d\.\d{4}\s+\([^)]+\))|(?=\S*[A-Za-z])\S+)\s+"
        r"(?P<qty>\d+(?:[,.]\d+)?)$"
    )
    for line in lines:
        match = pattern.match(_clean(line))
        if not match:
            continue
        body = _clean(match.group("body"))
        part_match = re.match(r"(?P<part_no>PK1-\d{5}-\d{3})\s+(?P<desc>.+)", body)
        part_no = part_match.group("part_no") if part_match else None
        desc = _clean(part_match.group("desc") if part_match else body)
        rows.append({
            "line_id": f"M{len(rows) + 1}",
            "item_no": int(match.group("item")),
            "part_no": part_no,
            "mat": match.group("material"),
            "name": part_no or desc,
            "dim": desc,
            "qty": _parse_float(match.group("qty")) or 1,
            "unit": "szt",
            "source": "drawing_bom",
        })
    return rows


def _parse_weld_operations(lines_by_page: list[list[str]], default_rate: float) -> list[dict[str, Any]]:
    stage_operations: list[dict[str, Any]] = []
    for page_no, lines in enumerate(lines_by_page, start=1):
        stage = None
        title = None
        for idx, line in enumerate(lines):
            cleaned = _clean(line)
            if cleaned.startswith("ETAP "):
                stage = cleaned
                title = _clean(lines[idx - 1]) if idx > 0 else cleaned
                break
        if not stage:
            continue
        weld_count = 0
        for line in lines:
            if "PN-EN ISO 14341" in line:
                weld_count += 1
        stage_operations.append({
            "op": f"{stage}: {title or 'Spawanie'}",
            "name": f"{stage}: {title or 'Spawanie'}",
            "wydział": "Spawalnia",
            "hours": 0,
            "rate_per_hour": default_rate,
            "material": "PN-EN ISO 14341-A: G3Si1" if weld_count else "",
            "source_page": page_no,
            "source": "drawing_weld_table",
        })
    if not stage_operations:
        return []
    return [_preparation_operation("Spawalnia")] + stage_operations


def _preparation_operation(department: str) -> dict[str, Any]:
    return {
        "op": "Przygotowanie stanowiska / maszyny",
        "name": "Przygotowanie stanowiska / maszyny",
        "wydział": department,
        "hours": 0,
        "rate_per_hour": 0,
        "is_preparation": True,
        "source": "system_preparation",
    }


def extract_drawing_pdf(path: str | Path, max_pages: int = MAX_EXTRACT_PAGES, default_rate: float = DEFAULT_LABOR_RATE_PLN) -> dict[str, Any]:
    from pypdf import PdfReader

    pdf_path = Path(path)
    reader = PdfReader(str(pdf_path))
    if len(reader.pages) > max_pages:
        raise ValueError(f"PDF ma {len(reader.pages)} stron; limit ekstrakcji to {max_pages}")

    lines_by_page: list[list[str]] = []
    all_lines: list[str] = []
    for page in reader.pages:
        text = page.extract_text() or ""
        lines = [_clean(line) for line in text.splitlines() if _clean(line)]
        lines_by_page.append(lines)
        all_lines.extend(lines)

    drawing_no = None
    for line in all_lines:
        if re.fullmatch(r"PK1-\d{5}-\d{3}", line):
            drawing_no = line
            break
    if not drawing_no:
        drawing_no = _drawing_no_from_filename(pdf_path)

    part_name = _extract_title_value(all_lines, "NAZWA CZĘŚCI") or drawing_no or pdf_path.stem
    material = _extract_material(all_lines)
    mass_kg = _extract_mass(all_lines)
    profile = _extract_profile(all_lines)
    bom = _parse_bom_lines(all_lines)

    materials = bom
    if not materials and (material or profile or mass_kg):
        materials = [{
            "line_id": "M1",
            "part_no": drawing_no,
            "mat": material or "Materiał wg rysunku",
            "name": part_name,
            "dim": profile or "",
            "qty": 1,
            "unit": "szt",
            "mass_kg": mass_kg,
            "source": "drawing_title_block",
            "requires_manual_check": not bool(material)
        }]

    operations = _parse_weld_operations(lines_by_page, default_rate)
    if not operations and profile:
        operations = [
            _preparation_operation("CNC"),
            {"op": "Cięcie profilu wg rysunku", "name": "Cięcie profilu wg rysunku", "wydział": "CNC", "hours": 0, "rate_per_hour": default_rate, "material_ref": "M1", "source": "drawing_profile"},
            {"op": "Kontrola wymiarów wg rysunku", "name": "Kontrola wymiarów wg rysunku", "wydział": "KJ", "hours": 0, "rate_per_hour": default_rate, "material_ref": "M1", "source": "drawing_profile"},
        ]

    return {
        "drawing_no": drawing_no,
        "name": part_name,
        "material": material,
        "mass_kg": mass_kg,
        "profile": profile,
        "pages": len(reader.pages),
        "materials_json": materials,
        "operations_json": operations,
        "raw_text_preview": "\n".join(all_lines[:80]),
    }
