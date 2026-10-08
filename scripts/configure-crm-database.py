from psycopg import sql


def grant_order_attachments(cursor, runtime_user):
    cursor.execute("SELECT rolcanlogin, rolsuper, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname = %s", (runtime_user,))
    role = cursor.fetchone()
    if role is None or not role[0] or any(role[1:]):
        raise SystemExit("Identify an existing restricted orders runtime login.")
    cursor.execute("""
        SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.oid = pg_get_serial_sequence('public.order_attachments', 'id')::regclass
    """)
    sequence = cursor.fetchone()
    if sequence is None:
        raise SystemExit("The existing attachment ID sequence is required.")
    cursor.execute(sql.SQL("GRANT USAGE ON SCHEMA public TO {}").format(sql.Identifier(runtime_user)))
    cursor.execute(sql.SQL("GRANT SELECT, INSERT, DELETE ON public.order_attachments TO {}").format(sql.Identifier(runtime_user)))
    cursor.execute(sql.SQL("GRANT USAGE ON SEQUENCE {} TO {}").format(sql.Identifier(*sequence), sql.Identifier(runtime_user)))


def grant_orders(cursor, runtime_user):
    grant_order_attachments(cursor, runtime_user)
    read_tables = ("orders", "quotes", "order_events", "order_counters", "order_create_receipts",
                   "order_command_receipts", "product_templates", "approved_materials", "operation_catalog",
                   "constraint_rules", "settings", "order_operations", "parameter_requests", "stock_movements", "price_history")
    for table in read_tables:
        cursor.execute(sql.SQL("GRANT SELECT ON public.{} TO {}").format(sql.Identifier(table), sql.Identifier(runtime_user)))
    for table in ("orders", "quotes", "order_counters", "order_operations", "parameter_requests"):
        cursor.execute(sql.SQL("GRANT INSERT, UPDATE ON public.{} TO {}").format(sql.Identifier(table), sql.Identifier(runtime_user)))
    for table in ("order_events", "order_create_receipts", "order_command_receipts", "product_templates", "stock_movements", "price_history"):
        cursor.execute(sql.SQL("GRANT INSERT ON public.{} TO {}").format(sql.Identifier(table), sql.Identifier(runtime_user)))
    for table in ("orders", "quotes", "order_events", "product_templates", "order_operations", "parameter_requests", "stock_movements", "price_history"):
        cursor.execute("SELECT pg_get_serial_sequence(%s, 'id')", ("public." + table,))
        sequence = cursor.fetchone()[0]
        if sequence:
            cursor.execute(sql.SQL("GRANT USAGE ON SEQUENCE {} TO {}").format(sql.Identifier(*sequence.split(".", 1)), sql.Identifier(runtime_user)))
    cursor.execute(sql.SQL("GRANT EXECUTE ON FUNCTION public.lock_order_writer() TO {}").format(sql.Identifier(runtime_user)))


def grant_catalog(cursor, runtime_user):
    cursor.execute("SELECT rolcanlogin,rolsuper,rolcreatedb,rolcreaterole FROM pg_roles WHERE rolname=%s", (runtime_user,))
    role = cursor.fetchone()
    if role is None or not role[0] or any(role[1:]):
        raise SystemExit("Identify an existing restricted catalog runtime login.")
    cursor.execute(sql.SQL("GRANT USAGE ON SCHEMA public TO {}").format(sql.Identifier(runtime_user)))
    for table, privileges in (("approved_materials", "SELECT,INSERT,UPDATE"),
                              ("operation_catalog", "SELECT,INSERT,UPDATE,DELETE"),
                              ("product_templates", "SELECT,INSERT,UPDATE"),
                              ("catalog_command_receipts", "SELECT,INSERT"), ("settings", "SELECT")):
        cursor.execute(sql.SQL("GRANT {} ON public.{} TO {}").format(sql.SQL(privileges), sql.Identifier(table), sql.Identifier(runtime_user)))
    for table in ("approved_materials", "operation_catalog", "product_templates"):
        cursor.execute("SELECT pg_get_serial_sequence(%s,'id')", ("public." + table,))
        sequence = cursor.fetchone()[0]
        if sequence:
            cursor.execute(sql.SQL("GRANT USAGE ON SEQUENCE {} TO {}").format(sql.Identifier(*sequence.split(".", 1)), sql.Identifier(runtime_user)))
    cursor.execute(sql.SQL("GRANT EXECUTE ON FUNCTION public.lock_catalog_writer() TO {}").format(sql.Identifier(runtime_user)))
    cursor.execute(sql.SQL("GRANT EXECUTE ON FUNCTION public.update_business_setting(text,text,bigint) TO {}").format(sql.Identifier(runtime_user)))
