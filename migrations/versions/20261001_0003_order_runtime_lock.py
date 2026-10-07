"""Allow a restricted order runtime to lock ownership without changing settings."""
from alembic import op

revision = "20261001_0003"
down_revision = "20261001_0002"
branch_labels = None
depends_on = None


def upgrade():
    if op.get_bind().dialect.name != "postgresql":
        return
    op.execute("""
        CREATE FUNCTION public.lock_order_writer() RETURNS text
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
        DECLARE owner text;
        BEGIN
            SELECT value INTO owner FROM public.settings WHERE key='orders_writer' FOR SHARE;
            RETURN owner;
        END $$;
        REVOKE ALL ON FUNCTION public.lock_order_writer() FROM PUBLIC;
        CREATE OR REPLACE FUNCTION public.require_order_writer() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
        DECLARE owner text;
        BEGIN
            owner := public.lock_order_writer();
            IF owner IS NULL OR owner <> coalesce(nullif(current_setting('rcm.orders_writer',true),''),'legacy') THEN
                RAISE EXCEPTION 'Order writer is disabled for this application' USING ERRCODE='55000';
            END IF;
            RETURN NULL;
        END $$;
    """)


def downgrade():
    raise RuntimeError("Retain runtime writer locking; roll back the application and writer without removing protection.")
