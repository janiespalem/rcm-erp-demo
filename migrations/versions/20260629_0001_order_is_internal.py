"""Add is_internal flag to orders (own-firm orders → koszt własny, not oferta)

Revision ID: 20260629_0001
Revises: 20260624_0001
Create Date: 2026-06-29
"""
from alembic import op
import sqlalchemy as sa

revision = "20260629_0001"
down_revision = "20260624_0001"
branch_labels = None
depends_on = None


def upgrade() -> None:
    cols = {c["name"] for c in sa.inspect(op.get_bind()).get_columns("orders")}
    if "is_internal" not in cols:
        op.add_column("orders", sa.Column("is_internal", sa.Boolean(), nullable=True, server_default=sa.false()))


def downgrade() -> None:
    with op.batch_alter_table("orders") as batch_op:
        batch_op.drop_column("is_internal")
