"""
PDF Generator — Arkusz Zlecenia Wewnętrznego

Przepływ:
  1. Jinja2 renderuje arkusz.html → HTML string
  2. WeasyPrint konwertuje HTML → PDF bytes
  3. pypdf dołącza PDF-załączniki (rysunki SolidWorks/PDF) za arkuszem
     → jeden plik do druku, pracownik dostaje wszystko naraz
"""
import io
import logging
import os
import zipfile
from pathlib import Path
from datetime import date
from types import SimpleNamespace
from jinja2 import Environment, FileSystemLoader, select_autoescape

_log = logging.getLogger(__name__)

TEMPLATES_DIR = os.path.join(os.path.dirname(__file__), "..", "templates")
LOGO_PATH = Path(__file__).resolve().parent / "static" / "demo-logo.png"


QUOTE_FIELDS_FOR_ARKUSZ = (
    "materials_json",
    "material_weight_kg",
    "weight_netto_kg",
    "weight_brutto_kg",
    "weight_kg",
)


class DocumentGenerationError(RuntimeError):
    pass


def _require_pdf(data: bytes | None, source: str) -> bytes:
    if not data or not data.startswith(b"%PDF-"):
        raise DocumentGenerationError(f"{source} nie jest poprawnym PDF")
    return data



_DEFAULT_COMPANY = {
    "name":    "DemoFab",
    "address": "ul. Przykładowa 10, 00-001 Warszawa",
    "nip":     "000-000-00-00",
    "regon":   "000000000",
    "tagline": "Manufacturing workflow demo",
}


# Jinja env tworzone RAZ (ma własny cache skompilowanych szablonów) — nie
# odbudowujemy go przy każdym PDF.
_JINJA_ENV = Environment(
    loader=FileSystemLoader(TEMPLATES_DIR),
    autoescape=select_autoescape(["html"]),
)

# WeasyPrint importowany i konfigurowany RAZ. Import ~1.5s + skan fontów są
# kosztowne, a leniwy import w funkcji kazał pierwszemu żądaniu na każdym workerze
# płacić ten koszt (stąd ~10s na pierwszy arkusz). Trzymamy HTML + współdzielony
# FontConfiguration; warmup() rozgrzewa to przy starcie aplikacji.
_WEASY = {"HTML": None, "font_config": None, "ready": False}


def _weasy():
    if not _WEASY["ready"]:
        try:
            from weasyprint import HTML
            try:
                from weasyprint.text.fonts import FontConfiguration
            except Exception:
                from weasyprint.fonts import FontConfiguration  # starsze wersje
            _WEASY["HTML"] = HTML
            _WEASY["font_config"] = FontConfiguration()
        except Exception as e:
            _log.error("WeasyPrint niedostępny: %s", e)
        _WEASY["ready"] = True
    return _WEASY["HTML"], _WEASY["font_config"]


def _html_to_pdf(html_str: str) -> bytes | None:
    HTML, fc = _weasy()
    if HTML is None:
        return None
    return HTML(string=html_str, base_url=TEMPLATES_DIR).write_pdf(font_config=fc)


def warmup() -> None:
    """Rozgrzej import WeasyPrint + fonty przy starcie, by pierwszy arkusz nie czekał."""
    try:
        _html_to_pdf("<html><body>warmup</body></html>")
        _log.info("PDF warmup done")
    except Exception as e:
        _log.error("PDF warmup failed: %s", e)


def _render_html(order, template, quote, company: dict | None = None) -> str:
    tmpl = _JINJA_ENV.get_template("arkusz.html")
    operations = (quote.processes_json or []) if quote else []
    return tmpl.render(
        order=order,
        sop_template=template,
        quote=quote,
        today=date.today().strftime("%d.%m.%Y"),
        operations=operations,
        logo_url=LOGO_PATH.as_uri() if LOGO_PATH.exists() else None,
        company=company or _DEFAULT_COMPANY,
    )


def _quote_for_single_operation(quote, operation: dict | None):
    operations = [operation] if operation else []
    if not quote:
        return SimpleNamespace(processes_json=operations)
    data = {field: getattr(quote, field, None) for field in QUOTE_FIELDS_FOR_ARKUSZ}
    data["processes_json"] = operations
    return SimpleNamespace(**data)


def _safe_filename_part(value, fallback: str) -> str:
    safe_chars = []
    for char in str(value):
        safe_chars.append(char if char.isalnum() or char in "._- " else "_")
    safe = "_".join("".join(safe_chars).split()).strip("._")
    return safe[:70] or fallback


def _operation_name(operation: dict, fallback: str) -> str:
    return _safe_filename_part(
        operation.get("name") or operation.get("op") or fallback,
        fallback,
    )


def _arkusz_filename(order, operation: dict | None = None, index: int | None = None) -> str:
    order_number = str(getattr(order, "order_number", None) or getattr(order, "id", "order"))
    safe_order = _safe_filename_part(order_number, "order")
    if operation is None:
        return f"Arkusz_{safe_order}.pdf"
    safe_operation = _safe_filename_part(
        _operation_name(operation, f"operacja_{index or 1}"),
        f"operacja_{index or 1}",
    )
    return f"Arkusz_{safe_order}_{index or 1:02d}_{safe_operation}.pdf"


def _collect_pdf_attachments(order) -> list[str]:
    """Zwraca absolutne ścieżki do PDF-załączników zlecenia."""
    if not getattr(order, "attachments", None):
        return []
    backend_dir = os.path.dirname(__file__)
    paths = []
    for att in order.attachments:
        if att.mime_type != "application/pdf":
            continue
        abs_path = os.path.abspath(os.path.join(backend_dir, att.stored_path))
        if not os.path.exists(abs_path):
            raise DocumentGenerationError(f"Brak załącznika PDF: {att.filename}")
        paths.append(abs_path)
    return paths


def _merge_pdfs(arkusz_bytes: bytes, pdf_paths: list[str]) -> bytes:
    """Łączy arkusz PDF z rysunkami; nie zwraca niepełnego pakietu."""
    _require_pdf(arkusz_bytes, "Arkusz")
    if not pdf_paths:
        return arkusz_bytes
    try:
        from pypdf import PdfWriter, PdfReader
        writer = PdfWriter()
        writer.append(io.BytesIO(arkusz_bytes))
        for path in pdf_paths:
            reader = PdfReader(path)
            for page in reader.pages:
                writer.add_page(page)
        out = io.BytesIO()
        writer.write(out)
        return _require_pdf(out.getvalue(), "Połączony dokument")
    except Exception as exc:
        raise DocumentGenerationError(f"Nie udało się połączyć PDF: {exc}") from exc


def generate_arkusz_pdf(order, template, quote=None, company: dict | None = None) -> bytes:
    """
    Generuje kompletny PDF do druku:
      strona 1:   arkusz zlecenia (operacje, czas, podpisy)
      strony 2+:  rysunek z szablonu katalogu (template.drawing_path)
      strony N+:  rysunki PDF załączone do zlecenia (order.attachments)
    Błąd któregokolwiek elementu przerywa generowanie zamiast zwracać niepełny pakiet.
    """
    html_str = _render_html(order, template, quote, company=company)
    arkusz_bytes = _require_pdf(_html_to_pdf(html_str), "Wygenerowany arkusz")

    pdf_paths: list[str] = []
    if template and getattr(template, "drawing_path", None):
        backend_dir = os.path.dirname(__file__)
        abs_drawing = os.path.abspath(os.path.join(backend_dir, template.drawing_path))
        if not os.path.exists(abs_drawing):
            raise DocumentGenerationError("Brak rysunku przypisanego do szablonu")
        pdf_paths.append(abs_drawing)
    pdf_paths.extend(_collect_pdf_attachments(order))
    return _merge_pdfs(arkusz_bytes, pdf_paths)


def generate_arkusze_by_operation_zip(order, template, quote=None, company: dict | None = None) -> bytes:
    operations = []
    if quote and getattr(quote, "processes_json", None):
        operations = quote.processes_json or []
    elif template and getattr(template, "operations_json", None):
        operations = template.operations_json or []

    if not operations:
        operations = [None]

    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for index, operation in enumerate(operations, start=1):
            single_quote = _quote_for_single_operation(quote, operation)
            pdf_bytes = generate_arkusz_pdf(order, template, single_quote, company=company)
            archive.writestr(_arkusz_filename(order, operation, index), pdf_bytes)
    return out.getvalue()


def generate_oferta_pdf(order, template, quote=None, company: dict | None = None, vat_rate: float = 0.23) -> bytes:
    """
    Oferta handlowa dla klienta.
    Zawiera: cena netto/brutto, termin. NIE zawiera: stawek, marginu, SOP.
    show_unit_prices defaults to False (safe); forced off for defence orders.
    """
    tmpl = _JINJA_ENV.get_template("oferta.html")
    # Defence orders never expose operation rates; default is False (opt-in only)
    is_defence = bool(getattr(order, "is_defence", False))
    show_breakdown = bool(
        not is_defence
        and quote
        and getattr(quote, "show_unit_prices", False)
        and quote.processes_json
    )
    vat_pct = round(vat_rate * 100)
    html_str = tmpl.render(
        order=order,
        sop_template=template,
        quote=quote,
        today=date.today().strftime("%d.%m.%Y"),
        show_breakdown=show_breakdown,
        company=company or _DEFAULT_COMPANY,
        vat_rate=vat_rate,
        vat_pct=vat_pct,
    )
    return _require_pdf(_html_to_pdf(html_str), "Wygenerowana oferta")


def get_content_type(order) -> str:
    return "application/pdf"
