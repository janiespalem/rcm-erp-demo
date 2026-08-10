"""Add reversible order archive

Revision ID: 20260715_0001
Revises: 20260701_0001
Create Date: 2026-07-15
"""
from alembic import op
import sqlalchemy as sa

revision = "20260715_0001"
down_revision = "20260701_0001"
branch_labels = None
depends_on = None


def upgrade() -> None:
    cols = {c["name"] for c in sa.inspect(op.get_bind()).get_columns("orders")}
    if "archived_at" not in cols:
        op.add_column("orders", sa.Column("archived_at", sa.DateTime(), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table("orders") as batch_op:
        batch_op.drop_column("archived_at")
