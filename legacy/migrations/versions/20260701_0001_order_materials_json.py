"""Add intake materials_json to orders

Revision ID: 20260701_0001
Revises: 20260629_0002
Create Date: 2026-07-01
"""
from alembic import op
import sqlalchemy as sa

revision = "20260701_0001"
down_revision = "20260629_0002"
branch_labels = None
depends_on = None


def upgrade() -> None:
    cols = {c["name"] for c in sa.inspect(op.get_bind()).get_columns("orders")}
    if "materials_json" not in cols:
        op.add_column("orders", sa.Column("materials_json", sa.JSON(), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table("orders") as batch_op:
        batch_op.drop_column("materials_json")
