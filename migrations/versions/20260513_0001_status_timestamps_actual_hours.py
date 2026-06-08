"""Add status timestamps to orders + actual_hours to order_operations

Revision ID: 20260513_0001
Revises: 20260507_0001
Create Date: 2026-05-13
"""
from alembic import op
import sqlalchemy as sa

revision = "20260513_0001"
down_revision = "20260507_0001"
branch_labels = None
depends_on = None


def _existing_columns(table_name: str) -> set:
    inspector = sa.inspect(op.get_bind())
    return {c["name"] for c in inspector.get_columns(table_name)}


def upgrade() -> None:
    orders_cols = _existing_columns("orders")
    with op.batch_alter_table("orders") as batch_op:
        if "quoted_at" not in orders_cols:
            batch_op.add_column(sa.Column("quoted_at",    sa.DateTime(), nullable=True))
        if "started_at" not in orders_cols:
            batch_op.add_column(sa.Column("started_at",   sa.DateTime(), nullable=True))
        if "completed_at" not in orders_cols:
            batch_op.add_column(sa.Column("completed_at", sa.DateTime(), nullable=True))
        if "delivered_at" not in orders_cols:
            batch_op.add_column(sa.Column("delivered_at", sa.DateTime(), nullable=True))

    ops_cols = _existing_columns("order_operations")
    with op.batch_alter_table("order_operations") as batch_op:
        if "actual_hours" not in ops_cols:
            batch_op.add_column(sa.Column("actual_hours", sa.Numeric(8, 2), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table("order_operations") as batch_op:
        batch_op.drop_column("actual_hours")

    with op.batch_alter_table("orders") as batch_op:
        batch_op.drop_column("delivered_at")
        batch_op.drop_column("completed_at")
        batch_op.drop_column("started_at")
        batch_op.drop_column("quoted_at")
