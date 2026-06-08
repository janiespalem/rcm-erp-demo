"""Add order_events table for audit log

Revision ID: 20260514_0001
Revises: 31c54e614e8c
Create Date: 2026-05-14
"""
from alembic import op
import sqlalchemy as sa

revision = "20260514_0001"
down_revision = "31c54e614e8c"
branch_labels = None
depends_on = None


def upgrade() -> None:
    inspector = sa.inspect(op.get_bind())
    if "order_events" in inspector.get_table_names():
        return
    op.create_table(
        "order_events",
        sa.Column("id",         sa.Integer(),     nullable=False, primary_key=True),
        sa.Column("order_id",   sa.Integer(),     nullable=False),
        sa.Column("user_role",  sa.String(30),    nullable=True),
        sa.Column("user_name",  sa.String(100),   nullable=True),
        sa.Column("event_type", sa.String(30),    nullable=True),
        sa.Column("old_status", sa.String(30),    nullable=True),
        sa.Column("new_status", sa.String(30),    nullable=True),
        sa.Column("note",       sa.Text(),        nullable=True),
        sa.Column("created_at", sa.DateTime(),    nullable=True),
        sa.ForeignKeyConstraint(["order_id"], ["orders.id"], ondelete="CASCADE"),
    )
    op.create_index("ix_order_events_order_id", "order_events", ["order_id"])


def downgrade() -> None:
    inspector = sa.inspect(op.get_bind())
    existing_indexes = [i["name"] for i in inspector.get_indexes("order_events")] if "order_events" in inspector.get_table_names() else []
    if "ix_order_events_order_id" in existing_indexes:
        op.drop_index("ix_order_events_order_id", table_name="order_events")
    if "order_events" in inspector.get_table_names():
        op.drop_table("order_events")
