"""
PDF Generator — Arkusz Zlecenia Wewnętrznego

Przepływ:
  1. Jinja2 renderuje arkusz.html → HTML string
  2. WeasyPrint konwertuje HTML → PDF bytes  (fallback: zwraca HTML)
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
    "material_weight_kg",
    "weight_netto_kg",
    "weight_brutto_kg",
    "weight_kg",
)



def _render_html(order, template, quote) -> str:
    env = Environment(
        loader=FileSystemLoader(TEMPLATES_DIR),
        autoescape=select_autoescape(["html"]),
    )
    tmpl = env.get_template("arkusz.html")
    operations = (quote.processes_json or []) if quote else []
    return tmpl.render(
        order=order,
        sop_template=template,
        quote=quote,
        today=date.today().strftime("%d.%m.%Y"),
        operations=operations,
        logo_url=LOGO_PATH.as_uri() if LOGO_PATH.exists() else None,
    )


def _quote_for_single_operation(quote, operation: dict | None):
    operations = [operation] if operation else []
    if not quote:
        return SimpleNamespace(processes_json=operations)
    data = {field: getattr(quote, field, None) for field in QUOTE_FIELDS_FOR_ARKUSZ}
    data["processes_json"] = operations
    return SimpleNamespace(**data)


def _operation_name(operation: dict, fallback: str) -> str:
    raw_name = operation.get("name") or operation.get("op") or fallback
    safe_chars = []
    for char in str(raw_name):
        safe_chars.append(char if char.isalnum() or char in "._- " else "_")
    return "_".join("".join(safe_chars).split())[:70] or fallback


def _arkusz_filename(order, operation: dict | None = None, index: int | None = None) -> str:
    order_number = str(getattr(order, "order_number", None) or getattr(order, "id", "order"))
    safe_order = order_number.replace("/", "-")
    if operation is None:
        return f"Arkusz_{safe_order}.pdf"
    safe_operation = _operation_name(operation, f"operacja_{index or 1}")
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
        if os.path.exists(abs_path):
            paths.append(abs_path)
    return paths


def _merge_pdfs(arkusz_bytes: bytes, pdf_paths: list[str]) -> bytes:
    """Łączy arkusz PDF z rysunkami w jeden plik. Jeśli pypdf niedostępny — zwraca sam arkusz."""
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
        return out.getvalue()
    except Exception as e:
        _log.error("Blad lacznia PDF: %s", e)
        return arkusz_bytes


def generate_arkusz_pdf(order, template, quote=None) -> bytes:
    """
    Generuje kompletny PDF do druku:
      strona 1:   arkusz zlecenia (operacje, czas, podpisy)
      strony 2+:  rysunek z szablonu katalogu (template.drawing_path)
      strony N+:  rysunki PDF załączone do zlecenia (order.attachments)
    Fallback na HTML jeśli WeasyPrint nie działa.
    """
    html_str = _render_html(order, template, quote)

    try:
        from weasyprint import HTML
        arkusz_bytes = HTML(string=html_str, base_url=TEMPLATES_DIR).write_pdf()

        # Zbierz wszystkie rysunki do dołączenia
        pdf_paths: list[str] = []
        if template and getattr(template, "drawing_path", None):
            backend_dir = os.path.dirname(__file__)
            abs_drawing = os.path.abspath(os.path.join(backend_dir, template.drawing_path))
            if os.path.exists(abs_drawing):
                pdf_paths.append(abs_drawing)
        pdf_paths.extend(_collect_pdf_attachments(order))

        return _merge_pdfs(arkusz_bytes, pdf_paths)
    except Exception as e:
        _log.error("Blad generowania PDF: %s", e)
        return html_str.encode("utf-8")


def generate_arkusze_by_operation_zip(order, template, quote=None) -> bytes:
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
            pdf_bytes = generate_arkusz_pdf(order, template, single_quote)
            archive.writestr(_arkusz_filename(order, operation, index), pdf_bytes)
    return out.getvalue()


def generate_oferta_pdf(order, template, quote=None) -> bytes:
    """
    Oferta handlowa dla klienta.
    Zawiera: cena netto/brutto, termin. NIE zawiera: stawek, marginu, SOP.
    """
    env = Environment(
        loader=FileSystemLoader(TEMPLATES_DIR),
        autoescape=select_autoescape(["html"]),
    )
    tmpl = env.get_template("oferta.html")
    show_breakdown = bool(
        quote and getattr(quote, "show_unit_prices", True) and quote.processes_json
    )
    html_str = tmpl.render(
        order=order,
        sop_template=template,
        quote=quote,
        today=date.today().strftime("%d.%m.%Y"),
        show_breakdown=show_breakdown,
    )
    try:
        from weasyprint import HTML
        return HTML(string=html_str, base_url=TEMPLATES_DIR).write_pdf()
    except Exception as e:
        _log.error("Blad generowania PDF: %s", e)
        return html_str.encode("utf-8")


def get_content_type(order) -> str:
    try:
        from weasyprint import HTML  # noqa: F401
        return "application/pdf"
    except ImportError:
        return "text/html; charset=utf-8"
