"""Persist native order creation receipts with the authoritative legacy order."""
from alembic import op
import sqlalchemy as sa

revision = "20260928_0001"
down_revision = "20260924_0001"
branch_labels = None
depends_on = None


def upgrade():
    # The historical bootstrap creates current metadata on a fresh database.
    if sa.inspect(op.get_bind()).has_table("order_create_receipts"):
        return
    op.create_table(
        "order_create_receipts",
        sa.Column("request_id", sa.String(36), primary_key=True),
        sa.Column("actor_id", sa.Integer(), nullable=False),
        sa.Column("payload_hash", sa.String(64), nullable=False),
        sa.Column("response_json", sa.JSON(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
    )


def downgrade():
    raise RuntimeError("Retain order creation receipts to prevent duplicate orders after retry or rollback.")
