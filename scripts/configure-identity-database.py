from psycopg import sql


USER_COLUMNS = ("id", "name", "role", "default_shift", "pin_hash", "username", "password_hash", "password_version")


SESSION_COLUMNS = ("id", "token_hash", "user_id", "password_version", "created_at", "last_used_at", "expires_at", "revoked_at")


SESSION_UPDATES = ("last_used_at", "expires_at", "revoked_at")


def grant_identity(cursor, runtime_user):
    cursor.execute("SELECT rolcanlogin,rolsuper,rolcreatedb,rolcreaterole,rolbypassrls,rolreplication,rolinherit FROM pg_roles WHERE rolname=%s", (runtime_user,))
    role = cursor.fetchone()
    if role is None or not role[0] or any(role[1:]):
        raise SystemExit("Identify a restricted non-inheriting identity runtime login.")
    cursor.execute("SELECT 1 FROM pg_auth_members WHERE member=(SELECT oid FROM pg_roles WHERE rolname=%s)", (runtime_user,))
    if cursor.fetchone():
        raise SystemExit("Identity runtime must not belong to another role.")
    cursor.execute("SELECT table_name,column_name FROM information_schema.columns WHERE table_schema='public'")
    for table, column in cursor.fetchall():
        allowed_read = table == "remembered_sessions" or table == "users" and column in USER_COLUMNS
        if not allowed_read:
            cursor.execute("SELECT has_column_privilege(%s,%s,%s,'SELECT')", (runtime_user, "public." + table, column))
            if cursor.fetchone()[0]:
                raise SystemExit("Runtime has read privileges outside identity.")
        allowed_insert = table == "remembered_sessions" and column in SESSION_COLUMNS
        allowed_update = table == "remembered_sessions" and column in SESSION_UPDATES
        for privilege, allowed in (("INSERT", allowed_insert), ("UPDATE", allowed_update)):
            if not allowed:
                cursor.execute("SELECT has_column_privilege(%s,%s,%s,%s)", (runtime_user, "public." + table, column, privilege))
                if cursor.fetchone()[0]:
                    raise SystemExit("Runtime has mutation privileges outside identity sessions.")
        cursor.execute("SELECT has_table_privilege(%s,%s,'DELETE,TRUNCATE,REFERENCES,TRIGGER')", (runtime_user, "public." + table))
        if cursor.fetchone()[0]:
            raise SystemExit("Runtime must retain credentials, session identity and revocations.")
    role_identifier = sql.Identifier(runtime_user)
    cursor.execute(sql.SQL("GRANT USAGE ON SCHEMA public TO {}").format(role_identifier))
    cursor.execute(sql.SQL("GRANT SELECT ({}) ON public.users TO {}").format(sql.SQL(",").join(map(sql.Identifier, USER_COLUMNS)), role_identifier))
    cursor.execute(sql.SQL("GRANT SELECT ON public.remembered_sessions TO {}").format(role_identifier))
    cursor.execute(sql.SQL("GRANT INSERT ({}) ON public.remembered_sessions TO {}").format(sql.SQL(",").join(map(sql.Identifier, SESSION_COLUMNS)), role_identifier))
    cursor.execute(sql.SQL("GRANT UPDATE ({}) ON public.remembered_sessions TO {}").format(sql.SQL(",").join(map(sql.Identifier, SESSION_UPDATES)), role_identifier))
    cursor.execute(sql.SQL("GRANT EXECUTE ON FUNCTION public.require_identity_writer(text) TO {}").format(role_identifier))
