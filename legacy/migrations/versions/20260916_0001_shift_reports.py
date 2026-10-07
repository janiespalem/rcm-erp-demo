"""Add concrete-plant shift reports without changing existing operational data."""
from alembic import op
import sqlalchemy as sa

revision = "20260916_0001"
down_revision = "20260912_0001"
branch_labels = None
depends_on = None


def upgrade():
    bind = op.get_bind()
    if bind.dialect.name == "postgresql":
        with op.get_context().autocommit_block():
            op.execute("ALTER TYPE userrole ADD VALUE IF NOT EXISTS 'produkcja'")
    if "default_shift" not in {c["name"] for c in sa.inspect(bind).get_columns("users")}:
        op.add_column("users", sa.Column("default_shift", sa.String(2), nullable=True))
    # Historical bootstrap uses current metadata on a fresh database.
    tables = sa.inspect(bind).get_table_names()
    if "shift_reports" not in tables:
        op.create_table(
            "shift_reports",
            sa.Column("id", sa.Integer(), primary_key=True),
            sa.Column("report_date", sa.Date(), nullable=False),
            sa.Column("shift", sa.String(2), nullable=False),
            sa.Column("author_id", sa.Integer(), sa.ForeignKey("users.id"), nullable=False),
            sa.Column("author_name", sa.String(100), nullable=False),
            sa.Column("status", sa.String(12), nullable=False),
            sa.Column("version_id", sa.Integer(), nullable=False),
            sa.Column("fields", sa.JSON(), nullable=False),
            sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
            sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False),
            sa.Column("finalized_at", sa.DateTime(timezone=True)),
            sa.Column("finalized_by_id", sa.Integer(), sa.ForeignKey("users.id")),
            sa.Column("finalized_by_name", sa.String(100)),
            sa.Column("correction_count", sa.Integer(), nullable=False),
            sa.UniqueConstraint("report_date", "shift", name="uq_shift_report_date_shift"),
            sa.CheckConstraint("shift IN ('I', 'II')", name="ck_shift_report_shift"),
            sa.CheckConstraint("status IN ('draft', 'finalized', 'corrected')", name="ck_shift_report_status"),
            sa.CheckConstraint("version_id > 0", name="ck_shift_report_version"),
            sa.CheckConstraint("(status = 'draft' AND finalized_at IS NULL) OR (status <> 'draft' AND finalized_at IS NOT NULL)", name="ck_shift_report_finalized"),
        )
    if "shift_report_audit" not in tables:
        op.create_table(
            "shift_report_audit",
            sa.Column("id", sa.Integer(), primary_key=True),
            sa.Column("report_id", sa.Integer(), sa.ForeignKey("shift_reports.id", ondelete="CASCADE"), nullable=False),
            sa.Column("actor_id", sa.Integer(), sa.ForeignKey("users.id"), nullable=False),
            sa.Column("actor_name", sa.String(100), nullable=False),
            sa.Column("action", sa.String(20), nullable=False),
            sa.Column("reason", sa.Text()),
            sa.Column("before", sa.JSON()),
            sa.Column("after", sa.JSON(), nullable=False),
            sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        )
        with op.get_context().autocommit_block():
            op.create_index("ix_shift_report_audit_report_id", "shift_report_audit", ["report_id"], postgresql_concurrently=True)


def downgrade():
    raise RuntimeError("Migration 20260916_0001 is irreversible: shift reports, audit and production accounts must be retained. Use an application rollback compatible with the current schema.")
