"""Retain resource command receipts and fence operation and question writers."""
from alembic import op
import sqlalchemy as sa

revision = "20260928_0003"
down_revision = "20260928_0002"
branch_labels = None
depends_on = None


def upgrade():
    bind = op.get_bind()
    if not sa.inspect(bind).has_table("order_command_receipts"):
        op.create_table(
            "order_command_receipts",
            sa.Column("request_id", sa.String(36), primary_key=True),
            sa.Column("actor_id", sa.BigInteger(), nullable=False),
            sa.Column("command_type", sa.String(40), nullable=False),
            sa.Column("order_id", sa.BigInteger(), nullable=False),
            sa.Column("target_id", sa.BigInteger(), nullable=True),
            sa.Column("payload_hash", sa.String(64), nullable=False),
            sa.Column("response_json", sa.JSON(), nullable=False),
            sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        )
    if bind.dialect.name != "postgresql":
        return
    for table in ("order_operations", "parameter_requests", "order_command_receipts"):
        op.execute(f"CREATE TRIGGER require_order_writer BEFORE INSERT OR UPDATE OR DELETE ON public.{table} FOR EACH STATEMENT EXECUTE FUNCTION public.require_order_writer()")


def downgrade():
    raise RuntimeError("Retain resource command receipts and writer fences to prevent duplicate operations after retry.")
