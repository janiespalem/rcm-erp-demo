"""Version business settings and restrict catalog runtime mutation."""
from alembic import op
import sqlalchemy as sa

revision = "20261001_0007"
down_revision = "20261001_0006"
branch_labels = None
depends_on = None


def upgrade():
    op.add_column("settings", sa.Column("version_id", sa.BigInteger(), nullable=False, server_default="1"))
    if op.get_bind().dialect.name != "postgresql":
        return
    op.execute("""
        CREATE FUNCTION public.increment_setting_version() RETURNS trigger LANGUAGE plpgsql
        SET search_path=pg_catalog,pg_temp AS $$
        BEGIN NEW.version_id := OLD.version_id + 1; RETURN NEW; END $$;
        REVOKE ALL ON FUNCTION public.increment_setting_version() FROM PUBLIC;
        CREATE TRIGGER increment_setting_version BEFORE UPDATE ON public.settings
            FOR EACH ROW EXECUTE FUNCTION public.increment_setting_version();
        CREATE FUNCTION public.update_business_setting(setting_key text,new_value text,expected_version bigint)
            RETURNS json LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
        DECLARE stored public.settings%ROWTYPE;
        BEGIN
            IF right(lower(setting_key),7)='_writer' THEN
                RAISE EXCEPTION 'Writer controls require controlled deployment' USING ERRCODE='42501';
            END IF;
            IF setting_key IS NULL OR length(setting_key)<1 OR length(setting_key)>50
                OR new_value IS NULL OR length(new_value)>200 OR expected_version IS NULL OR expected_version<1 THEN
                RAISE EXCEPTION 'Invalid business setting command' USING ERRCODE='22023';
            END IF;
            IF public.lock_catalog_writer() IS DISTINCT FROM 'dotnet'
                OR current_setting('rcm.catalog_writer',true) IS DISTINCT FROM 'dotnet' THEN
                RAISE EXCEPTION 'Catalog writer is disabled for this application' USING ERRCODE='55000';
            END IF;
            SELECT * INTO stored FROM public.settings WHERE key=setting_key FOR UPDATE;
            IF NOT FOUND THEN RAISE EXCEPTION 'Setting not found' USING ERRCODE='P0002'; END IF;
            IF stored.version_id<>expected_version THEN
                RAISE EXCEPTION 'Setting version changed' USING ERRCODE='40001';
            END IF;
            UPDATE public.settings SET value=new_value WHERE key=setting_key RETURNING * INTO stored;
            RETURN json_build_object('key',stored.key,'value',stored.value,'label',stored.label,'version',stored.version_id);
        END $$;
        REVOKE ALL ON FUNCTION public.update_business_setting(text,text,bigint) FROM PUBLIC;
    """)


def downgrade():
    raise RuntimeError("Retain settings versions, controlled mutation and receipts; reverse catalog ownership without removing data.")
