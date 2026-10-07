from psycopg import sql


REVIEW_RIGHTS = {
    "Contracts": {"SELECT"},
    "ReportLinks": {"SELECT", "INSERT", "UPDATE"},
    "ReportReviews": {"SELECT", "INSERT"},
    "ReportReviewAudit": {"SELECT", "INSERT"},
    "ReviewReceipts": {"SELECT", "INSERT"},
    "ReviewerAssignments": {"SELECT"},
}


def grant_reviews(cursor, runtime_user):
    cursor.execute("SELECT oid,rolcanlogin,rolsuper,rolcreatedb,rolcreaterole,rolbypassrls,rolreplication,rolinherit FROM pg_roles WHERE rolname=%s", (runtime_user,))
    role = cursor.fetchone()
    if role is None or not role[1] or any(role[2:]):
        raise SystemExit("Identify the existing restricted shift report runtime login.")
    cursor.execute("SELECT 1 FROM pg_auth_members WHERE member=%s", (role[0],))
    if cursor.fetchone():
        raise SystemExit("Review runtime must not belong to another role.")
    cursor.execute("SELECT has_database_privilege(%s,current_database(),'CREATE')", (runtime_user,))
    if cursor.fetchone()[0]:
        raise SystemExit("Review runtime must not create database objects.")
    cursor.execute("SELECT 1 FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname <> 'information_schema' AND has_schema_privilege(%s,oid,'CREATE')", (runtime_user,))
    if cursor.fetchone():
        raise SystemExit("Review runtime must not create schema objects.")
    cursor.execute("SELECT bool_and(has_table_privilege(%s,'public.shift_reports',privilege_name)) FROM unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE']) AS rights(privilege_name)", (runtime_user,))
    if not cursor.fetchone()[0]:
        raise SystemExit("Configure the existing shift report runtime first.")
    cursor.execute("SELECT c.relname,c.relowner FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='production' AND c.relkind IN ('r','p','v','m','f')")
    for table, owner in cursor.fetchall():
        if owner == role[0]:
            raise SystemExit("Review runtime must not own production tables.")
        allowed = REVIEW_RIGHTS.get(table, set())
        name = sql.Identifier("production", table).as_string(cursor)
        for privilege in ("SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"):
            if privilege in allowed:
                continue
            cursor.execute("SELECT has_table_privilege(%s,%s,%s)", (runtime_user, name, privilege))
            if cursor.fetchone()[0]:
                raise SystemExit("Runtime has production privileges outside the review allowlist.")
            if privilege in ("SELECT", "INSERT", "UPDATE", "REFERENCES"):
                cursor.execute("SELECT has_any_column_privilege(%s,%s,%s)", (runtime_user, name, privilege))
                if cursor.fetchone()[0]:
                    raise SystemExit("Runtime has production column privileges outside the review allowlist.")
    cursor.execute(sql.SQL("GRANT USAGE ON SCHEMA production TO {}").format(sql.Identifier(runtime_user)))
    for table, rights in REVIEW_RIGHTS.items():
        cursor.execute(sql.SQL("GRANT {} ON production.{} TO {}").format(sql.SQL(",".join(sorted(rights))), sql.Identifier(table), sql.Identifier(runtime_user)))


def assign_reviewer(cursor, user_id, active, operator, reason):
    if user_id <= 0 or not operator.strip() or len(operator) > 240 or not reason.strip() or len(reason) > 2000:
        raise SystemExit("A positive existing user ID, bounded operator identifier and reason are required.")
    cursor.execute("""SELECT role::text, (nullif(pin_hash,'') IS NOT NULL OR nullif(password_hash,'') IS NOT NULL)
        FROM public.users WHERE id=%s FOR SHARE""", (user_id,))
    user = cursor.fetchone()
    if user is None:
        raise SystemExit("The identified ERP user does not exist.")
    if active and (user[0] not in {"biuro", "technolog", "produkcja", "ceo"} or not user[1]):
        raise SystemExit("Reviewer assignment requires an existing login-enabled user with shift report access.")
    cursor.execute("SELECT pg_advisory_xact_lock(hashtextextended('production-reviewer:' || %s::text,0))", (user_id,))
    cursor.execute('SELECT "Active" FROM production."ReviewerAssignments" WHERE "UserId"=%s FOR UPDATE', (user_id,))
    current = cursor.fetchone()
    if current is not None and current[0] == active:
        return False
    if current is None and not active:
        raise SystemExit("The identified user has no reviewer assignment to revoke.")
    cursor.execute("""INSERT INTO production."ReviewerAssignments"("UserId","Version","Active","ConfiguredBy","Reason","ChangedAt")
        VALUES(%s,1,%s,%s,%s,clock_timestamp())
        ON CONFLICT("UserId") DO UPDATE SET "Version"=production."ReviewerAssignments"."Version"+1,
            "Active"=excluded."Active","ConfiguredBy"=excluded."ConfiguredBy","Reason"=excluded."Reason","ChangedAt"=excluded."ChangedAt"
        """, (user_id, active, operator.strip(), reason.strip()))
    return True
