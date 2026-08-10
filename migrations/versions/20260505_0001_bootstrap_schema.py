"""bootstrap existing ERP schema

Revision ID: 20260505_0001
Revises:
Create Date: 2026-05-05 00:01:00 UTC
"""
from __future__ import annotations

from pathlib import Path
import sys

from alembic import op
import sqlalchemy as sa

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "backend"))
from models import Base

revision = "20260505_0001"
down_revision = None
branch_labels = None
depends_on = None


def _has_column(inspector: sa.Inspector, table_name: str, column_name: str) -> bool:
    return any(column["name"] == column_name for column in inspector.get_columns(table_name))


def _add_column_if_missing(table_name: str, column: sa.Column) -> None:
    bind = op.get_bind()
    inspector = sa.inspect(bind)
    if table_name not in inspector.get_table_names():
        return
    if not _has_column(inspector, table_name, column.name):
        op.add_column(table_name, column)


def _ensure_unique_quote_order_index() -> None:
    bind = op.get_bind()
    inspector = sa.inspect(bind)
    if "quotes" not in inspector.get_table_names():
        return

    duplicates = bind.execute(sa.text("""
        SELECT order_id
        FROM quotes
        GROUP BY order_id
        HAVING COUNT(*) > 1
        ORDER BY order_id
    """)).scalars().all()
    if duplicates:
        raise RuntimeError(
            "Nie można utworzyć unikalnego indeksu quotes.order_id; "
            f"duplikaty dla zleceń: {duplicates}"
        )

    indexes = {index["name"] for index in inspector.get_indexes("quotes")}
    unique_constraints = {constraint["name"] for constraint in inspector.get_unique_constraints("quotes")}
    if "uq_quotes_order_id" not in indexes and "uq_quotes_order_id" not in unique_constraints:
        op.create_index("uq_quotes_order_id", "quotes", ["order_id"], unique=True)


def upgrade() -> None:
    bind = op.get_bind()
    Base.metadata.create_all(bind=bind)

    _add_column_if_missing("orders", sa.Column("approved_material_id", sa.Integer(), nullable=True))
    _add_column_if_missing("orders", sa.Column("order_type", sa.String(length=20), server_default="remont"))
    _add_column_if_missing("orders", sa.Column("sop_name", sa.String(length=200), nullable=True))
    _add_column_if_missing("orders", sa.Column("description", sa.Text(), nullable=True))
    _add_column_if_missing("orders", sa.Column("requires_visit", sa.Boolean(), server_default=sa.false()))
    _add_column_if_missing("orders", sa.Column("quantity", sa.Integer(), server_default="1"))
    _add_column_if_missing("orders", sa.Column("is_defence", sa.Boolean(), server_default=sa.false()))

    _add_column_if_missing("quotes", sa.Column("processes_json", sa.JSON(), nullable=True))
    _add_column_if_missing("quotes", sa.Column("weight_kg", sa.Numeric(10, 3), server_default="0"))
    _add_column_if_missing("quotes", sa.Column("weight_rate_pln_kg", sa.Numeric(6, 2), server_default="15"))
    _add_column_if_missing("quotes", sa.Column("welding_hours", sa.Numeric(8, 2), server_default="0"))
    _add_column_if_missing("quotes", sa.Column("weight_netto_kg", sa.Numeric(10, 3), server_default="0"))
    _add_column_if_missing("quotes", sa.Column("weight_brutto_kg", sa.Numeric(10, 3), server_default="0"))
    _add_column_if_missing("quotes", sa.Column("estimate_version", sa.String(length=10), server_default="v1"))
    _add_column_if_missing("quotes", sa.Column("last_edited_at", sa.DateTime(), nullable=True))
    _add_column_if_missing("quotes", sa.Column("transport_cost", sa.Numeric(10, 2), server_default="0"))
    _add_column_if_missing("quotes", sa.Column("material_weight_kg", sa.Numeric(10, 3), server_default="0"))
    _add_column_if_missing("quotes", sa.Column("material_price_per_kg", sa.Numeric(6, 2), server_default="0"))
    _add_column_if_missing("quotes", sa.Column("show_unit_prices", sa.Boolean(), server_default=sa.true()))

    _add_column_if_missing("product_templates", sa.Column("project_code", sa.String(length=50), nullable=True))
    _add_column_if_missing("product_templates", sa.Column("position_nr", sa.String(length=50), nullable=True))
    _add_column_if_missing("product_templates", sa.Column("drawing_path", sa.String(length=500), nullable=True))
    _add_column_if_missing("product_templates", sa.Column("notes", sa.Text(), nullable=True))

    _ensure_unique_quote_order_index()


def downgrade() -> None:
    pass
