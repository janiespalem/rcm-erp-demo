"""add indexes on orders.status, deadline, order_number

Revision ID: 31c54e614e8c
Revises: 20260513_0001
Create Date: 2026-05-13
"""
from alembic import op
import sqlalchemy as sa

revision = '31c54e614e8c'
down_revision = '20260513_0001'
branch_labels = None
depends_on = None


def _existing_index_names(table_name: str) -> set:
    return {idx["name"] for idx in sa.inspect(op.get_bind()).get_indexes(table_name)}


def upgrade() -> None:
    existing = _existing_index_names("orders")
    if "ix_orders_status" not in existing:
        op.create_index('ix_orders_status',       'orders', ['status'])
    if "ix_orders_deadline" not in existing:
        op.create_index('ix_orders_deadline',     'orders', ['deadline'])
    if "ix_orders_order_number" not in existing:
        op.create_index('ix_orders_order_number', 'orders', ['order_number'])


def downgrade() -> None:
    op.drop_index('ix_orders_order_number', table_name='orders')
    op.drop_index('ix_orders_deadline',     table_name='orders')
    op.drop_index('ix_orders_status',       table_name='orders')
