import os
import sys
from pathlib import Path

from openpyxl import Workbook

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "backend"))

from service_import import build_service_import_records, infer_service_category, parse_hours, parse_money


def test_parse_money_polish_netto_values():
    assert parse_money("1 273,16 netto") == 1273.16
    assert parse_money("25 700 netto") == 25700.0
    assert parse_money(None) is None


def test_parse_hours_polish_values():
    assert parse_hours("14h 30min") == 14.5
    assert parse_hours("92,5h") == 92.5
    assert parse_hours(None) is None


def test_infer_service_category_from_description():
    assert infer_service_category("Ładowarka - remont otworów") == "remont"
    assert infer_service_category("spawanie wąsów") == "spawanie"
    assert infer_service_category("zbrojenie do kopuły") == "zbrojenie"


def test_build_service_import_records_maps_excel_to_existing_catalog_shape(tmp_path):
    path = Path(tmp_path) / "uslugi.xlsx"
    workbook = Workbook()
    worksheet = workbook.active
    worksheet.append([None] * 15)
    worksheet.append([None] * 15)
    worksheet.append([
        None,
        "Numer zlecenia",
        "Firma/Klient ",
        "Data Przyjecia",
        "Termin ",
        "Data Wykonania Usługi",
        "Rodzaj wykonania usługi",
        "Materiał",
        "Koszty-Materiału",
        "Wycena",
        "Łaczna kwota usługi",
        "Godziny Konstruktora",
        "Godziny Produkcyjne",
        "Zakończone",
        None,
    ])
    worksheet.append([
        "1.",
        "24/03/2026 - 06/26",
        "DBK",
        None,
        None,
        None,
        "konstrukcja / zbrojenia",
        "pręt żebrowany Fi 6,12 B500SP",
        "2686 netto",
        "3824 netto",
        None,
        "1h",
        "14h 30min",
        "zakończone",
        None,
    ])
    workbook.save(path)

    records = build_service_import_records(path)

    assert len(records) == 1
    record = records[0]
    assert record.template["category"] == "zbrojenie"
    assert record.template["materials_json"][0]["mat"] == "pręt żebrowany Fi 6,12 B500SP"
    assert record.template["materials_json"][0]["cost_pln"] == 2686.0
    assert record.template["base_price_pln"] == 3824.0
    assert record.template["operations_json"][0]["wydział"] == "Konstruktor"
    assert record.template["operations_json"][1]["hours"] == 14.5
    assert record.price_history["parameters_json"]["production_hours"] == 14.5
