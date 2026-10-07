"""Extend catalog ownership and optimistic versions to product templates."""
from alembic import op
import sqlalchemy as sa

revision = '20261001_0005'
down_revision = '20261001_0004'
branch_labels = None
depends_on = None


def upgrade():
    if 'version_id' not in {column['name'] for column in sa.inspect(op.get_bind()).get_columns('product_templates')}:
        op.add_column('product_templates', sa.Column('version_id', sa.BigInteger(), nullable=False, server_default='1'))
    if op.get_bind().dialect.name != 'postgresql':
        return
    op.execute("""
        CREATE FUNCTION public.require_template_writer() RETURNS trigger LANGUAGE plpgsql
        SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
        DECLARE owner text;
        BEGIN
            IF TG_OP = 'INSERT' AND current_setting('rcm.orders_writer',true) = 'dotnet' THEN
                IF public.lock_order_writer() = 'dotnet' THEN
                    RETURN NULL;
                END IF;
            END IF;
            owner := public.lock_catalog_writer();
            IF owner IS NULL OR owner <> coalesce(nullif(current_setting('rcm.catalog_writer',true),''),'legacy') THEN
                RAISE EXCEPTION 'Template writer is disabled for this application' USING ERRCODE='55000';
            END IF;
            RETURN NULL;
        END $$;
        CREATE TRIGGER require_template_writer BEFORE INSERT OR UPDATE OR DELETE ON public.product_templates
          FOR EACH STATEMENT EXECUTE FUNCTION public.require_template_writer();
        CREATE TRIGGER increment_catalog_version BEFORE UPDATE ON public.product_templates
          FOR EACH ROW EXECUTE FUNCTION public.increment_catalog_version();
    """)


def downgrade():
    raise RuntimeError('Retain template versions and catalog writer fencing; reverse ownership and application without removing data.')
