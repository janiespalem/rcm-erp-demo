using System.Text.Json;
using Npgsql;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeShiftReports
{
    public Task<ShiftReportDto> Create(LegacyUser actor, CreateShiftReport command, CancellationToken ct)
    {
        if (command.Shift is not ("I" or "II")) throw CrmFault.Invalid("shift", "Wybierz zmianę I lub II.");
        if (command.ReportDate == DateOnly.MinValue) throw CrmFault.Invalid("reportDate", "Podaj datę raportu.");
        return Change(actor, command.RequestId, "create", null, command, async db =>
        {
            await Execute(db, "SELECT pg_advisory_xact_lock(@key)", ct, P("key", LockKey(command.ReportDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ":" + command.Shift)));
            var existing = (await Read<ReportRow>(db, "SELECT " + ReportJson + " FROM public.shift_reports WHERE report_date=@day AND shift=@shift AND deleted_at IS NULL FOR UPDATE", ct,
                P("day", command.ReportDate), P("shift", command.Shift))).SingleOrDefault();
            if (existing is not null) return Output(existing);
            var fields = new ShiftReportFields { Leader = actor.Name, Responsible = actor.Name, ProductionPerson = actor.Name };
            var now = clock.GetUtcNow();
            var row = (await Read<ReportRow>(db, """
                INSERT INTO public.shift_reports(report_date,shift,author_id,author_name,status,version_id,schema_version,fields,created_at,updated_at,correction_count)
                VALUES(@day,@shift,@actor,@name,'draft',1,1,@fields::json,@now,@now,0) RETURNING
                """ + " " + ReportJson, ct, P("day", command.ReportDate), P("shift", command.Shift), P("actor", actor.Id), P("name", actor.Name),
                P("fields", JsonSerializer.Serialize(fields, ShiftReportValidation.StoredJson)), P("now", now)))[0];
            await Record(db, row, actor, "created", null, null, ct);
            return Output(row);
        }, ct);
    }
    public Task<ShiftReportDto> Save(LegacyUser actor, long id, SaveShiftReport command, CancellationToken ct)
    {
        RequireVersion(command.ExpectedVersion); var fields = ShiftReportValidation.Normalize(command.Fields);
        return Change(actor, command.RequestId, "save", id, command, async db =>
        {
            var before = await Get(db, id, false, true, ct); CheckVersion(before, actor, command.ExpectedVersion);
            var row = (await Read<ReportRow>(db, "UPDATE public.shift_reports SET fields=@fields::json,updated_at=@now,version_id=version_id+1 WHERE id=@id RETURNING " + ReportJson, ct,
                P("id", id), P("fields", JsonSerializer.Serialize(fields, ShiftReportValidation.StoredJson)), P("now", clock.GetUtcNow())))[0];
            await Record(db, row, actor, "saved", Snapshot(before), null, ct); return Output(row);
        }, ct);
    }
    public Task<ShiftReportDto> Finalize(LegacyUser actor, long id, ShiftReportVersionCommand command, CancellationToken ct)
    {
        RequireVersion(command.ExpectedVersion);
        return Change(actor, command.RequestId, "finalize", id, command, async db =>
        {
            var before = await Get(db, id, false, true, ct); CheckVersion(before, actor, command.ExpectedVersion);
            ShiftReportValidation.RequireComplete(ShiftReportValidation.ReadFields(before.Fields, before.SchemaVersion));
            var row = (await Read<ReportRow>(db, """
                UPDATE public.shift_reports SET status='finalized',finalized_at=@now,updated_at=@now,
                    finalized_by_id=@actor,finalized_by_name=@name,version_id=version_id+1 WHERE id=@id RETURNING
                """ + " " + ReportJson, ct, P("id", id), P("now", clock.GetUtcNow()), P("actor", actor.Id), P("name", actor.Name)))[0];
            await Record(db, row, actor, "finalized", Snapshot(before), null, ct); return Output(row);
        }, ct);
    }
    public Task<ShiftReportDto> Correct(LegacyUser actor, long id, CorrectShiftReport command, CancellationToken ct)
    {
        RequireVersion(command.ExpectedVersion); var fields = ShiftReportValidation.Normalize(command.Fields);
        var reason = ShiftReportValidation.Reason(command.Reason); ShiftReportValidation.RequireComplete(fields);
        return Change(actor, command.RequestId, "correct", id, command, async db =>
        {
            var before = await Get(db, id, false, true, ct); CheckVersion(before, actor, command.ExpectedVersion, false);
            if (before.Status == "draft") throw new CrmFault(409, "Najpierw zakończ raport.");
            var row = (await Read<ReportRow>(db, """
                UPDATE public.shift_reports SET fields=@fields::json,status='corrected',correction_count=correction_count+1,
                    updated_at=@now,version_id=version_id+1 WHERE id=@id RETURNING
                """ + " " + ReportJson, ct, P("id", id), P("fields", JsonSerializer.Serialize(fields, ShiftReportValidation.StoredJson)), P("now", clock.GetUtcNow())))[0];
            await Record(db, row, actor, "corrected", Snapshot(before), reason, ct); return Output(row);
        }, ct);
    }
    public Task<ShiftReportMutationResult> Discard(LegacyUser actor, long id, ShiftReportVersionCommand command, CancellationToken ct)
    {
        RequireVersion(command.ExpectedVersion);
        return Change(actor, command.RequestId, "discard", id, command, async db =>
        {
            var row = await Get(db, id, false, true, ct); CheckVersion(row, actor, command.ExpectedVersion);
            if (ShiftReportValidation.CompletionErrors(ShiftReportValidation.ReadFields(row.Fields, row.SchemaVersion)).Count == 0)
                throw new CrmFault(409, "Raport jest kompletny. Zakończ go zamiast usuwać.");
            await Execute(db, "DELETE FROM public.shift_reports WHERE id=@id", ct, P("id", id));
            return new ShiftReportMutationResult(id, row.Version + 1, true);
        }, ct);
    }
    public Task<ShiftReportMutationResult> AdminDelete(LegacyUser actor, long id, AdminDeleteShiftReport command, CancellationToken ct)
    {
        RequireVersion(command.ExpectedVersion); var reason = ShiftReportValidation.Reason(command.Reason);
        return Change(actor, command.RequestId, "admin_delete", id, command, async db =>
        {
            var before = await Get(db, id, false, true, ct); CheckVersion(before, actor, command.ExpectedVersion, false, true);
            var row = (await Read<ReportRow>(db, "UPDATE public.shift_reports SET deleted_at=@now,updated_at=@now,version_id=version_id+1 WHERE id=@id RETURNING " + ReportJson, ct,
                P("id", id), P("now", clock.GetUtcNow())))[0];
            await Record(db, row, actor, "deleted", Snapshot(before), reason, ct);
            return new ShiftReportMutationResult(id, row.Version, true);
        }, ct, admin: true);
    }
    private static async Task Record(NpgsqlConnection db, ReportRow row, LegacyUser actor, string action, JsonElement? before, string? reason, CancellationToken ct)
    {
        var after = Snapshot(row); AuditRow? previous = null;
        if (action == "saved")
        {
            previous = (await Read<AuditRow>(db, "SELECT json_build_object('id',id,'actor',actor_id,'action',action,'after',\"after\") FROM public.shift_report_audit WHERE report_id=@id ORDER BY id DESC LIMIT 1", ct, P("id", row.Id))).SingleOrDefault();
            var count = 0;
            var coalesce = previous is not null && previous.Action == "saved" && previous.Actor == actor.Id
                && previous.After.TryGetProperty("schema_version", out var schema) && schema.TryGetInt32(out var version) && version == row.SchemaVersion
                && previous.After.TryGetProperty("draft_save_count", out var saves) && saves.TryGetInt32(out count) && count > 0;
            if (!coalesce) { previous = null; count = 0; }
            var snapshot = after.Deserialize<Dictionary<string, JsonElement>>()!;
            snapshot["draft_save_count"] = JsonSerializer.SerializeToElement(checked(count + 1));
            snapshot["last_saved_at"] = JsonSerializer.SerializeToElement(row.UpdatedAt);
            after = JsonSerializer.SerializeToElement(snapshot);
        }
        if (previous is not null)
        {
            await Execute(db, "UPDATE public.shift_report_audit SET \"after\"=@after::json WHERE id=@id", ct, P("id", previous.Id), P("after", after.GetRawText()));
            return;
        }
        await Execute(db, """
            INSERT INTO public.shift_report_audit(report_id,actor_id,actor_name,action,reason,"before","after",created_at)
            VALUES(@report,@actor,@name,@action,@reason,@before::json,@after::json,@now)
            """, ct, P("report", row.Id), P("actor", actor.Id), P("name", actor.Name), P("action", action), P("reason", reason),
            P("before", before?.GetRawText()), P("after", after.GetRawText()), P("now", row.UpdatedAt));
    }
    private sealed record AuditRow(long Id, long Actor, string Action, JsonElement After);
}
