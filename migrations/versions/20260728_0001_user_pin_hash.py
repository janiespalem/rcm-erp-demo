"""Add database-backed user PIN hashes

Revision ID: 20260728_0001
Revises: 20260715_0001
Create Date: 2026-07-28
"""
from alembic import op
import sqlalchemy as sa

revision = "20260728_0001"
down_revision = "20260715_0001"
branch_labels = None
depends_on = None


def upgrade() -> None:
    inspector = sa.inspect(op.get_bind())
    columns = {column["name"] for column in inspector.get_columns("users")}
    if "pin_hash" not in columns:
        op.add_column("users", sa.Column("pin_hash", sa.String(length=128), nullable=True))
    if "pin" in columns:
        op.execute(sa.text("UPDATE users SET pin = NULL"))


def downgrade() -> None:
    columns = {column["name"] for column in sa.inspect(op.get_bind()).get_columns("users")}
    if "pin_hash" in columns:
        op.drop_column("users", "pin_hash")
