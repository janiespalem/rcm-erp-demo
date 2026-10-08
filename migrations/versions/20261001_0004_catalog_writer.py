"""Fence catalog writers and retain optimistic versions and command receipts."""
from alembic import op
import sqlalchemy as sa

revision = '20261001_0004'
down_revision = '20261001_0003'
branch_labels = None
depends_on = None


def upgrade():
    for table in ('approved_materials', 'operation_catalog'):
        if 'version_id' not in {c['name'] for c in sa.inspect(op.get_bind()).get_columns(table)}:
            op.add_column(table, sa.Column('version_id', sa.BigInteger(), nullable=False, server_default='1'))
    op.create_table('catalog_command_receipts',
        sa.Column('request_id', sa.String(36), primary_key=True),
        sa.Column('actor_id', sa.Integer(), nullable=False),
        sa.Column('command_type', sa.String(64), nullable=False),
        sa.Column('target_id', sa.BigInteger()),
        sa.Column('payload_hash', sa.String(64), nullable=False),
        sa.Column('response_json', sa.JSON(), nullable=False),
        sa.Column('created_at', sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()))
    op.execute("INSERT INTO settings(key,value,label) VALUES ('catalog_writer','legacy','Catalog writer') ON CONFLICT(key) DO NOTHING")
    if op.get_bind().dialect.name != 'postgresql':
        return
    op.execute("""
        CREATE FUNCTION public.lock_catalog_writer() RETURNS text LANGUAGE plpgsql
        SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
        DECLARE owner text;
        BEGIN
            SELECT value INTO owner FROM public.settings WHERE key='catalog_writer' FOR SHARE;
            RETURN owner;
        END $$;
        REVOKE ALL ON FUNCTION public.lock_catalog_writer() FROM PUBLIC;
        CREATE FUNCTION public.require_catalog_writer() RETURNS trigger LANGUAGE plpgsql
        SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
        DECLARE owner text;
        BEGIN
            owner := public.lock_catalog_writer();
            IF owner IS NULL OR owner <> coalesce(nullif(current_setting('rcm.catalog_writer',true),''),'legacy') THEN
                RAISE EXCEPTION 'Catalog writer is disabled for this application' USING ERRCODE='55000';
            END IF;
            RETURN NULL;
        END $$;
        CREATE FUNCTION public.increment_catalog_version() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN NEW.version_id := OLD.version_id + 1; RETURN NEW; END $$;
    """)
    for table in ('approved_materials', 'operation_catalog'):
        op.execute(f'CREATE TRIGGER require_catalog_writer BEFORE INSERT OR UPDATE OR DELETE ON public.{table} FOR EACH STATEMENT EXECUTE FUNCTION public.require_catalog_writer()')
        op.execute(f'CREATE TRIGGER increment_catalog_version BEFORE UPDATE ON public.{table} FOR EACH ROW EXECUTE FUNCTION public.increment_catalog_version()')


def downgrade():
    raise RuntimeError('Retain catalog receipts and writer fencing; reverse ownership and application without removing data.')
