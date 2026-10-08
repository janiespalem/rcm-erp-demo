"""Allow a CRM-only identity without changing existing users or ERP permissions."""
from alembic import op

revision = "20260924_0001"
down_revision = "20260917_0002"
branch_labels = None
depends_on = None


def upgrade():
    if op.get_bind().dialect.name == "postgresql":
        with op.get_context().autocommit_block():
            op.execute("ALTER TYPE userrole ADD VALUE IF NOT EXISTS 'crm'")


def downgrade():
    raise RuntimeError("Migration 20260924_0001 is irreversible: retain CRM identities and use a compatible application rollback.")
