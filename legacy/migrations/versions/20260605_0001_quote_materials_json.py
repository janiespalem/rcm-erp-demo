"""Add materials_json to quotes table (multi-material v3)

Revision ID: 20260605_0001
Revises: 20260514_0004
Create Date: 2026-06-05
"""
from alembic import op
import sqlalchemy as sa

revision = "20260605_0001"
down_revision = "20260514_0004"
branch_labels = None
depends_on = None


def upgrade() -> None:
    cols = {c["name"] for c in sa.inspect(op.get_bind()).get_columns("quotes")}
    if "materials_json" not in cols:
        with op.batch_alter_table("quotes") as batch_op:
            batch_op.add_column(
                sa.Column("materials_json", sa.JSON(), nullable=True)
            )


def downgrade() -> None:
    with op.batch_alter_table("quotes") as batch_op:
        batch_op.drop_column("materials_json")
