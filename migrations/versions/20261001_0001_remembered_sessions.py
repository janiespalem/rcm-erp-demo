"""Add revocable remembered password sessions."""
from alembic import op
import sqlalchemy as sa

revision = "20261001_0001"
down_revision = "20260930_0001"
branch_labels = None
depends_on = None


def upgrade() -> None:
    bind = op.get_bind()
    if sa.inspect(bind).has_table("remembered_sessions"):
        return
    op.create_table(
        "remembered_sessions",
        sa.Column("id", sa.String(length=36), primary_key=True),
        sa.Column("token_hash", sa.String(length=64), nullable=False),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("users.id"), nullable=False),
        sa.Column("password_version", sa.Integer(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("last_used_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("revoked_at", sa.DateTime(timezone=True), nullable=True),
        sa.UniqueConstraint("token_hash", name="uq_remembered_sessions_token_hash"),
    )
    op.create_index("ix_remembered_sessions_user_id", "remembered_sessions", ["user_id"])
    op.create_index("ix_remembered_sessions_expires_at", "remembered_sessions", ["expires_at"])


def downgrade() -> None:
    raise RuntimeError("Retain remembered-session revocations so application rollback cannot restore device access.")
