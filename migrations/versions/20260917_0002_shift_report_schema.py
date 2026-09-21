"""Pin existing and future reports to an immutable paper-form schema version."""
from alembic import op
import sqlalchemy as sa

revision = "20260917_0002"
down_revision = "20260917_0001"
branch_labels = None
depends_on = None


def upgrade():
    if "schema_version" not in {c["name"] for c in sa.inspect(op.get_bind()).get_columns("shift_reports")}:
        # Constant DEFAULT uses PostgreSQL's metadata-only fast path. Existing
        # reports are v1; their JSON fields and audit snapshots are untouched.
        op.add_column("shift_reports", sa.Column("schema_version", sa.Integer(), nullable=False, server_default="1"))


def downgrade():
    raise RuntimeError("Migration 20260917_0002 is irreversible: retain historical report schema versions and use a compatible application rollback.")
