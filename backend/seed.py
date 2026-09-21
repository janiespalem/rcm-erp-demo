"""Small, synthetic dataset for the public portfolio demo."""

from datetime import date, datetime, timedelta, timezone

import bcrypt
from sqlalchemy.orm import Session

from models import (
    ApprovedMaterial,
    OperationCatalog,
    Order,
    OrderEvent,
    OrderStatus,
    ProductTemplate,
    Quote,
    Setting,
    TriageBranch,
    User,
    UserRole,
)


_DEMO_USERS = (
    ("Demo Office", UserRole.biuro, "1111", None),
    ("Demo Technologist", UserRole.technolog, "2222", None),
    ("Demo CEO", UserRole.ceo, "3333", None),
    ("Demo Shift I", UserRole.produkcja, "4444", "I"),
    ("Demo Shift II", UserRole.produkcja, "5555", "II"),
)


def _pin_hash(pin: str) -> str:
    return bcrypt.hashpw(pin.encode(), bcrypt.gensalt()).decode()


def seed_demo_data(db: Session) -> None:
    """Populate an empty database with idempotent, entirely fictional records."""
    # Add new demo identities on upgrades without resetting existing credentials.
    for name, role, pin, shift in _DEMO_USERS:
        if not db.query(User).filter_by(name=name, role=role).first():
            db.add(User(name=name, role=role, pin_hash=_pin_hash(pin), default_shift=shift))

    settings = (
        ("labor_rate_pln", "95", "Stawka robocizny (PLN/h)"),
        ("min_order_value", "500", "Minimalna wartość zlecenia (PLN)"),
        ("default_overhead_pct", "0.10", "Narzut overhead (%)"),
        ("default_margin_pct", "0.25", "Domyślna marża (%)"),
        ("company_name", "DemoFab", "Nazwa firmy"),
        ("company_address", "ul. Przykładowa 10, 00-001 Warszawa", "Adres firmy"),
        ("company_nip", "000-000-00-00", "NIP firmy"),
        ("company_regon", "000000000", "REGON firmy"),
        ("company_tagline", "Manufacturing workflow demo", "Tagline firmy"),
        ("vat_rate", "0.23", "Stawka VAT"),
    )
    for key, value, label in settings:
        if db.get(Setting, key) is None:
            db.add(Setting(key=key, value=value, label=label))

    if db.query(ApprovedMaterial).count() == 0:
        db.add_all(
            (
                ApprovedMaterial(name="Demo Steel A", category="steel", default_rate_pln_kg=4.20),
                ApprovedMaterial(name="Demo Steel B", category="steel", default_rate_pln_kg=4.80),
                ApprovedMaterial(name="Demo Bar 12", category="reinforcement", default_rate_pln_kg=3.90),
            )
        )

    if db.query(OperationCatalog).count() == 0:
        db.add_all(
            (
                OperationCatalog(name="Laser cutting", department="CNC", default_rate=95, formula="cut sheet profile"),
                OperationCatalog(name="Welding", department="Assembly", default_rate=110, formula="weld frame repair"),
                OperationCatalog(name="Assembly", department="Assembly", default_rate=90, formula="fit assemble"),
                OperationCatalog(name="Quality check", department="QC", default_rate=85, formula="measure inspect"),
            )
        )

    if db.query(ProductTemplate).count() == 0:
        db.add_all(
            (
                ProductTemplate(
                    name="Demo support frame",
                    category="fabrication",
                    operations_json=[{"op": "Laser cutting", "hours": 1.5}, {"op": "Welding", "hours": 2.0}],
                    materials_json=[{"mat": "Demo Steel A", "qty": 42, "unit": "kg"}],
                    instruction_blocks=[{"order": 1, "text": "Verify dimensions"}, {"order": 2, "text": "Assemble and inspect"}],
                    machines_json=[],
                    base_price_pln=1450,
                    margin_pct=0.25,
                ),
                ProductTemplate(
                    name="Demo machine guard",
                    category="fabrication",
                    operations_json=[{"op": "Laser cutting", "hours": 0.8}, {"op": "Assembly", "hours": 1.2}],
                    materials_json=[{"mat": "Demo Steel B", "qty": 18, "unit": "kg"}],
                    instruction_blocks=[{"order": 1, "text": "Cut synthetic sample parts"}],
                    machines_json=[],
                    base_price_pln=760,
                    margin_pct=0.25,
                ),
            )
        )

    db.flush()
    if db.query(Order).count() == 0:
        users = {user.role: user for user in db.query(User).all()}
        today = date.today()
        orders = (
            Order(
                order_number="DEMO-001",
                client="Northwind Workshop",
                status=OrderStatus.standard,
                triage_branch=TriageBranch.standard,
                deadline=today + timedelta(days=7),
                description="Protective machine cover",
                material="Demo Steel A",
                weight_kg=42,
                dimensions="800 × 450 × 320 mm",
                contact="Alex Demo, +00 000 000 001",
                created_by_id=users[UserRole.biuro].id,
            ),
            Order(
                order_number="DEMO-002",
                client="Sample Robotics",
                status=OrderStatus.quoted,
                triage_branch=TriageBranch.niestandard,
                deadline=today + timedelta(days=12),
                description="Prototype support frame",
                material="Demo Steel B",
                weight_kg=68,
                dimensions="1200 × 600 × 500 mm",
                created_by_id=users[UserRole.biuro].id,
                assigned_to_id=users[UserRole.technolog].id,
            ),
            Order(
                order_number="DEMO-003",
                client="DemoFab Internal",
                status=OrderStatus.in_production,
                triage_branch=TriageBranch.standard,
                deadline=today + timedelta(days=4),
                description="Internal transport rack",
                material="Demo Steel A",
                weight_kg=125,
                is_internal=True,
                created_by_id=users[UserRole.biuro].id,
            ),
            Order(
                order_number="DEMO-004",
                client="Example Marine",
                status=OrderStatus.wydane,
                triage_branch=TriageBranch.standard,
                deadline=today - timedelta(days=3),
                description="Finished inspection platform",
                material="Demo Steel B",
                weight_kg=210,
                delivered_at=datetime.now(timezone.utc).replace(tzinfo=None),
                created_by_id=users[UserRole.biuro].id,
            ),
        )
        db.add_all(orders)
        db.flush()

        quoted = orders[1]
        db.add(
            Quote(
                order_id=quoted.id,
                pricing_method="kalkulacja",
                weight_basis="netto",
                weight_kg=68,
                materials_json=[{"name": "Demo Steel B", "qty_kg": 68, "price_per_kg": 4.8, "cost": 326.4}],
                processes_json=[{"name": "Laser cutting", "hours": 1.5, "rate_per_hour": 95, "cost": 142.5}, {"name": "Welding", "hours": 3, "rate_per_hour": 110, "cost": 330}],
                material_cost=326.4,
                overhead_pct=0.10,
                margin_pct=0.25,
                transport_cost=120,
                total_net=1225.50,
            )
        )
        for order in orders:
            db.add(
                OrderEvent(
                    order_id=order.id,
                    user_role="biuro",
                    user_name="Demo Office",
                    event_type="created",
                    new_status=order.status.value,
                    note="Synthetic portfolio record",
                )
            )

    db.commit()
