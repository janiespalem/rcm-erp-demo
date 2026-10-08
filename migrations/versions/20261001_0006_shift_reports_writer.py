"""Fence shift report writers and retain atomic command receipts."""
from alembic import op
import sqlalchemy as sa

revision = "20261001_0006"
down_revision = "20261001_0005"
branch_labels = None
depends_on = None


def upgrade():
    op.create_table(
        "shift_report_command_receipts",
        sa.Column("request_id", sa.String(36), primary_key=True),
        sa.Column("actor_id", sa.Integer(), nullable=False),
        sa.Column("command_type", sa.String(64), nullable=False),
        sa.Column("target_id", sa.BigInteger()),
        sa.Column("payload_hash", sa.String(64), nullable=False),
        sa.Column("response_json", sa.JSON(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
    )
    op.execute("INSERT INTO settings(key,value,label) VALUES ('shift_reports_writer','legacy','Shift reports writer') ON CONFLICT(key) DO NOTHING")
    if op.get_bind().dialect.name != "postgresql":
        return
    op.execute("""
        CREATE FUNCTION public.lock_shift_reports_writer() RETURNS text LANGUAGE plpgsql
        SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
        DECLARE owner text;
        BEGIN
            SELECT value INTO owner FROM public.settings WHERE key='shift_reports_writer' FOR SHARE;
            RETURN owner;
        END $$;
        REVOKE ALL ON FUNCTION public.lock_shift_reports_writer() FROM PUBLIC;
        CREATE FUNCTION public.require_shift_reports_writer() RETURNS trigger LANGUAGE plpgsql
        SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
        DECLARE owner text;
        BEGIN
            owner := public.lock_shift_reports_writer();
            IF owner IS NULL OR owner <> coalesce(nullif(current_setting('rcm.shift_reports_writer',true),''),'legacy') THEN
                RAISE EXCEPTION 'Shift report writer is disabled for this application' USING ERRCODE='55000';
            END IF;
            RETURN NULL;
        END $$;
        REVOKE ALL ON FUNCTION public.require_shift_reports_writer() FROM PUBLIC;
        CREATE FUNCTION public.increment_shift_report_version() RETURNS trigger LANGUAGE plpgsql
        SET search_path=pg_catalog,pg_temp AS $$
        BEGIN NEW.version_id := OLD.version_id + 1; RETURN NEW; END $$;
        REVOKE ALL ON FUNCTION public.increment_shift_report_version() FROM PUBLIC;
    """)
    for table in ("shift_reports", "shift_report_audit", "shift_report_command_receipts"):
        op.execute(f"CREATE TRIGGER require_shift_reports_writer BEFORE INSERT OR UPDATE OR DELETE ON public.{table} FOR EACH STATEMENT EXECUTE FUNCTION public.require_shift_reports_writer()")
    op.execute("CREATE TRIGGER increment_shift_report_version BEFORE UPDATE ON public.shift_reports FOR EACH ROW EXECUTE FUNCTION public.increment_shift_report_version()")


def downgrade():
    raise RuntimeError("Retain shift reports, audit, receipts and writer fencing; reverse application ownership without removing data.")
