from psycopg import sql


def grant_shift_reports(cursor, runtime_user):
    cursor.execute("SELECT rolcanlogin,rolsuper,rolcreatedb,rolcreaterole,rolbypassrls,rolreplication,rolinherit FROM pg_roles WHERE rolname=%s", (runtime_user,))
    role = cursor.fetchone()
    if role is None or not role[0] or any(role[1:]):
        raise SystemExit("Identify a restricted non-inheriting shift report runtime login.")
    cursor.execute("SELECT 1 FROM pg_auth_members WHERE member=(SELECT oid FROM pg_roles WHERE rolname=%s)", (runtime_user,))
    if cursor.fetchone():
        raise SystemExit("Shift report runtime must not belong to another role.")
    for table, privileges in (("users", "SELECT,INSERT,UPDATE,DELETE"), ("orders", "SELECT,INSERT,UPDATE,DELETE"),
                              ("product_templates", "SELECT,INSERT,UPDATE,DELETE"), ("approved_materials", "SELECT,INSERT,UPDATE,DELETE"),
                              ("operation_catalog", "SELECT,INSERT,UPDATE,DELETE"), ("settings", "INSERT,UPDATE,DELETE"),
                              ("shift_report_audit", "UPDATE,DELETE"), ("shift_report_command_receipts", "UPDATE,DELETE")):
        cursor.execute("SELECT has_table_privilege(%s,%s,%s)", (runtime_user, "public." + table, privileges))
        if cursor.fetchone()[0]:
            raise SystemExit("Runtime has privileges outside the shift report module.")
    for column in ("id", "report_id", "actor_id", "actor_name", "action", "reason", "before", "created_at"):
        cursor.execute("SELECT has_column_privilege(%s,'public.shift_report_audit',%s,'UPDATE')", (runtime_user, column))
        if cursor.fetchone()[0]:
            raise SystemExit("Runtime must preserve audit attribution and original snapshots.")
    cursor.execute(sql.SQL("GRANT USAGE ON SCHEMA public TO {}").format(sql.Identifier(runtime_user)))
    for table, privileges in (("shift_reports", "SELECT,INSERT,UPDATE,DELETE"),
                              ("shift_report_audit", "SELECT,INSERT"),
                              ("shift_report_command_receipts", "SELECT,INSERT"), ("settings", "SELECT")):
        cursor.execute(sql.SQL("GRANT {} ON public.{} TO {}").format(sql.SQL(privileges), sql.Identifier(table), sql.Identifier(runtime_user)))
    cursor.execute(sql.SQL('GRANT UPDATE ("after") ON public.shift_report_audit TO {}').format(sql.Identifier(runtime_user)))
    for table in ("shift_reports", "shift_report_audit"):
        cursor.execute("SELECT pg_get_serial_sequence(%s,'id')", ("public." + table,))
        sequence = cursor.fetchone()[0]
        if not sequence:
            raise SystemExit("Existing shift report ID sequences are required.")
        cursor.execute(sql.SQL("GRANT USAGE ON SEQUENCE {} TO {}").format(sql.Identifier(*sequence.split(".", 1)), sql.Identifier(runtime_user)))
    cursor.execute(sql.SQL("GRANT EXECUTE ON FUNCTION public.lock_shift_reports_writer() TO {}").format(sql.Identifier(runtime_user)))
