using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeShiftReports
{
    public async Task CheckRuntime(CancellationToken ct)
    {
        await using var db = await Open(ct);
        var privileges = new List<string>();
        foreach (var (table, rights) in new[]
        {
            ("shift_reports", new[] { "SELECT", "INSERT", "UPDATE", "DELETE" }),
            ("shift_report_audit", new[] { "SELECT", "INSERT" }),
            ("shift_report_command_receipts", new[] { "SELECT", "INSERT" }),
            ("settings", new[] { "SELECT" })
        }) foreach (var right in rights) privileges.Add($"has_table_privilege(current_user,'public.{table}','{right}')");
        privileges.Add("NOT has_table_privilege(current_user,'public.settings','INSERT,UPDATE,DELETE')");
        foreach (var table in new[] { "users", "orders", "approved_materials", "operation_catalog", "product_templates" })
            privileges.Add($"NOT has_table_privilege(current_user,'public.{table}','SELECT,INSERT,UPDATE,DELETE')");
        foreach (var column in new[] { "id", "report_id", "actor_id", "actor_name", "action", "reason", "before", "created_at" })
            privileges.Add($"NOT has_column_privilege(current_user,'public.shift_report_audit','{column}','UPDATE')");
        var ready = (await Read<bool>(db, """
            SELECT to_json(NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolbypassrls
                AND NOT r.rolreplication AND NOT r.rolinherit
                AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid)
                AND NOT has_table_privilege(current_user,'public.users','SELECT')
                AND NOT has_table_privilege(current_user,'public.shift_report_audit','UPDATE,DELETE')
                AND has_column_privilege(current_user,'public.shift_report_audit','after','UPDATE')
                AND NOT has_table_privilege(current_user,'public.shift_report_command_receipts','UPDATE,DELETE')
                AND has_sequence_privilege(current_user,pg_get_serial_sequence('public.shift_reports','id'),'USAGE')
                AND has_sequence_privilege(current_user,pg_get_serial_sequence('public.shift_report_audit','id'),'USAGE')
                AND has_function_privilege(current_user,'public.lock_shift_reports_writer()','EXECUTE')
                AND (SELECT count(*) FROM pg_trigger WHERE tgname='require_shift_reports_writer' AND tgenabled='O'
                    AND tgrelid IN ('public.shift_reports'::regclass,'public.shift_report_audit'::regclass,'public.shift_report_command_receipts'::regclass))=3
                AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgname='increment_shift_report_version' AND tgenabled='O' AND tgrelid='public.shift_reports'::regclass)
                AND
            """ + " " + string.Join(" AND ", privileges) + ") FROM pg_roles r WHERE r.rolname=current_user", ct)).SingleOrDefault();
        if (!ready) throw new CrmFault(503, "Skonfiguruj ograniczone uprawnienia raportów zmianowych.");
    }
}
