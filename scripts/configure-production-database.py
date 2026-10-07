from psycopg import sql


MUTABLE = ("Contracts", "Deliveries")


APPEND_ONLY = ("Changes", "Receipts")


HISTORY = "__EFMigrationsHistory"


def grant_production(cursor, runtime_user):
    cursor.execute("SELECT oid,rolcanlogin,rolsuper,rolcreatedb,rolcreaterole,rolbypassrls,rolreplication,rolinherit FROM pg_roles WHERE rolname=%s", (runtime_user,))
    role = cursor.fetchone()
    if role is None or not role[1] or any(role[2:]):
        raise SystemExit("Identify a restricted non-inheriting production runtime login.")
    cursor.execute("SELECT 1 FROM pg_auth_members WHERE member=%s", (role[0],))
    if cursor.fetchone():
        raise SystemExit("Production runtime must not belong to another role.")
    cursor.execute("SELECT has_database_privilege(%s,current_database(),'CREATE')", (runtime_user,))
    if cursor.fetchone()[0]:
        raise SystemExit("Production runtime must not create database objects.")
    cursor.execute("SELECT 1 FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname <> 'information_schema' AND has_schema_privilege(%s,oid,'CREATE')", (runtime_user,))
    if cursor.fetchone():
        raise SystemExit("Production runtime must not create schema objects.")
    cursor.execute("SELECT n.nspname,c.relname,c.relowner FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND c.relkind IN ('r','p','v','m','f')")
    for schema, table, owner in cursor.fetchall():
        if owner == role[0]:
            raise SystemExit("Production runtime must not own application tables.")
        allowed = set()
        if schema == "production":
            if table in MUTABLE:
                allowed = {"SELECT", "INSERT", "UPDATE"}
            elif table in APPEND_ONLY:
                allowed = {"SELECT", "INSERT"}
            elif table == HISTORY:
                allowed = {"SELECT"}
        name = sql.Identifier(schema, table).as_string(cursor)
        for privilege in ("SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"):
            if privilege in allowed:
                continue
            cursor.execute("SELECT has_table_privilege(%s,%s,%s)", (runtime_user, name, privilege))
            if cursor.fetchone()[0]:
                raise SystemExit("Runtime has privileges outside the production allowlist.")
            if privilege in ("SELECT", "INSERT", "UPDATE", "REFERENCES"):
                cursor.execute("SELECT has_any_column_privilege(%s,%s,%s)", (runtime_user, name, privilege))
                if cursor.fetchone()[0]:
                    raise SystemExit("Runtime has column privileges outside the production allowlist.")
    cursor.execute(sql.SQL("GRANT USAGE ON SCHEMA production TO {}").format(sql.Identifier(runtime_user)))
    for table in MUTABLE:
        cursor.execute(sql.SQL("GRANT SELECT,INSERT,UPDATE ON production.{} TO {}").format(sql.Identifier(table), sql.Identifier(runtime_user)))
    for table in APPEND_ONLY:
        cursor.execute(sql.SQL("GRANT SELECT,INSERT ON production.{} TO {}").format(sql.Identifier(table), sql.Identifier(runtime_user)))
    cursor.execute(sql.SQL("GRANT SELECT ON production.{} TO {}").format(sql.Identifier(HISTORY), sql.Identifier(runtime_user)))
