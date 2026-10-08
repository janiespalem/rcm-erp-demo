"""Add quote pricing method fields

Revision ID: 20260629_0002
Revises: 20260629_0001
Create Date: 2026-06-29
"""
from alembic import op
import sqlalchemy as sa

revision = "20260629_0002"
down_revision = "20260629_0001"
branch_labels = None
depends_on = None


def upgrade() -> None:
    cols = {c["name"] for c in sa.inspect(op.get_bind()).get_columns("quotes")}
    if "pricing_method" not in cols:
        op.add_column("quotes", sa.Column("pricing_method", sa.String(length=12), nullable=True, server_default="kalkulacja"))
    if "weight_basis" not in cols:
        op.add_column("quotes", sa.Column("weight_basis", sa.String(length=8), nullable=True, server_default="netto"))


def downgrade() -> None:
    cols = {c["name"] for c in sa.inspect(op.get_bind()).get_columns("quotes")}
    with op.batch_alter_table("quotes") as batch_op:
        if "weight_basis" in cols:
            batch_op.drop_column("weight_basis")
        if "pricing_method" in cols:
            batch_op.drop_column("pricing_method")
