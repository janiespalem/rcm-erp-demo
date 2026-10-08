"""
Triage Engine — serce systemu.
Wejście: dane zlecenia od Biuro
Wyjście: TriageResult (odrzut / standard / niestandard) + wiadomość

Logika (dokładnie wg schematu z kartki):
  1. ODRZUT   — twarде reguły z bazy (materiał, termin, marża)
  2. STANDARD — jest szablon w katalogu + klient ma gotowy rysunek
  3. NIESTANDARD — wszystko inne → kolejka Technologa
"""
from dataclasses import dataclass, field
from typing import Optional
from sqlalchemy.orm import Session
from models import ConstraintRule, ProductTemplate, ApprovedMaterial


@dataclass
class TriageInput:
    """Dane wejściowe z formularza Biuro (Step-by-Step Wizard)."""
    client: str
    material: str              # np. "nierdzewka", "S355", "żeliwo"
    deadline_days: int         # ile dni do deadline
    has_drawing: bool          # Gotowy projekt? ✓/✗
    order_type: str            # typ z Wizarda: "remont", "catalog", "nowa_czesc"
    sop_name: Optional[str]    # np. "Wymiana zęba" — jeśli order_type == "remont"
    template_id: Optional[int] = None   # ID wybranego produktu (catalog) — przekazany z Order
    estimated_value: float = 0.0
    is_internal: bool = False  # zlecenie wewnętrzne (własna firma) → omija niestandard/odrzut
    materials_json: list = field(default_factory=list)


@dataclass
class TriageResult:
    branch: str               # "odrzut" | "standard" | "niestandard"
    message: str              # wiadomość dla Biuro
    template_id: Optional[int] = None   # ID szablonu jeśli branch == "standard"
    rule_name: Optional[str] = None     # nazwa reguły jeśli branch == "odrzut"
    warnings: list = field(default_factory=list)             # ostrzeżenia (action="warn") — nie blokują zlecenia


def run_triage(order: TriageInput, db: Session) -> TriageResult:
    """
    Główna funkcja Triage Engine.
    Kolejność sprawdzeń jest ważna — Odrzut zawsze sprawdzamy PIERWSZE.
    """

    # ----------------------------------------------------------------
    # KROK 1: ODRZUT — sprawdź twarde reguły z bazy danych
    # action="reject" → twardy odrzut, action="warn" → ostrzeżenie (nie blokuje)
    # ----------------------------------------------------------------
    rules = db.query(ConstraintRule).filter(ConstraintRule.is_active == True).all()
    warnings = []

    for rule in rules:
        if _matches_rule(order, rule):
            if rule.action == "reject":
                return TriageResult(
                    branch="odrzut",
                    message=rule.message or f"Odrzut: {rule.rule_name}",
                    rule_name=rule.rule_name,
                    warnings=warnings,
                )
            else:
                # action="warn" — zapisz ostrzeżenie, ale nie blokuj
                warnings.append(rule.message or rule.rule_name)

    # ----------------------------------------------------------------
    # KROK 1b: WHITELIST MATERIAŁÓW
    # Jeśli tabela approved_materials nie jest pusta i materiał zlecenia
    # nie pasuje do żadnego wpisu — dodaj ostrzeżenie (nie blokuje).
    # ----------------------------------------------------------------
    material_names = _material_names(order)
    if material_names:
        approved = db.query(ApprovedMaterial).filter(ApprovedMaterial.is_active == True).all()
        if approved:
            approved_names = {m.name.lower() for m in approved}
            for material in material_names:
                if material.lower() not in approved_names:
                    warnings.append(
                        f"Nieznany materiał '{material}' — nie ma go na liście zatwierdzonych. Zweryfikuj."
                    )

    if order.is_internal:
        return TriageResult(
            branch="standard",
            message="Zlecenie wewnętrzne — od razu do kalkulacji kosztu własnego.",
            warnings=warnings,
        )

    # ----------------------------------------------------------------
    # KROK 2: STANDARD
    # Przypadek A: Biuro wybrało produkt z katalogu (order_type == "catalog")
    #              → zawsze Standard, niezależnie od rysunku
    # Przypadek B: Jest rysunek + pasujący szablon w bazie
    # ----------------------------------------------------------------
    if order.order_type == "catalog":
        template = db.query(ProductTemplate).filter(
            ProductTemplate.id == order.template_id,
            ProductTemplate.is_active == True
        ).first() if order.template_id else None

        if not template:
            return TriageResult(
                branch="niestandard",
                message="Niestandard: zamówienie katalogowe bez aktywnego szablonu. Przekazano do Technologa.",
                warnings=warnings,
            )

        return TriageResult(
            branch="standard",
            message=f"Standard (katalog): '{template.name}'. Technolog potwierdzi wycenę.",
            template_id=template.id,
            warnings=warnings,
        )

    if order.has_drawing:
        template = _find_matching_template(order, db)
        if template:
            return TriageResult(
                branch="standard",
                message=f"Standard: zastosowano szablon '{template.name}'.",
                template_id=template.id,
                warnings=warnings,
            )

    # ----------------------------------------------------------------
    # KROK 3: NIESTANDARD — brak szablonu lub brak rysunku
    # ----------------------------------------------------------------
    reason = "brak rysunku" if not order.has_drawing else "brak pasującego szablonu"
    return TriageResult(
        branch="niestandard",
        message=f"Niestandard ({reason}). Przekazano do Technologa.",
        warnings=warnings,
    )


def _matches_rule(order: TriageInput, rule: ConstraintRule) -> bool:
    """
    Sprawdza czy zlecenie narusza regułę Odrzutu.
    Obsługiwane operatory: eq, in, lt, gt
    """
    if rule.field == "material":
        return any(_matches_value(material, rule) for material in _material_names(order))

    # Pobierz wartość pola z obiektu order
    field_value = getattr(order, rule.field, None)
    if field_value is None:
        return False

    return _matches_value(field_value, rule)


def _matches_value(field_value, rule: ConstraintRule) -> bool:
    """Sprawdza czy jedna wartość narusza regułę."""

    op: str = rule.operator
    rule_val: str = rule.value

    if op == "eq":
        # Porównanie case-insensitive dla stringów
        return str(field_value).lower() == rule_val.lower()

    elif op == "in":
        # rule.value to lista rozdzielona przecinkiem: "nierdzewka,aluminium,tytan"
        allowed = [v.strip().lower() for v in rule_val.split(",")]
        return str(field_value).lower() in allowed

    elif op == "lt":
        # Pole musi być liczbą (np. deadline_days, estimated_value)
        try:
            return float(field_value) < float(rule_val)
        except (TypeError, ValueError):
            return False

    elif op == "gt":
        try:
            return float(field_value) > float(rule_val)
        except (TypeError, ValueError):
            return False

    return False


def _material_names(order: TriageInput) -> list[str]:
    names = []
    for item in order.materials_json or []:
        if isinstance(item, dict):
            name = item.get("name") or item.get("material") or item.get("mat")
            if name:
                names.append(str(name))
        elif item:
            names.append(str(item))
    if order.material:
        names.insert(0, order.material)
    return list(dict.fromkeys(n.strip() for n in names if n and n.strip()))


def _find_matching_template(order: TriageInput, db: Session) -> Optional[ProductTemplate]:
    templates = db.query(ProductTemplate).filter(ProductTemplate.is_active == True).all()
    if order.template_id:
        return next((template for template in templates if template.id == order.template_id), None)
    if not order.sop_name:
        return None
    matches = [
        template
        for template in templates
        if (template.name or "").casefold() == order.sop_name.strip().casefold()
    ]
    return matches[0] if len(matches) == 1 else None
