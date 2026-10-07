"""Fence order writers before switching the module to ASP.NET Core."""
from alembic import op
import sqlalchemy as sa

revision = "20260928_0002"
down_revision = "20260928_0001"
branch_labels = None
depends_on = None

TABLES = ("orders", "quotes", "order_events", "order_counters", "order_create_receipts")


def upgrade():
    bind = op.get_bind()
    bind.execute(sa.text("INSERT INTO settings(key,value,label) VALUES ('orders_writer','legacy','Order writer') ON CONFLICT(key) DO NOTHING"))
    if bind.dialect.name != "postgresql":
        return
    op.execute("""
        CREATE FUNCTION public.require_order_writer() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE owner text;
        BEGIN
            SELECT value INTO owner FROM public.settings WHERE key='orders_writer' FOR SHARE;
            IF owner IS NULL OR owner <> coalesce(nullif(current_setting('rcm.orders_writer',true),''),'legacy') THEN
                RAISE EXCEPTION 'Order writer is disabled for this application' USING ERRCODE='55000';
            END IF;
            RETURN NULL;
        END $$
    """)
    for table in TABLES:
        op.execute(f"CREATE TRIGGER require_order_writer BEFORE INSERT OR UPDATE OR DELETE ON public.{table} FOR EACH STATEMENT EXECUTE FUNCTION public.require_order_writer()")


def downgrade():
    raise RuntimeError("Retain the order writer fence; switch ownership explicitly without deleting receipts or order data.")
