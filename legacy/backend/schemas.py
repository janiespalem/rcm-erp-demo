"""
Pydantic schemas — walidacja danych wejście/wyjście API.
Oddzielone od modeli SQLAlchemy celowo (separacja warstw).
"""
from datetime import date, datetime
from typing import Optional, List, Any, Literal
from pydantic import BaseModel, Field, field_validator

from utils import DEFAULT_OVERHEAD_PCT, DEFAULT_MARGIN_PCT


# =============================================================================
# USERS
# =============================================================================
class UserOut(BaseModel):
    id:   int
    name: str
    role: str
    model_config = {"from_attributes": True}


# =============================================================================
# ORDERS — Zlecenie Wewnętrzne
# =============================================================================
class OrderCreate(BaseModel):
    """Dane wejściowe z Wizarda Biuro (Step 1-3)."""
    order_number:    Optional[str]  = None
    client:          str            = Field(default="", max_length=200)
    deadline:        date
    approved_material_id: Optional[int] = None 
    material:        Optional[str]  = None
    materials_json:  List[Any]      = Field(default_factory=list)
    has_drawing:     bool           = False
    order_type:      str            = "remont"   # remont | catalog | nowa_czesc | zbrojenie
    sop_name:        Optional[str]  = None
    purpose:         Optional[str]  = None
    notes:           Optional[str]  = None
    estimated_value: float          = Field(default=0.0, ge=0, le=99_999_999.99, allow_inf_nan=False)
    description:     Optional[str]  = None        # Opis problemu (remont/nowa_czesc)
    requires_visit:  bool           = False        # Wymaga wizyty u klienta
    template_id:     Optional[int]  = None         # Wybrany produkt z katalogu
    quantity:        int            = Field(default=1, ge=1, le=1_000_000)  # Ilość sztuk
    is_defence:      bool           = False        # Restricted-project marker
    is_internal:     bool           = False        # Zlecenie wewnętrzne (własna firma)
    weight_kg:        Optional[float] = Field(default=None, ge=0, le=9_999_999.999, allow_inf_nan=False)
    drawing_number:   Optional[str]  = None        # Nr rysunku
    dimensions:       Optional[str]  = None        # Wymiary
    delivery_address: Optional[str]  = None        # Adres dostawy
    contact:          Optional[str]  = None        # Osoba kontaktowa / telefon


class OrderOut(BaseModel):
    version_id: int
    id:            int
    order_number:  Optional[str]
    approved_material_id: Optional[int] = None
    client:        str
    status:        str
    triage_branch: Optional[str]
    deadline:      Optional[date]
    has_drawing:   bool
    material:      Optional[str]
    materials_json: List[Any] = Field(default_factory=list)

    # Stare zlecenia mają materials_json = NULL (kolumna dodana później) — z ORM
    # przychodzi None, a pole wymaga listy → 500 na całym GET /orders. Koercja None→[].
    @field_validator("materials_json", mode="before")
    @classmethod
    def _materials_json_none_to_list(cls, v):
        return v if isinstance(v, list) else []

    notes:         Optional[str]
    order_type:    Optional[str]   = None
    sop_name:      Optional[str]   = None
    template_id:   Optional[int]   = None
    description:   Optional[str]   = None
    purpose:       Optional[str]   = None
    requires_visit: bool           = False
    quantity:      Optional[int]   = None
    estimated_value: Optional[float] = Field(default=None, ge=0, le=99_999_999.99, allow_inf_nan=False)
    is_defence:    bool            = False
    is_internal:   bool            = False
    weight_kg:        Optional[float] = None
    drawing_number:   Optional[str]  = None
    dimensions:       Optional[str]  = None
    delivery_address: Optional[str]  = None
    contact:          Optional[str]  = None
    created_at:    datetime
    quoted_at:     Optional[datetime] = None
    started_at:    Optional[datetime] = None
    completed_at:  Optional[datetime] = None
    delivered_at:  Optional[datetime] = None
    archived_at:   Optional[datetime] = None
    model_config = {"from_attributes": True}


class OrderUpdate(BaseModel):
    """Частичное обновление заказа (PATCH). Передаём только изменённые поля."""
    version_id: int = Field(ge=1)
    order_number:    Optional[str]   = None
    client:          Optional[str]   = None
    deadline:        Optional[date]  = None
    approved_material_id: Optional[int] = None
    material:        Optional[str]   = None
    has_drawing:     Optional[bool]  = None
    order_type:      Optional[str]   = None
    sop_name:        Optional[str]   = None
    purpose:         Optional[str]   = None
    notes:           Optional[str]   = None
    estimated_value: Optional[float] = None
    description:     Optional[str]   = None
    requires_visit:  Optional[bool]  = None
    quantity:        Optional[int]   = Field(default=None, ge=1, le=1_000_000)
    is_defence:      Optional[bool]  = None
    weight_kg:        Optional[float] = Field(default=None, ge=0, le=9_999_999.999, allow_inf_nan=False)
    drawing_number:   Optional[str]  = None
    dimensions:       Optional[str]  = None
    delivery_address: Optional[str]  = None
    contact:          Optional[str]  = None

    @field_validator("client")
    @classmethod
    def _client_cannot_be_null(cls, value):
        if value is None:
            raise ValueError("Klient nie może być null")
        return value


# =============================================================================
# TRIAGE
# =============================================================================
class TriageResponse(BaseModel):
    branch:      str   # "odrzut" | "standard" | "niestandard"
    message:     str
    template_id: Optional[int] = None
    rule_name:   Optional[str] = None
    warnings:    List[str] = []   # ostrzeżenia (action="warn") — wyświetlane, nie blokują


# =============================================================================
# QUOTES — Wycena
# =============================================================================
class ProcessItem(BaseModel):
    """Operacja produkcyjna v3: hours × rate_per_hour. Legacy: cost bezpośredni."""
    name:          str
    department:    Optional[str] = None
    material:      Optional[str] = None
    hours:         float = Field(default=0, ge=0, le=1_000_000, allow_inf_nan=False)
    rate_per_hour: float = Field(default=0, ge=0, le=1_000_000, allow_inf_nan=False)
    cost:          float = Field(default=0, ge=0, le=99_999_999.99, allow_inf_nan=False)


class MaterialLine(BaseModel):
    """
    Jedna pozycja materiałowa wyceny (multi-material v3).
    Koszt linii: cost gdy podany (>0), inaczej qty_kg × price_per_kg.
    """
    name:         Optional[str] = None   # nazwa/marka materiału (np. S235)
    material:     Optional[str] = None   # alias akceptowany od starszych klientów
    qty_kg:       float = Field(default=0, ge=0, le=9_999_999.999, allow_inf_nan=False)
    price_per_kg: float = Field(default=0, ge=0, le=1_000_000, allow_inf_nan=False)
    cost:         float = Field(default=0, ge=0, le=99_999_999.99, allow_inf_nan=False)


class QuoteStructuredCreate(BaseModel):
    """
    Strukturalna wycena v3 (cleaner formula).
    Materiał: lista pozycji `materials` (multi-material); gdy pusta — legacy
    pojedyncze pola material_weight_kg × material_price_per_kg.
    Operacje: sum(hours × rate_per_hour), fallback na cost.
    """
    processes:             List[ProcessItem] = Field(default_factory=list)
    method:                Literal["kalkulacja", "od_masy"] = "kalkulacja"
    weight_basis:          Literal["netto", "brutto"] = "netto"
    materials:             List[MaterialLine] = Field(default_factory=list)
    material_weight_kg:    float = Field(default=0, ge=0, le=9_999_999.999, allow_inf_nan=False)
    material_price_per_kg: float = Field(default=0, ge=0, le=1_000_000, allow_inf_nan=False)
    material_cost:         float = Field(default=0, ge=0, le=99_999_999.99, allow_inf_nan=False)
    weight_netto_kg:       float = Field(default=0, ge=0, le=9_999_999.999, allow_inf_nan=False)
    weight_brutto_kg:      float = Field(default=0, ge=0, le=9_999_999.999, allow_inf_nan=False)
    labor_hours:           float = Field(default=0, ge=0, le=1_000_000, allow_inf_nan=False)
    overhead_pct:          float = Field(default=DEFAULT_OVERHEAD_PCT, ge=0, le=1, allow_inf_nan=False)
    margin_pct:            float = Field(default=DEFAULT_MARGIN_PCT, ge=0, le=1, allow_inf_nan=False)
    transport_cost:        float = Field(default=0, ge=0, le=99_999_999.99, allow_inf_nan=False)
    show_unit_prices:      bool  = True
    # Stare pola — backward compat
    weight_kg:             float = Field(default=0, ge=0, le=9_999_999.999, allow_inf_nan=False)
    weight_rate_pln_kg:    float = Field(default=0, ge=0, le=1_000_000, allow_inf_nan=False)


class ManualQuoteCreate(BaseModel):
    """Ręczna cena netto bez liczenia — technolog wpisuje finalną kwotę."""
    total_net: float = Field(ge=0, le=99_999_999.99, allow_inf_nan=False)


class QuoteOut(BaseModel):
    id:            int
    order_id:      int
    labor_hours:   Optional[float]
    material_cost: Optional[float]
    total_net:     Optional[float]
    margin_pct:    Optional[float]
    is_zapor:         bool
    zapor_multiplier: Optional[float] = None
    created_at:       datetime
    # v2 pola
    processes_json:     Optional[List[Any]] = None
    weight_kg:          Optional[float]     = None
    weight_rate_pln_kg: Optional[float]     = None
    welding_hours:      Optional[float]     = None
    weight_netto_kg:    Optional[float]     = None
    weight_brutto_kg:   Optional[float]     = None
    estimate_version:   Optional[str]       = None
    pricing_method:     Optional[str]       = "kalkulacja"
    weight_basis:       Optional[str]       = "netto"
    last_edited_at:     Optional[datetime]  = None
    transport_cost:     Optional[float]     = None
    # v3 pola
    material_weight_kg:    Optional[float] = None
    material_price_per_kg: Optional[float] = None
    materials_json:        Optional[List[Any]] = None
    show_unit_prices:      bool             = True
    model_config = {"from_attributes": True}


# =============================================================================
# PARAMETER REQUESTS — "Zapytaj o parametry" (Technolog → Biuro)
# =============================================================================
class ParameterRequestCreate(BaseModel):
    question_text: str = Field(min_length=1, max_length=2000)

    @field_validator("question_text")
    @classmethod
    def _trim_question(cls, value):
        value = value.strip()
        if not value:
            raise ValueError("Pytanie nie może być puste")
        return value


class ParameterRequestAnswer(BaseModel):
    answer_text: str = Field(min_length=1, max_length=2000)

    @field_validator("answer_text")
    @classmethod
    def _trim_answer(cls, value):
        value = value.strip()
        if not value:
            raise ValueError("Odpowiedź nie może być pusta")
        return value


class ParameterRequestOut(BaseModel):
    id:            int
    order_id:      int
    question_text: str
    answer_text:   Optional[str]
    status:        str
    asked_at:      datetime
    answered_at:   Optional[datetime]
    model_config = {"from_attributes": True}


# =============================================================================
# PRODUCT TEMPLATES — Katalog
# =============================================================================
class TemplateCreate(BaseModel):
    name:               str
    category:           str = "remont"
    operations_json:    List[Any] = Field(default_factory=list)
    materials_json:     List[Any] = Field(default_factory=list)
    instruction_blocks: List[Any] = Field(default_factory=list)
    machines_json:      List[Any] = Field(default_factory=list)
    base_price_pln:     Optional[float] = Field(default=None, ge=0, le=99_999_999.99, allow_inf_nan=False)
    margin_pct:         float = Field(default=0.25, ge=0, le=1, allow_inf_nan=False)
    project_code:       Optional[str] = None
    position_nr:        Optional[str] = None
    notes:              Optional[str] = None


class TemplateOut(BaseModel):
    id:                 int
    name:               str
    category:           str
    operations_json:    List[Any]
    materials_json:     List[Any]
    instruction_blocks: List[Any]
    machines_json:      List[Any]
    base_price_pln:     Optional[float]
    margin_pct:         float
    is_active:          bool
    project_code:       Optional[str] = None
    position_nr:        Optional[str] = None
    notes:              Optional[str] = None
    drawing_path:       Optional[str] = None
    model_config = {"from_attributes": True}


# =============================================================================
# ATTACHMENTS — Załączniki (rysunki, dokumentacja)
# =============================================================================
class AttachmentOut(BaseModel):
    id:          int
    order_id:    int
    filename:    str
    stored_path: str
    size_bytes:  Optional[int]  = None
    mime_type:   Optional[str]  = None
    uploaded_by: Optional[str]  = None
    uploaded_at: datetime
    model_config = {"from_attributes": True}

# OPERATION CATALOG — Słownik Typowych Operacji

class OperationCatalogCreate(BaseModel):
    name:   str
    department: Optional[str] = None
    default_rate: Optional[float] = Field(default=None, ge=0, le=1_000_000, allow_inf_nan=False)
    formula: Optional[str] = None

class OperationCatalogUpdate(BaseModel):
    name: Optional[str] = None
    department: Optional[str] = None
    default_rate: Optional[float] = Field(default=None, ge=0, le=1_000_000, allow_inf_nan=False)
    formula: Optional[str] = None

class OperationCatalogOut(BaseModel):
    id:     int
    name:   str
    department: Optional[str]
    default_rate: Optional[float]
    formula: Optional[str]
    model_config = {"from_attributes": True}


# =============================================================================
# ANALYTICS — Dashboard Dyrektora
# =============================================================================
class RevenueMonth(BaseModel):
    month: str          # "2026-04"
    orders: int
    revenue_pln: float

class TopClient(BaseModel):
    client: str
    orders: int
    revenue_pln: float

class OverdueOrder(BaseModel):
    id: int
    order_number: str
    client: str
    status: str
    deadline: str

class AnalyticsSummary(BaseModel):
    total_orders:     int
    odrzut_count:     int
    odrzut_pct:       float
    standard_count:   int
    niestandard_count: int
    avg_margin_pct:   Optional[float]
    orders_in_production: int
    orders_done:      int
    avg_cycle_days:         Optional[float] = None
    avg_quote_to_start_days: Optional[float] = None
    estimate_accuracy_pct:  Optional[float] = None
    revenue_by_month: List[RevenueMonth] = []
    top_clients:      List[TopClient] = []
    overdue_orders:   List[OverdueOrder] = []


# =============================================================================
# APPROVED MATERIALS — Whitelist materiałów
# =============================================================================
class ApprovedMaterialCreate(BaseModel):
    name:                str
    category:            Optional[str]   = None
    default_rate_pln_kg: Optional[float] = Field(default=None, ge=0, le=1_000_000, allow_inf_nan=False)
    is_active:           bool            = True
    notes:               Optional[str]   = None


class ApprovedMaterialOut(BaseModel):
    id:                  int
    name:                str
    category:            Optional[str]   = None
    default_rate_pln_kg: Optional[float] = None
    is_active:           bool
    notes:               Optional[str]   = None
    model_config = {"from_attributes": True}


# =============================================================================
# BENCHMARK — Analiza cen historycznych (benchmark/cena za kg)
# =============================================================================
class BenchmarkSample(BaseModel):
    """Pojedyncza próbka z historii cen."""
    order_id:    int
    date:        date
    weight_kg:   float
    total_net:   float
    pln_kg:      float


class BenchmarkOut(BaseModel):
    """Benchmark ceny za kg — agregacja historycznych zleceń."""
    avg_pln_kg:  float
    min_pln_kg:  float
    max_pln_kg:  float
    count:       int
    warning:     Optional[str] = None  # "Недостаточно данных" jeśli < 3 próbek
    samples:     List[BenchmarkSample]


# =============================================================================
# ORDER OPERATIONS — actual hours update
# =============================================================================
class ActualHoursUpdate(BaseModel):
    actual_hours: float = Field(ge=0, le=1_000_000, allow_inf_nan=False)


# =============================================================================
# TEMPLATES — Patch schema (partial update)
# =============================================================================
class TemplatePatch(BaseModel):
    name:               Optional[str]       = None
    operations_json:    Optional[List[Any]] = None
    materials_json:     Optional[List[Any]] = None
    instruction_blocks: Optional[List[Any]] = None
    machines_json:      Optional[List[Any]] = None
    base_price_pln:     Optional[float]     = Field(default=None, ge=0, le=99_999_999.99, allow_inf_nan=False)
    notes:              Optional[str]       = None
    margin_pct:         Optional[float]     = Field(default=None, ge=0, le=1, allow_inf_nan=False)
    category:           Optional[str]       = None
    project_code:       Optional[str]       = None
    position_nr:        Optional[str]       = None

    @field_validator(
        "operations_json",
        "materials_json",
        "instruction_blocks",
        "machines_json",
    )
    @classmethod
    def _json_lists_cannot_be_null(cls, value):
        if value is None:
            raise ValueError("Lista nie może być null")
        return value


# =============================================================================
# TYPED PAYLOADS — replaces raw dict at API boundaries
# =============================================================================
class SettingUpdate(BaseModel):
    value: str


class ApprovedMaterialPatch(BaseModel):
    name:                Optional[str]   = None
    category:            Optional[str]   = None
    default_rate_pln_kg: Optional[float] = Field(default=None, ge=0, le=1_000_000, allow_inf_nan=False)
    is_active:           Optional[bool]  = None
    notes:               Optional[str]   = None


class SaveAsTemplatePayload(BaseModel):
    name:     Optional[str] = None
    category: Optional[str] = None


# =============================================================================
# QUOTE PREVIEW — stateless pricing breakdown (no DB write)
# =============================================================================
class QuotePreviewOut(BaseModel):
    ops_total:      float
    material_total: float
    extra_labor:    float
    weight_total:   float = 0
    base:           float
    subtotal:       float
    total_net:      float
    pricing_method: str = "kalkulacja"
    weight_basis:   str = "netto"
    koszt_materialu:     float = 0
    koszt_robocizny:     float = 0
    koszt_wytworzenia:   float = 0
