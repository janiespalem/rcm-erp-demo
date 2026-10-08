"""Add operation_catalog table and catalog_id to order_operations

Revision ID: 20260514_0002
Revises: 20260514_0001
Create Date: 2026-05-14
"""
from alembic import op
import sqlalchemy as sa

revision = "20260514_0002"
down_revision = "20260514_0001"
branch_labels = None
depends_on = None


def upgrade():
    conn = op.get_bind()

    # Create operation_catalog if not exists
    if not conn.dialect.has_table(conn, "operation_catalog"):
        op.create_table(
            "operation_catalog",
            sa.Column("id",           sa.Integer(), primary_key=True),
            sa.Column("name",         sa.String(100), nullable=False, unique=True),
            sa.Column("department",   sa.String(50)),
            sa.Column("default_rate", sa.Numeric(10, 2)),
            sa.Column("formula",      sa.String(255)),
        )

    # Add catalog_id to order_operations if not exists
    inspector = sa.inspect(conn)
    columns = [c["name"] for c in inspector.get_columns("order_operations")]
    if "catalog_id" not in columns:
        op.add_column(
            "order_operations",
            sa.Column("catalog_id", sa.Integer(), sa.ForeignKey("operation_catalog.id"), nullable=True),
        )


def downgrade():
    conn = op.get_bind()
    inspector = sa.inspect(conn)
    if "catalog_id" in [c["name"] for c in inspector.get_columns("order_operations")]:
        with op.batch_alter_table("order_operations") as batch_op:
            batch_op.drop_column("catalog_id")
    if "operation_catalog" in inspector.get_table_names():
        op.drop_table("operation_catalog")
