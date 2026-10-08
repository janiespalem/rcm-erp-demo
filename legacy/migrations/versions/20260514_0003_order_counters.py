"""Add order_counters table for race-safe order number generation

Revision ID: 20260514_0003
Revises: 20260514_0002
Create Date: 2026-05-14
"""
from alembic import op
import sqlalchemy as sa

revision = "20260514_0003"
down_revision = "20260514_0002"
branch_labels = None
depends_on = None


def upgrade() -> None:
    inspector = sa.inspect(op.get_bind())
    if "order_counters" in inspector.get_table_names():
        return

    op.create_table(
        "order_counters",
        sa.Column("year",     sa.Integer(), primary_key=True),
        sa.Column("next_seq", sa.Integer(), nullable=False, server_default="1"),
    )

    # Seed counter from existing orders using dialect-agnostic ORM query.
    # Parse "{seq}/{year}" format in Python — no dialect-specific string functions.
    conn = op.get_bind()
    rows = conn.execute(
        sa.text("SELECT order_number FROM orders WHERE order_number IS NOT NULL")
    ).fetchall()

    year_max: dict[int, int] = {}
    for (order_number,) in rows:
        if not order_number or "/" not in order_number:
            continue
        parts = order_number.split("/")
        if len(parts) != 2:
            continue
        try:
            seq = int(parts[0])
            yr  = int(parts[1])
        except ValueError:
            continue
        if yr not in year_max or seq > year_max[yr]:
            year_max[yr] = seq

    dialect = conn.dialect.name
    for yr, max_seq in year_max.items():
        if dialect == "postgresql":
            conn.execute(
                sa.text(
                    "INSERT INTO order_counters (year, next_seq) VALUES (:yr, :ns) "
                    "ON CONFLICT (year) DO NOTHING"
                ),
                {"yr": yr, "ns": max_seq + 1},
            )
        else:
            conn.execute(
                sa.text(
                    "INSERT OR IGNORE INTO order_counters (year, next_seq) VALUES (:yr, :ns)"
                ),
                {"yr": yr, "ns": max_seq + 1},
            )


def downgrade() -> None:
    op.drop_table("order_counters")
