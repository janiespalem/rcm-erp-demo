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


def list_orders(db: Session, status: Optional[str] = None, branch: Optional[str] = None) -> list[Order]:
    query = db.query(Order)
    if status:
        query = query.filter(Order.status == status)
    if branch:
        query = query.filter(Order.triage_branch == branch)
    return query.order_by(Order.created_at.desc()).all()


def update_order(db: Session, order_id: int, payload: OrderUpdate) -> Order:
    order = get_order_or_404(db, order_id)
    update_data = payload.model_dump(exclude_unset=True)
    if "order_number" in update_data and update_data["order_number"] is not None:
        update_data["order_number"] = ensure_order_number_available(
            db,
            update_data["order_number"],
            current_order_id=order.id,
        )

    for field, value in update_data.items():
        setattr(order, field, value)
    db.commit()
    db.refresh(order)
    return order


def archive_order(db: Session, order_id: int) -> None:
    order = get_order_or_404(db, order_id)
    delete_order_dependents(db, order)
    db.delete(order)
    db.commit()


def confirm_order(db: Session, order_id: int, user: Optional[dict] = None) -> Order:
    order = get_order_or_404(db, order_id)
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
    _log_event(db, order, "confirmed", old_status=old_status, new_status="in_production", user=user)
    db.commit()
    db.refresh(order)
    return order


def start_order(db: Session, order_id: int, user: Optional[dict] = None) -> Order:
    order = get_order_or_404(db, order_id)
    if order.status != OrderStatus.in_production:
        raise HTTPException(status_code=409, detail="Tylko zlecenia 'in_production' można rozpocząć")
    old_status = order.status.value
    order.status = OrderStatus.w_trakcie
    order.started_at = _now()
    _log_event(db, order, "started", old_status=old_status, new_status="w_trakcie", user=user)
    db.commit()
    db.refresh(order)
    return order


def complete_order(db: Session, order_id: int, user: Optional[dict] = None) -> Order:
    order = get_order_or_404(db, order_id)
    if order.status != OrderStatus.w_trakcie:
        raise HTTPException(status_code=409, detail="Tylko zlecenia 'w_trakcie' można oznaczyć jako gotowe")

    if order.approved_material_id and any(operation.catalog_id for operation in order.operations):
        quote = db.query(Quote).filter(Quote.order_id == order.id).first()
        material_weight = float(quote.material_weight_kg) if (quote and quote.material_weight_kg) else 1.0
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

    old_status = order.status.value
    order.status = OrderStatus.gotowe
    order.completed_at = _now()
    _log_event(db, order, "completed", old_status=old_status, new_status="gotowe", user=user)
    db.commit()
    db.refresh(order)
    return order


def deliver_order(db: Session, order_id: int, user: Optional[dict] = None) -> Order:
    order = get_order_or_404(db, order_id)
    if order.status != OrderStatus.gotowe:
        raise HTTPException(status_code=409, detail="Tylko zlecenia 'gotowe' można oznaczyć jako wydane")
    old_status = order.status.value
    order.status = OrderStatus.wydane
    order.delivered_at = _now()
    _log_event(db, order, "delivered", old_status=old_status, new_status="wydane", user=user)
    db.commit()
    db.refresh(order)
    return order
