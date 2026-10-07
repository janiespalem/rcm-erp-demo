"""Add zapor_multiplier to quotes table

Revision ID: 20260514_0004
Revises: 20260514_0003
Create Date: 2026-05-14
"""
from alembic import op
import sqlalchemy as sa

revision = "20260514_0004"
down_revision = "20260514_0003"
branch_labels = None
depends_on = None


def upgrade() -> None:
    cols = {c["name"] for c in sa.inspect(op.get_bind()).get_columns("quotes")}
    if "zapor_multiplier" not in cols:
        with op.batch_alter_table("quotes") as batch_op:
            batch_op.add_column(sa.Column("zapor_multiplier", sa.Numeric(5, 2), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table("quotes") as batch_op:
        batch_op.drop_column("zapor_multiplier")
