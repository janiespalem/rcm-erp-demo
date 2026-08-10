from typing import List, Optional

from fastapi import APIRouter, Depends, HTTPException, Response
from sqlalchemy.orm import Session, joinedload

from auth import require_role
from database import get_db
from models import Order, OrderEvent, OrderOperation
from schemas import ActualHoursUpdate, OrderCreate, OrderOut, OrderUpdate
from services import order_service

router = APIRouter(prefix="/api/orders", tags=["Orders"])

_ALL   = require_role("biuro", "technolog", "ceo")
_BIURO = require_role("biuro", "technolog")
_TECH  = require_role("technolog")
_DIR   = require_role("technolog")


@router.post("/", response_model=OrderOut, status_code=201)
def create_order(
    payload: OrderCreate,
    db: Session = Depends(get_db),
    current_user: dict = _BIURO,
) -> Order:
    return order_service.create_order(db, payload, user=current_user)


@router.get("/", response_model=List[OrderOut])
def list_orders(
    status: Optional[str] = None,
    branch: Optional[str] = None,
    archived: bool = False,
    db: Session = Depends(get_db),
    _: dict = _ALL,
) -> list[Order]:
    return order_service.list_orders(db, status=status, branch=branch, archived=archived)


@router.get("/{order_id}", response_model=OrderOut)
def get_order(
    order_id: int,
    db: Session = Depends(get_db),
    _: dict = _ALL,
) -> Order:
    return order_service.get_order_or_404(db, order_id)


@router.delete("/{order_id}", status_code=204)
def delete_order(
    order_id: int,
    db: Session = Depends(get_db),
    _: dict = _DIR,
) -> Response:
    order_service.delete_order(db, order_id)
    return Response(status_code=204)


@router.post("/{order_id}/archive", response_model=OrderOut)
def archive_order(
    order_id: int,
    db: Session = Depends(get_db),
    current_user: dict = _TECH,
) -> Order:
    return order_service.archive_order(db, order_id, user=current_user)


@router.post("/{order_id}/restore", response_model=OrderOut)
def restore_order(
    order_id: int,
    db: Session = Depends(get_db),
    current_user: dict = _TECH,
) -> Order:
    return order_service.restore_order(db, order_id, user=current_user)


@router.patch("/{order_id}", response_model=OrderOut)
def update_order(
    order_id: int,
    data: OrderUpdate,
    db: Session = Depends(get_db),
    current_user: dict = require_role("biuro", "technolog"),
) -> Order:
    return order_service.update_order(db, order_id, data, user=current_user)


@router.post("/{order_id}/confirm", response_model=OrderOut)
def confirm_order(
    order_id: int,
    db: Session = Depends(get_db),
    current_user: dict = _BIURO,
) -> Order:
    return order_service.confirm_order(db, order_id, user=current_user)


@router.post("/{order_id}/complete", response_model=OrderOut)
def complete_order(
    order_id: int,
    db: Session = Depends(get_db),
    current_user: dict = _TECH,
) -> Order:
    return order_service.complete_order(db, order_id, user=current_user)


@router.post("/{order_id}/deliver", response_model=OrderOut)
def deliver_order(
    order_id: int,
    db: Session = Depends(get_db),
    current_user: dict = _BIURO,
) -> Order:
    return order_service.deliver_order(db, order_id, user=current_user)


@router.get("/{order_id}/operations")
def list_operations(
    order_id: int,
    db: Session = Depends(get_db),
    _: dict = _ALL,
):
    order = (
        db.query(Order)
        .options(joinedload(Order.operations).joinedload(OrderOperation.catalog_entry))
        .filter(Order.id == order_id)
        .first()
    )
    if not order:
        raise HTTPException(status_code=404, detail="Zlecenie nie znalezione")
    return [
        {
            "id":           op.id,
            "name":         op.catalog_entry.name if op.catalog_entry else f"Op #{op.id}",
            "department":   op.catalog_entry.department if op.catalog_entry else None,
            "responsible":  op.responsible,
            "sequence":     op.sequence,
            "status":       op.status,
            "actual_hours": float(op.actual_hours) if op.actual_hours is not None else None,
        }
        for op in sorted(order.operations, key=lambda x: x.sequence or 0)
    ]


@router.patch("/{order_id}/operations/{op_id}/actual-hours")
def set_actual_hours(
    order_id: int,
    op_id: int,
    payload: ActualHoursUpdate,
    db: Session = Depends(get_db),
    current_user: dict = _TECH,
):
    order = order_service.get_mutable_order_or_404(db, order_id)
    op = db.get(OrderOperation, op_id)
    if not op or op.order_id != order_id:
        raise HTTPException(status_code=404, detail="Operacja nie znaleziona")
    op.actual_hours = payload.actual_hours
    order_service._log_event(
        db,
        order,
        "actual_hours_updated",
        user=current_user,
        note=f"Operacja {op.id}: {payload.actual_hours} h",
    )
    db.commit()
    return {"id": op.id, "actual_hours": float(op.actual_hours)}


@router.get("/{order_id}/events")
def list_order_events(
    order_id: int,
    db: Session = Depends(get_db),
    _: dict = _ALL,
):
    order_service.get_order_or_404(db, order_id)
    events = (
        db.query(OrderEvent)
        .filter(OrderEvent.order_id == order_id)
        .order_by(OrderEvent.created_at.asc())
        .all()
    )
    return [
        {
            "id":         e.id,
            "event_type": e.event_type,
            "old_status": e.old_status,
            "new_status": e.new_status,
            "user_name":  e.user_name,
            "user_role":  e.user_role,
            "note":       e.note,
            "created_at": e.created_at.isoformat() if e.created_at else None,
        }
        for e in events
    ]
