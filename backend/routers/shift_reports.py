"""Concrete-plant shift aggregate: draft writes and atomic, audited corrections."""
from datetime import date, datetime, timezone
import json
from pathlib import Path
from typing import Literal
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends, HTTPException, Query, Response
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session
from sqlalchemy.orm.exc import StaleDataError

from auth import require_role
from database import get_db
from models import ShiftReport, ShiftReportAudit

router = APIRouter(prefix="/api/shift-reports", tags=["Raporty zmianowe"])
READ = require_role("produkcja", "technolog", "ceo", "biuro")
WRITE = require_role("produkcja", "technolog")
Check = Literal["OK", "NIE", "N/D"]
# Published schemas are immutable. New questions/order require a new version.
REPORT_SCHEMAS = json.loads((Path(__file__).resolve().parents[2] / "shared/shift-report-schemas.json").read_text())


def can_admin_delete(user: dict) -> bool:
    return user["role"] in ("technolog", "ceo")


ADMIN = require_role("technolog", "ceo")


class Equipment(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)
    condition: Literal["sprawne", "niesprawne"] | None = None
    reason: str = Field(default="", max_length=1000)
    note: str = Field(default="", max_length=1000)


class ShiftFields(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)
    leader: str = Field(default="", max_length=100)
    responsible: str = Field(default="", max_length=100)
    people: int | None = Field(default=None, ge=1, le=1000, strict=True)
    reference: str = Field(default="", max_length=200)
    assembled: int | None = Field(default=None, ge=0, le=1000000, strict=True)
    prepared: int | None = Field(default=None, ge=0, le=1000000, strict=True)
    poured: int | None = Field(default=None, ge=0, le=1000000, strict=True)
    checked: int | None = Field(default=None, ge=0, le=1000000, strict=True)
    demoulded: int | None = Field(default=None, ge=0, le=1000000, strict=True)
    damaged: int | None = Field(default=None, ge=0, le=1000000, strict=True)
    damage_reason: str = Field(default="", max_length=2000)
    vibrators: Equipment = Field(default_factory=Equipment)
    extensions: Equipment = Field(default_factory=Equipment)
    checks: list[Check | None] = Field(default_factory=lambda: [None] * 9, min_length=9, max_length=9)
    corrected_work: str = Field(default="", max_length=4000)
    remaining_work: str = Field(default="", max_length=4000)
    work_owner: str = Field(default="", max_length=100)
    remarks: str = Field(default="", max_length=4000)
    production_person: str = Field(default="", max_length=100)
    controller: str = Field(default="", max_length=100)


class CreateReport(BaseModel):
    report_date: date
    shift: Literal["I", "II"]


class Version(BaseModel):
    version_id: int = Field(ge=1)


class SaveReport(Version):
    fields: ShiftFields


class Correction(SaveReport):
    reason: str = Field(min_length=1, max_length=2000)


class AdminDelete(Version):
    model_config = ConfigDict(str_strip_whitespace=True, extra="forbid")
    reason: str = Field(min_length=1, max_length=2000)


def validation(fields: ShiftFields) -> dict:
    errors = {}
    for key in ("leader", "responsible", "people", "assembled", "prepared", "poured", "checked", "demoulded", "damaged", "production_person", "controller"):
        if getattr(fields, key) is None or getattr(fields, key) == "":
            errors[key] = "Uzupełnij pole przed zakończeniem raportu."
    if fields.damaged and not fields.damage_reason:
        errors["damage_reason"] = "Podaj powód uszkodzenia."
    for key in ("vibrators", "extensions"):
        equipment = getattr(fields, key)
        if equipment.condition is None:
            errors[f"{key}.condition"] = "Wybierz stan sprzętu."
        if equipment.condition == "niesprawne" and not equipment.reason:
            errors[f"{key}.reason"] = "Podaj powód usterki lub osobę odpowiedzialną."
    for i, value in enumerate(fields.checks):
        if value is None:
            errors[f"checks.{i}"] = "Wybierz OK, NIE lub N/D."
    if "NIE" in fields.checks and not (fields.corrected_work or fields.remaining_work):
        errors["corrected_work"] = "Wyjaśnij wynik NIE: co poprawiono lub co zostało do wykonania."
    if fields.remaining_work and not fields.work_owner:
        errors["work_owner"] = "Wskaż osobę odpowiedzialną za pozostałe prace."
    return errors


def snapshot(report: ShiftReport) -> dict:
    return {"status": report.status, "version_id": report.version_id, "schema_version": report.schema_version, "fields": report.fields,
            "deleted_at": report.deleted_at.isoformat() if report.deleted_at else None}


def output(report: ShiftReport) -> dict:
    if str(report.schema_version) not in REPORT_SCHEMAS:
        raise HTTPException(409, "Nieobsługiwana wersja formularza. Zaktualizuj aplikację.")
    fields = ShiftFields.model_validate(report.fields)
    return {key: getattr(report, key) for key in (
        "id", "report_date", "shift", "author_id", "author_name", "status", "version_id", "schema_version", "fields",
        "created_at", "updated_at", "finalized_at", "finalized_by_id", "finalized_by_name", "correction_count", "deleted_at",
    )} | {"warning": "NIE" in fields.checks or any(e.condition == "niesprawne" for e in (fields.vibrators, fields.extensions)) or bool(fields.remaining_work),
         "validation_errors": validation(fields)}


def get_report(db: Session, report_id: int, include_deleted: bool = False) -> ShiftReport:
    report = db.get(ShiftReport, report_id)
    if report is None or (report.deleted_at is not None and not include_deleted):
        raise HTTPException(404, "Raport nie istnieje.")
    if str(report.schema_version) not in REPORT_SCHEMAS:
        raise HTTPException(409, "Nieobsługiwana wersja formularza. Zaktualizuj aplikację.")
    return report


def check_write(report: ShiftReport, user: dict, version: int, draft: bool = True):
    if report.deleted_at is not None:
        raise HTTPException(409, "Raport został usunięty przez administratora.")
    if user["role"] == "produkcja" and report.author_id != int(user["id"]):
        raise HTTPException(403, "Możesz zmieniać tylko własny raport.")
    if report.version_id != version:
        raise HTTPException(409, "Raport został zmieniony w innym oknie. Twoje dane pozostają w formularzu. Porównaj z aktualną wersją.")
    if draft and report.status != "draft":
        raise HTTPException(409, "Raport jest zakończony. Użyj Koryguj raport.")


def commit(db: Session):
    try:
        db.commit()
    except StaleDataError:
        db.rollback()
        raise HTTPException(409, "Raport został zmieniony. Zachowaj swoje dane i porównaj z aktualną wersją.")


def record(db: Session, report: ShiftReport, user: dict, action: str, before=None, reason=None):
    try:
        db.flush()
    except StaleDataError:
        db.rollback()
        raise HTTPException(409, "Raport został zmieniony. Twoje dane pozostają w formularzu.")
    after = snapshot(report)
    previous = None
    if action == "saved":
        # The successful versioned report flush above serializes writers before
        # this query. Coalesce only new-format consecutive saves by one actor;
        # preserve all legacy audit records and every finalized/corrected event.
        previous = db.scalar(select(ShiftReportAudit).where(ShiftReportAudit.report_id == report.id)
                             .order_by(ShiftReportAudit.id.desc()).limit(1))
        if not (previous and previous.action == "saved" and previous.actor_id == int(user["id"])
                and previous.after.get("schema_version") == report.schema_version
                and previous.after.get("draft_save_count")):
            previous = None
        after["draft_save_count"] = previous.after["draft_save_count"] + 1 if previous else 1
        after["last_saved_at"] = report.updated_at.isoformat()
    if previous:
        previous.after = after
    else:
        db.add(ShiftReportAudit(report_id=report.id, actor_id=int(user["id"]), actor_name=user["name"],
                                action=action, reason=reason, before=before, after=after))
    commit(db)


@router.get("")
def list_reports(date_from: date | None = None, date_to: date | None = None,
                 shift: Literal["I", "II"] | None = None, offset: int = Query(0, ge=0),
                 deleted: bool = False,
                 db: Session = Depends(get_db), user: dict = READ):
    if deleted and not can_admin_delete(user):
        raise HTTPException(403, "Usunięte raporty są dostępne tylko dla administratora.")
    query = select(ShiftReport).where(ShiftReport.deleted_at.is_not(None) if deleted else ShiftReport.deleted_at.is_(None))
    if date_from:
        query = query.where(ShiftReport.report_date >= date_from)
    if date_to:
        query = query.where(ShiftReport.report_date <= date_to)
    if shift:
        query = query.where(ShiftReport.shift == shift)
    return [output(r) for r in db.scalars(query.order_by(ShiftReport.report_date.desc(), ShiftReport.shift).offset(offset).limit(100))]


@router.get("/today")
def today(db: Session = Depends(get_db), user: dict = READ):
    day = datetime.now(ZoneInfo("Europe/Warsaw")).date()
    return {"date": day, "can_admin_delete": can_admin_delete(user), "reports": [output(r) for r in db.scalars(select(ShiftReport).where(ShiftReport.report_date == day, ShiftReport.deleted_at.is_(None)))]}


@router.post("")
def create(payload: CreateReport, db: Session = Depends(get_db), user: dict = WRITE):
    existing = db.scalar(select(ShiftReport).where(ShiftReport.report_date == payload.report_date, ShiftReport.shift == payload.shift, ShiftReport.deleted_at.is_(None)))
    if existing:
        return output(existing)
    fields = ShiftFields(leader=user["name"], responsible=user["name"], production_person=user["name"])
    report = ShiftReport(report_date=payload.report_date, shift=payload.shift, author_id=int(user["id"]), author_name=user["name"], fields=fields.model_dump())
    db.add(report)
    try:
        record(db, report, user, "created")
    except IntegrityError:
        db.rollback()
        raise HTTPException(409, "Raport dla tej daty i zmiany już istnieje. Otwórz go w historii.")
    return output(report)


@router.get("/{report_id}")
def detail(report_id: int, db: Session = Depends(get_db), user: dict = READ):
    return output(get_report(db, report_id, include_deleted=can_admin_delete(user)))


@router.put("/{report_id}")
def save(report_id: int, payload: SaveReport, db: Session = Depends(get_db), user: dict = WRITE):
    report = get_report(db, report_id)
    check_write(report, user, payload.version_id)
    before = snapshot(report)
    report.fields = payload.fields.model_dump()
    report.updated_at = datetime.now(timezone.utc)
    record(db, report, user, "saved", before)
    return output(report)


@router.post("/{report_id}/finalize")
def finalize(report_id: int, payload: Version, db: Session = Depends(get_db), user: dict = WRITE):
    report = get_report(db, report_id)
    check_write(report, user, payload.version_id)
    errors = validation(ShiftFields.model_validate(report.fields))
    if errors:
        raise HTTPException(422, {"message": "Uzupełnij zaznaczone pola.", "fields": errors})
    before = snapshot(report)
    report.status = "finalized"
    report.finalized_at = report.updated_at = datetime.now(timezone.utc)
    report.finalized_by_id, report.finalized_by_name = int(user["id"]), user["name"]
    record(db, report, user, "finalized", before)
    return output(report)


@router.post("/{report_id}/corrections")
def correct(report_id: int, payload: Correction, db: Session = Depends(get_db), user: dict = WRITE):
    report = get_report(db, report_id)
    check_write(report, user, payload.version_id, draft=False)
    if report.status == "draft":
        raise HTTPException(409, "Najpierw zakończ raport.")
    errors = validation(payload.fields)
    if not payload.reason.strip():
        errors["correction_reason"] = "Podaj powód korekty."
    if errors:
        raise HTTPException(422, {"message": "Uzupełnij zaznaczone pola.", "fields": errors})
    before = snapshot(report)
    report.fields, report.status = payload.fields.model_dump(), "corrected"
    report.correction_count += 1
    report.updated_at = datetime.now(timezone.utc)
    record(db, report, user, "corrected", before, payload.reason.strip())
    return output(report)


@router.delete("/{report_id}", status_code=204)
def delete(report_id: int, version_id: int = Query(ge=1), db: Session = Depends(get_db), user: dict = WRITE):
    report = get_report(db, report_id)
    check_write(report, user, version_id)
    # A complete draft must be finalized, never discarded as unfinished.
    if not validation(ShiftFields.model_validate(report.fields)):
        raise HTTPException(409, "Raport jest kompletny. Zakończ go zamiast usuwać.")
    for entry in db.scalars(select(ShiftReportAudit).where(ShiftReportAudit.report_id == report.id)):
        db.delete(entry)
    db.delete(report)
    commit(db)
    return Response(status_code=204)


@router.post("/{report_id}/delete", status_code=204)
def admin_delete(report_id: int, payload: AdminDelete, db: Session = Depends(get_db), user: dict = ADMIN):
    report = get_report(db, report_id)
    if report.version_id != payload.version_id:
        raise HTTPException(409, "Raport został zmieniony. Otwórz aktualną wersję przed usunięciem.")
    before = snapshot(report)
    report.deleted_at = report.updated_at = datetime.now(timezone.utc)
    record(db, report, user, "deleted", before, payload.reason)
    return Response(status_code=204)


@router.get("/{report_id}/audit")
def audit(report_id: int, include_drafts: bool = False, db: Session = Depends(get_db), user: dict = READ):
    get_report(db, report_id, include_deleted=can_admin_delete(user))
    query = select(ShiftReportAudit).where(ShiftReportAudit.report_id == report_id)
    if not include_drafts:
        query = query.where(ShiftReportAudit.action != "saved")
    return [{k: getattr(a, k) for k in ("id", "actor_id", "actor_name", "action", "reason", "before", "after", "created_at")}
            for a in db.scalars(query.order_by(ShiftReportAudit.id))]
