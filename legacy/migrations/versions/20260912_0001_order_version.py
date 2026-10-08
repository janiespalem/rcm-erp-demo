"""Add optimistic versioning for orders, preserving existing rows."""
from alembic import op
import sqlalchemy as sa

revision = "20260912_0001"
down_revision = "20260729_0001"
branch_labels = None
depends_on = None


def upgrade():
    # The historical bootstrap creates current metadata on a fresh database.
    if "version_id" not in {c["name"] for c in sa.inspect(op.get_bind()).get_columns("orders")}:
        op.add_column("orders", sa.Column("version_id", sa.Integer(), nullable=False, server_default="1"))


def downgrade():
    op.drop_column("orders", "version_id")
