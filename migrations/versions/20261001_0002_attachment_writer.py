"""Fence attachment metadata writes with the existing order owner."""
from alembic import op

revision = "20261001_0002"
down_revision = "20261001_0001"
branch_labels = None
depends_on = None


def upgrade():
    if op.get_bind().dialect.name == "postgresql":
        op.execute("""
            CREATE TRIGGER require_order_writer
            BEFORE INSERT OR UPDATE OR DELETE ON public.order_attachments
            FOR EACH STATEMENT EXECUTE FUNCTION public.require_order_writer()
        """)


def downgrade():
    raise RuntimeError("Retain the attachment writer fence; switch ownership explicitly without deleting files or metadata.")
