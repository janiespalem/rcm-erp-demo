from datetime import date
from typing import Optional

from fastapi import HTTPException
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from models import (
    ComponentContainer,
    DocType,
    MaterialRequest,
    Order,
    OrderAttachment,
    OrderCounter,
    OrderEvent,
    OrderOperation,
    OrderStatus,
    ParameterRequest,
    PriceHistory,
    QualityCard,
    Quote,
    StockMovement,
    TechCard,
)
from schemas import OrderCreate, OrderUpdate
from utils import _now


def _log_event(
    db: Session,
    order: Order,
    event_type: str,
    old_status: Optional[str] = None,
    new_status: Optional[str] = None,
    user: Optional[dict] = None,
    note: Optional[str] = None,
) -> None:
    db.add(OrderEvent(
        order_id   = order.id,
        user_role  = user["role"] if user else None,
        user_name  = user["name"] if user else None,
        event_type = event_type,
        old_status = old_status,
        new_status = new_status,
        note       = note,
        created_at = _now(),
    ))


def _legacy_max_seq(db: Session, year: int) -> int:
    """Scan existing orders to find highest sequence for the given year."""
    rows = (
        db.query(Order.order_number)
        .filter(Order.order_number.like(f"%/{year}"))
        .all()
    )
    max_seq = 0
    for (number,) in rows:
        if not number:
            continue
        prefix, _, suffix = number.partition("/")
        if suffix == str(year) and prefix.isdigit():
            max_seq = max(max_seq, int(prefix))
    return max_seq


def _is_postgres(db: Session) -> bool:
    try:
        return db.get_bind().dialect.name == "postgresql"
    except Exception:
        return False


def generate_order_number(db: Session) -> str:
    year = date.today().year

    for _ in range(3):
        use_lock = _is_postgres(db)
        query = db.query(OrderCounter).filter(OrderCounter.year == year)
        if use_lock:
            query = query.with_for_update()
        counter = query.first()

        if counter is None:
            # First order of the year — seed from existing numbers
            max_seq = _legacy_max_seq(db, year)
            counter = OrderCounter(year=year, next_seq=max_seq + 1)
            db.add(counter)
            try:
                db.flush()
            except IntegrityError:
                # Another concurrent transaction created the row — retry
                db.rollback()
                continue

        counter.next_seq = max(counter.next_seq, _legacy_max_seq(db, year) + 1)
        seq = counter.next_seq
        counter.next_seq += 1
        db.flush()
        return f"{seq}/{year}"

    raise HTTPException(status_code=500, detail="Nie udało się wygenerować numeru zlecenia")


def get_order_or_404(db: Session, order_id: int) -> Order:
    order = db.get(Order, order_id)
    if not order:
        raise HTTPException(status_code=404, detail="Zlecenie nie znalezione")
    return order


def get_mutable_order_or_404(db: Session, order_id: int) -> Order:
    order = get_order_or_404(db, order_id)
    if order.archived_at:
        raise HTTPException(status_code=409, detail="Zarchiwizowane zlecenie jest tylko do odczytu")
    return order


def get_mutable_order_for_update(db: Session, order_id: int) -> Order:
    order = (
        db.query(Order)
        .filter(Order.id == order_id)
        .with_for_update()
        .first()
    )
    if not order:
        raise HTTPException(status_code=404, detail="Zlecenie nie znalezione")
    if order.archived_at:
        raise HTTPException(status_code=409, detail="Zarchiwizowane zlecenie jest tylko do odczytu")
    return order


def ensure_order_number_available(
    db: Session,
    order_number: str,
    current_order_id: int | None = None,
) -> str:
    clean_number = order_number.strip()
    if not clean_number:
        raise HTTPException(status_code=422, detail="Numer zlecenia nie może być pusty")

    query = db.query(Order).filter(Order.order_number == clean_number)
    if current_order_id is not None:
        query = query.filter(Order.id != current_order_id)
    if query.first():
        raise HTTPException(status_code=409, detail=f"Numer zlecenia '{clean_number}' już istnieje")
    return clean_number


def delete_order_dependents(db: Session, order: Order) -> None:
    operation_ids = [
        row for (row,) in
        db.query(OrderOperation.id).filter(OrderOperation.order_id == order.id).all()
    ]
    if operation_ids:
        db.query(TechCard).filter(TechCard.operation_id.in_(operation_ids)).delete(synchronize_session=False)
    db.query(OrderOperation).filter(OrderOperation.order_id == order.id).delete(synchronize_session=False)

    dependent_models = (
        QualityCard,
        MaterialRequest,
        Quote,
        OrderAttachment,
        ParameterRequest,
        StockMovement,
        ComponentContainer,
    )
    for model in dependent_models:
        db.query(model).filter(model.order_id == order.id).delete(synchronize_session=False)


def create_order(db: Session, payload: OrderCreate, user: Optional[dict] = None) -> Order:
    for attempt in range(3):
        order_number = (
            ensure_order_number_available(db, payload.order_number)
            if payload.order_number
            else generate_order_number(db)
        )
        order = Order(
            order_number=order_number,
            client=payload.client,
            deadline=payload.deadline,
            approved_material_id=payload.approved_material_id,
            material=payload.material,
            materials_json=payload.materials_json,
            has_drawing=payload.has_drawing,
            notes=payload.notes,
            purpose=payload.purpose,
            estimated_value=payload.estimated_value,
            order_type=payload.order_type,
            sop_name=payload.sop_name,
            description=payload.description,
            requires_visit=payload.requires_visit,
            template_id=payload.template_id,
            quantity=payload.quantity,
            is_defence=payload.is_defence,
            is_internal=payload.is_internal,
            weight_kg=payload.weight_kg,
            drawing_number=payload.drawing_number,
            dimensions=payload.dimensions,
            delivery_address=payload.delivery_address,
            contact=payload.contact,
            status=OrderStatus.draft,
        )
        db.add(order)
        try:
            db.flush()
        except IntegrityError:
            db.rollback()
            if attempt == 2 or payload.order_number:
                raise HTTPException(status_code=409, detail=f"Numer zlecenia '{order_number}' już istnieje")
            continue
        _log_event(db, order, "created", new_status="draft", user=user)
        db.commit()
        db.refresh(order)
        return order
    raise HTTPException(status_code=500, detail="Nie udało się wygenerować unikalnego numeru zlecenia")


def list_orders(
    db: Session,
    status: Optional[str] = None,
    branch: Optional[str] = None,
    archived: bool = False,
) -> list[Order]:
    query = db.query(Order).filter(
        Order.archived_at.isnot(None) if archived else Order.archived_at.is_(None)
    )
    if status:
        query = query.filter(Order.status == status)
    if branch:
        query = query.filter(Order.triage_branch == branch)
    return query.order_by(Order.created_at.desc()).all()


_FIELD_LABELS_PL = {
    "client": "klient",
    "deadline": "termin",
    "material": "materiał",
    "quantity": "ilość",
    "description": "opis",
    "weight_kg": "masa",
    "notes": "uwagi",
    "order_type": "typ zlecenia",
    "sop_name": "usługa",
    "purpose": "przeznaczenie",
    "has_drawing": "rysunek",
    "requires_visit": "wizyta",
    "drawing_number": "nr rysunku",
    "dimensions": "wymiary",
    "delivery_address": "adres dostawy",
    "contact": "kontakt",
    "estimated_value": "szacunkowa wartość",
    "order_number": "numer zlecenia",
}

# Fields that don't warrant a note entry (internal/system fields)
_SKIP_EDIT_FIELDS = {"approved_material_id", "template_id", "is_defence"}


def update_order(
    db: Session,
    order_id: int,
    payload: OrderUpdate,
    user: Optional[dict] = None,
) -> Order:
    order = get_mutable_order_or_404(db, order_id)
    update_data = payload.model_dump(exclude_unset=True)
    if "order_number" in update_data and update_data["order_number"] is not None:
        update_data["order_number"] = ensure_order_number_available(
            db,
            update_data["order_number"],
            current_order_id=order.id,
        )

    for field, value in update_data.items():
        setattr(order, field, value)

    meaningful_keys = [k for k in update_data if k not in _SKIP_EDIT_FIELDS]
    if meaningful_keys:
        labels = [_FIELD_LABELS_PL.get(k, k) for k in meaningful_keys]
        _log_event(db, order, "edited", user=user, note=", ".join(labels))

    try:
        db.commit()
    except IntegrityError:
        db.rollback()
        raise HTTPException(status_code=409, detail="Numer zlecenia jest już używany")
    db.refresh(order)
    return order


def delete_order(db: Session, order_id: int) -> None:
    order = get_mutable_order_or_404(db, order_id)
    if order.status != OrderStatus.draft:
        raise HTTPException(status_code=409, detail="Usunąć można tylko pusty szkic zlecenia")
    dependent_models = (
        OrderOperation,
        Quote,
        OrderAttachment,
        ParameterRequest,
        MaterialRequest,
        QualityCard,
        StockMovement,
        ComponentContainer,
    )
    if any(
        db.query(model).filter(model.order_id == order.id).first()
        for model in dependent_models
    ):
        raise HTTPException(status_code=409, detail="Szkic ma powiązane dane — zamiast usuwać, odrzuć go")
    delete_order_dependents(db, order)
    db.delete(order)
    db.commit()


def archive_order(db: Session, order_id: int, user: Optional[dict] = None) -> Order:
    order = get_order_or_404(db, order_id)
    if order.status not in (OrderStatus.wydane, OrderStatus.rejected):
        raise HTTPException(status_code=409, detail="Archiwizować można tylko zakończone lub odrzucone zlecenie")
    if not order.archived_at:
        order.archived_at = _now()
        _log_event(db, order, "archived", user=user)
        db.commit()
        db.refresh(order)
    return order


def restore_order(db: Session, order_id: int, user: Optional[dict] = None) -> Order:
    order = get_order_or_404(db, order_id)
    if order.archived_at:
        order.archived_at = None
        _log_event(db, order, "restored", user=user)
        db.commit()
        db.refresh(order)
    return order


def confirm_order(db: Session, order_id: int, user: Optional[dict] = None) -> Order:
    order = get_mutable_order_for_update(db, order_id)
    if order.status != OrderStatus.quoted:
        raise HTTPException(status_code=409, detail="Tylko zlecenia w statusie 'quoted' można zatwierdzić")

    quote = db.query(Quote).filter(Quote.order_id == order_id).first()
    if quote and quote.total_net:
        weight_kg = float(quote.material_weight_kg or quote.weight_kg or 0)
        if weight_kg > 0:
            db.add(
                PriceHistory(
                    order_type=order.sop_name or order.order_type,
                    total_price_historical=quote.total_net,
                    parameters_json={
                        "weight_kg": weight_kg,
                        "pln_kg": float(quote.total_net) / weight_kg,
                        "material": order.material,
                    },
                    order_date=date.today(),
                    client=order.client,
                )
            )

    old_status = order.status.value if order.status else None
    order.status = OrderStatus.in_production
    if not order.quoted_at:
        order.quoted_at = _now()
    # Confirming now puts the order straight into production (the separate
    # "start production" step was merged away), so record started_at here.
    if not order.started_at:
        order.started_at = _now()
    _log_event(db, order, "confirmed", old_status=old_status, new_status="in_production", user=user)
    db.commit()
    db.refresh(order)
    return order


def _emit_material_rozchod(db: Session, order: Order) -> None:
    """Automatyczny rozchód materiału przy zamknięciu produkcji (z katalogu)."""
    if not order.approved_material_id:
        return
    if db.query(StockMovement).filter(
        StockMovement.order_id == order.id,
        StockMovement.material_id == order.approved_material_id,
        StockMovement.doc_type == DocType.rozchod,
    ).first():
        return
    quote = db.query(Quote).filter(Quote.order_id == order.id).first()
    material_weight = float(
        (quote.material_weight_kg or quote.weight_kg or 0) if quote else 0
    )
    if material_weight <= 0:
        return
    db.add(
        StockMovement(
            doc_type=DocType.rozchod,
            order_id=order.id,
            item_name=f"Automatyczny rozchod materialow dla zlecenia: {order.order_number}",
            qty=material_weight,
            unit="kg",
            material_id=order.approved_material_id,
        )
    )


def complete_order(db: Session, order_id: int, user: Optional[dict] = None) -> Order:
    """Legacy: in_production → gotowe. Nowy przepływ zamyka jednym krokiem (deliver)."""
    order = get_mutable_order_for_update(db, order_id)
    if order.status != OrderStatus.in_production:
        raise HTTPException(status_code=409, detail="Tylko zlecenia 'in_production' można oznaczyć jako gotowe")

    _emit_material_rozchod(db, order)
    old_status = order.status.value
    order.status = OrderStatus.gotowe
    order.completed_at = _now()
    _log_event(db, order, "completed", old_status=old_status, new_status="gotowe", user=user)
    db.commit()
    db.refresh(order)
    return order


def deliver_order(db: Session, order_id: int, user: Optional[dict] = None) -> Order:
    """Zakończenie zlecenia → wydane. Jeden krok z produkcji (in_production) LUB
    z legacy 'gotowe'. Z produkcji dodatkowo robi rozchód materiału i completed_at."""
    order = get_mutable_order_for_update(db, order_id)
    if order.status not in (OrderStatus.gotowe, OrderStatus.in_production):
        raise HTTPException(status_code=409, detail="Tylko zlecenia 'gotowe' lub 'in_production' można zakończyć")
    old_status = order.status.value
    if order.status == OrderStatus.in_production:
        if not user or user.get("role") != "technolog":
            raise HTTPException(status_code=403, detail="Zlecenie w produkcji może zakończyć tylko technolog")
        _emit_material_rozchod(db, order)
        if not order.completed_at:
            order.completed_at = _now()
    order.status = OrderStatus.wydane
    order.delivered_at = _now()
    _log_event(db, order, "delivered", old_status=old_status, new_status="wydane", user=user)
    db.commit()
    db.refresh(order)
    return order
