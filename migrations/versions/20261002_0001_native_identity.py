"""Fence identity ownership without changing existing credentials or sessions."""
from alembic import op

revision = "20261002_0001"
down_revision = "20261001_0007"
branch_labels = None
depends_on = None


def upgrade():
    op.execute("INSERT INTO settings(key,value,label) VALUES ('identity_writer','legacy','Identity writer') ON CONFLICT(key) DO NOTHING")
    if op.get_bind().dialect.name != "postgresql":
        return
    op.execute("""
        CREATE FUNCTION public.require_identity_writer(expected text) RETURNS void
        LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
        DECLARE owner text;
        BEGIN
            IF expected IS NULL OR expected NOT IN ('legacy','dotnet') THEN
                RAISE EXCEPTION 'Invalid identity writer' USING ERRCODE='22023';
            END IF;
            SELECT value INTO owner FROM public.settings WHERE key='identity_writer' FOR SHARE;
            IF owner IS DISTINCT FROM expected THEN
                RAISE EXCEPTION 'Identity writer is disabled for this application' USING ERRCODE='55000';
            END IF;
        END $$;
        REVOKE ALL ON FUNCTION public.require_identity_writer(text) FROM PUBLIC;
    """)


def downgrade():
    raise RuntimeError("Retain identity ownership and existing session revocations; roll back the application and writer together.")
