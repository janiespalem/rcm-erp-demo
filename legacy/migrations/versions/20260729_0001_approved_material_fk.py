"""Enforce the approved material reference on orders

Revision ID: 20260729_0001
Revises: 20260728_0001
Create Date: 2026-07-29
"""
from alembic import op
import sqlalchemy as sa

revision = "20260729_0001"
down_revision = "20260728_0001"
branch_labels = None
depends_on = None


def _constraint_name() -> str | None:
    inspector = sa.inspect(op.get_bind())
    for constraint in inspector.get_foreign_keys("orders"):
        if constraint["constrained_columns"] == ["approved_material_id"]:
            return constraint["name"]
    return None


def upgrade() -> None:
    if _constraint_name():
        return
    with op.batch_alter_table("orders") as batch_op:
        batch_op.create_foreign_key(
            "fk_orders_approved_material_id",
            "approved_materials",
            ["approved_material_id"],
            ["id"],
        )


def downgrade() -> None:
    constraint_name = _constraint_name()
    if constraint_name:
        with op.batch_alter_table("orders") as batch_op:
            batch_op.drop_constraint(constraint_name, type_="foreignkey")
