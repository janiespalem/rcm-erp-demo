"""Add intake fields to orders (weight, drawing no., dimensions, delivery, contact)

Revision ID: 20260623_0001
Revises: 20260605_0001
Create Date: 2026-06-23
"""
from alembic import op
import sqlalchemy as sa

revision = "20260623_0001"
down_revision = "20260605_0001"
branch_labels = None
depends_on = None


_NEW_COLUMNS = [
    ("weight_kg", sa.Numeric(10, 3)),
    ("drawing_number", sa.String(50)),
    ("dimensions", sa.String(120)),
    ("delivery_address", sa.Text()),
    ("contact", sa.String(160)),
]


def upgrade() -> None:
    # Plain ADD COLUMN is natively supported by both SQLite and Postgres; avoid
    # batch_alter_table here because recreating `orders` (FKs + Enum CHECKs) trips
    # SQLite's "no ALTER of constraints".
    cols = {c["name"] for c in sa.inspect(op.get_bind()).get_columns("orders")}
    for name, type_ in _NEW_COLUMNS:
        if name not in cols:
            op.add_column("orders", sa.Column(name, type_, nullable=True))


def downgrade() -> None:
    with op.batch_alter_table("orders") as batch_op:
        for name, _ in _NEW_COLUMNS:
            batch_op.drop_column(name)
