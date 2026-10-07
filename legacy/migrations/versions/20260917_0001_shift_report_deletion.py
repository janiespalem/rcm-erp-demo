"""Retain deleted reports/audit, with uniqueness for active date/shift only."""
from alembic import op
import sqlalchemy as sa

revision = "20260917_0001"
down_revision = "20260916_0001"
branch_labels = None
depends_on = None


def upgrade():
    bind = op.get_bind()
    if "deleted_at" not in {c["name"] for c in sa.inspect(bind).get_columns("shift_reports")}:
        op.add_column("shift_reports", sa.Column("deleted_at", sa.DateTime(timezone=True), nullable=True))
    indexes = {i["name"] for i in sa.inspect(bind).get_indexes("shift_reports")}
    if "uq_shift_report_active_date_shift" not in indexes:
        with op.get_context().autocommit_block():
            op.create_index("uq_shift_report_active_date_shift", "shift_reports", ["report_date", "shift"],
                            unique=True, postgresql_where=sa.text("deleted_at IS NULL"),
                            sqlite_where=sa.text("deleted_at IS NULL"), postgresql_concurrently=True)
    constraints = {c["name"] for c in sa.inspect(bind).get_unique_constraints("shift_reports")}
    if "uq_shift_report_date_shift" in constraints:
        with op.batch_alter_table("shift_reports") as batch:
            batch.drop_constraint("uq_shift_report_date_shift", type_="unique")


def downgrade():
    raise RuntimeError("Migration 20260917_0001 is irreversible: archived reports and audit must be retained. Use an application rollback compatible with tombstones.")
