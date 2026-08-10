"""Simplify OrderStatus: merge w_trakcie into in_production, drop legacy aliases

Reduces OrderStatus from 12 values to 8. Data-only and idempotent — remaps any
rows still carrying a removed status onto its surviving equivalent so the ORM
(whose enum no longer contains the old labels) can load every row.

  w_trakcie  -> in_production   (the separate "start production" step is gone)
  done       -> wydane          (legacy alias of a delivered order)
  cancelled  -> rejected        (dead status; terminal "not proceeding")
  triage     -> draft           (dead status; un-triaged new order)

The status column is a plain VARCHAR on SQLite and a native ENUM on Postgres.
We deliberately do NOT drop enum labels from the Postgres type — leftover unused
labels are harmless and dropping them is needlessly painful. Only data UPDATEs.

Revision ID: 20260624_0001
Revises: 20260623_0001
Create Date: 2026-06-24
"""
from alembic import op
import sqlalchemy as sa

revision = "20260624_0001"
down_revision = "20260623_0001"
branch_labels = None
depends_on = None


# (old_status, new_status) — applied in order; each UPDATE is a no-op when no
# matching rows exist, so the migration is safe on any database.
_STATUS_REMAP = [
    ("w_trakcie", "in_production"),
    ("done", "wydane"),
    ("cancelled", "rejected"),
    ("triage", "draft"),
]


def upgrade() -> None:
    bind = op.get_bind()
    postgres = bind.dialect.name == "postgresql"
    status_column = "status::text" if postgres else "status"
    new_value = "CAST(:new AS orderstatus)" if postgres else ":new"
    for old, new in _STATUS_REMAP:
        op.execute(sa.text(
            f"UPDATE orders SET status = {new_value} WHERE {status_column} = :old"
        ).bindparams(old=old, new=new))


def downgrade() -> None:
    # Irreversible by design: the original distinction (e.g. which in_production
    # rows were once w_trakcie) is not recoverable, and the surviving statuses
    # are a strict superset-compatible target. No-op.
    pass
