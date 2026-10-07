"""Add individual password login to existing users."""
from alembic import op
import sqlalchemy as sa

revision = "20260930_0001"
down_revision = "20260928_0003"
branch_labels = None
depends_on = None


def upgrade() -> None:
    bind = op.get_bind()
    columns = {column["name"] for column in sa.inspect(bind).get_columns("users")}
    if "username" not in columns:
        op.add_column("users", sa.Column("username", sa.String(length=64), nullable=True))
    if "password_hash" not in columns:
        op.add_column("users", sa.Column("password_hash", sa.String(length=128), nullable=True))
    if "password_version" not in columns:
        op.add_column(
            "users",
            sa.Column("password_version", sa.Integer(), server_default="0", nullable=False),
        )

    if bind.dialect.name == "postgresql":
        with op.get_context().autocommit_block():
            op.create_index(
                "ux_users_username",
                "users",
                ["username"],
                unique=True,
                postgresql_where=sa.text("username IS NOT NULL"),
                postgresql_concurrently=True,
                if_not_exists=True,
            )
    else:
        indexes = {index["name"] for index in sa.inspect(bind).get_indexes("users")}
        if "ux_users_username" not in indexes:
            op.create_index("ux_users_username", "users", ["username"], unique=True)


def downgrade() -> None:
    raise RuntimeError("Retain password credentials so application rollback cannot silently remove access state.")
