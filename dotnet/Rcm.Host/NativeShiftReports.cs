using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeShiftReports(IConfiguration configuration, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ReportJson = """
        json_build_object('id',id,'reportDate',report_date,'shift',shift,'authorId',author_id,'authorName',author_name,
            'status',status,'version',version_id,'schemaVersion',schema_version,'fields',fields,'createdAt',created_at,
            'updatedAt',updated_at,'finalizedAt',finalized_at,'finalizedById',finalized_by_id,
            'finalizedByName',finalized_by_name,'correctionCount',correction_count,'deletedAt',deleted_at)
        """;
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration.GetConnectionString("ShiftReports") ?? throw new CrmFault(503, "Raporty zmianowe nie zostały skonfigurowane."));
        try { await db.OpenAsync(ct); return db; } catch { await db.DisposeAsync(); throw; }
    }
    private static NpgsqlParameter P(string name, object? value) => value is null ? new(name, NpgsqlDbType.Unknown) { Value = DBNull.Value } : new(name, value);
    private static async Task<T[]> Read<T>(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct); List<T> result = [];
        while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return result.ToArray();
    }
    private static async Task Execute(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    { await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync(ct); }
    internal static bool Admin(LegacyUser actor) => actor.Role is "technolog" or "ceo";
    internal static void RequireRead(LegacyUser actor)
    { if (actor.Id <= 0 || actor.Role is not ("produkcja" or "technolog" or "ceo" or "biuro")) throw new CrmFault(403, "Brak dostępu do raportów zmianowych."); }
    private static void RequireWrite(LegacyUser actor, bool admin = false)
    {
        RequireRead(actor);
        if (admin ? !Admin(actor) : actor.Role is not ("produkcja" or "technolog"))
            throw new CrmFault(403, "Brak uprawnień do zmiany raportu.");
    }
    internal static void QueryValidation(DateOnly? from, DateOnly? to, string? shift, int page = 1, int pageSize = 50)
    {
        if (shift is not (null or "I" or "II")) throw CrmFault.Invalid("shift", "Wybierz zmianę I lub II.");
        if (from > to) throw CrmFault.Invalid("dateTo", "Data końcowa nie może poprzedzać początkowej.");
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100) throw CrmFault.Invalid("page", "Nieprawidłowa strona (rozmiar od 1 do 100).");
    }
    private static NpgsqlParameter[] Filters(DateOnly? from, DateOnly? to, string? shift, bool deleted = false) =>
        [P("from", from), P("to", to), P("shift", shift), P("deleted", deleted)];
    private const string Filter = "WHERE ((@deleted AND deleted_at IS NOT NULL) OR (NOT @deleted AND deleted_at IS NULL)) AND (@from::date IS NULL OR report_date>=@from) AND (@to::date IS NULL OR report_date<=@to) AND (@shift::text IS NULL OR shift=@shift)";
    public async Task<Page<ShiftReportDto>> List(LegacyUser actor, DateOnly? from, DateOnly? to, string? shift, bool deleted, int page, int pageSize, CancellationToken ct)
    {
        RequireRead(actor); QueryValidation(from, to, shift, page, pageSize);
        if (deleted && !Admin(actor)) throw new CrmFault(403, "Usunięte raporty są dostępne tylko dla administratora.");
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var total = (await Read<int>(db, "SELECT to_json(count(*)::int) FROM public.shift_reports " + Filter, ct, Filters(from, to, shift, deleted)))[0];
        var rows = await Read<ReportRow>(db, "SELECT " + ReportJson + " FROM public.shift_reports " + Filter + " ORDER BY report_date DESC,shift,id LIMIT @limit OFFSET @offset", ct,
            [.. Filters(from, to, shift, deleted), P("limit", pageSize), P("offset", (page - 1) * pageSize)]);
        return new(rows.Select(Output).ToArray(), total, page, pageSize);
    }
    private static async Task<ReportRow> Get(NpgsqlConnection db, long id, bool deleted, bool locked, CancellationToken ct)
    {
        var report = (await Read<ReportRow>(db, "SELECT " + ReportJson + " FROM public.shift_reports WHERE id=@id AND (@deleted OR deleted_at IS NULL)" + (locked ? " FOR UPDATE" : ""), ct, P("id", id), P("deleted", deleted))).SingleOrDefault()
            ?? throw new CrmFault(404, "Raport nie istnieje.");
        ShiftReportValidation.ReadFields(report.Fields, report.SchemaVersion);
        return report;
    }
    public async Task<ShiftReportDto> Detail(LegacyUser actor, long id, CancellationToken ct)
    { RequireRead(actor); await using var db = await Open(ct); return Output(await Get(db, id, Admin(actor), false, ct)); }
    internal static DateOnly WarsawDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).DateTime);
    public async Task<ShiftReportToday> Today(LegacyUser actor, CancellationToken ct)
    {
        RequireRead(actor); var day = WarsawDate(clock.GetUtcNow()); await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var rows = await Read<ReportRow>(db, "SELECT " + ReportJson + " FROM public.shift_reports WHERE report_date=@day AND deleted_at IS NULL ORDER BY shift,id", ct, P("day", day));
        return new(day, Admin(actor), rows.Select(Output).ToArray(), await Totals(db, day, day, null, ct));
    }
    public async Task<ShiftReportTotals> Summary(LegacyUser actor, DateOnly? from, DateOnly? to, string? shift, CancellationToken ct)
    { RequireRead(actor); QueryValidation(from, to, shift); await using var db = await Open(ct); return await Totals(db, from, to, shift, ct); }
    private static async Task<ShiftReportTotals> Totals(NpgsqlConnection db, DateOnly? from, DateOnly? to, string? shift, CancellationToken ct)
    {
        var reports = await Read<ReportRow>(db, "SELECT " + ReportJson + " FROM public.shift_reports " + Filter + " AND status IN ('finalized','corrected')", ct, Filters(from, to, shift));
        long assembled = 0, prepared = 0, poured = 0, checkedCount = 0, demoulded = 0, damaged = 0;
        foreach (var report in reports)
        {
            var f = ShiftReportValidation.ReadFields(report.Fields, report.SchemaVersion);
            assembled += f.Assembled ?? 0; prepared += f.Prepared ?? 0; poured += f.Poured ?? 0;
            checkedCount += f.Checked ?? 0; demoulded += f.Demoulded ?? 0; damaged += f.Damaged ?? 0;
        }
        return new(reports.Length, assembled, prepared, poured, checkedCount, demoulded, damaged);
    }
    public async Task<ShiftReportFeatures> Features(LegacyUser actor, CancellationToken ct)
    {
        RequireRead(actor); await using var db = await Open(ct);
        var native = (await Read<string>(db, "SELECT to_json(value) FROM public.settings WHERE key='shift_reports_writer'", ct)).SingleOrDefault() == "dotnet";
        return new(true, native && actor.Role is ("produkcja" or "technolog"), native && Admin(actor), ShiftReportValidation.Schemas);
    }
    public async Task<ShiftReportAuditDto[]> Audit(LegacyUser actor, long id, bool drafts, CancellationToken ct)
    {
        RequireRead(actor); await using var db = await Open(ct); await Get(db, id, Admin(actor), false, ct);
        return await Read<ShiftReportAuditDto>(db, """
            SELECT json_build_object('id',id,'actorId',actor_id,'actorName',actor_name,'action',action,'reason',reason,
                'before',"before",'after',"after",'createdAt',created_at) FROM public.shift_report_audit
            WHERE report_id=@id AND (@drafts OR action<>'saved') ORDER BY id
            """, ct, P("id", id), P("drafts", drafts));
    }
    private static ShiftReportDto Output(ReportRow row)
    {
        var fields = ShiftReportValidation.ReadFields(row.Fields, row.SchemaVersion);
        return new(row.Id, row.ReportDate, row.Shift, row.AuthorId, row.AuthorName, row.Status, row.Version, row.SchemaVersion,
            fields, row.CreatedAt, row.UpdatedAt, row.FinalizedAt, row.FinalizedById, row.FinalizedByName, row.CorrectionCount,
            row.DeletedAt, ShiftReportValidation.Warning(fields), ShiftReportValidation.CompletionErrors(fields));
    }
    private static JsonElement Snapshot(ReportRow row) => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
    {
        ["status"] = row.Status, ["version_id"] = row.Version, ["schema_version"] = row.SchemaVersion,
        ["fields"] = row.Fields, ["deleted_at"] = row.DeletedAt
    });
    private static void CheckVersion(ReportRow row, LegacyUser actor, long version, bool draft = true, bool admin = false)
    {
        if (row.DeletedAt is not null) throw new CrmFault(409, "Raport został usunięty przez administratora.");
        if (!admin && actor.Role == "produkcja" && actor.Id != row.AuthorId) throw new CrmFault(403, "Możesz zmieniać tylko własny raport.");
        if (row.Version != version) throw new CrmFault(409, "Raport został zmieniony w innym oknie. Zachowaj dane i porównaj z aktualną wersją.");
        if (draft && row.Status != "draft") throw new CrmFault(409, "Raport jest zakończony. Użyj Koryguj raport.");
    }
    private static void RequireVersion(long version)
    { if (version < 1 || version >= int.MaxValue) throw CrmFault.Invalid("expectedVersion", "Wersja raportu jest wymagana."); }
    private static long LockKey(string value) => BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("shift-reports:" + value)));
    private async Task<T> Change<T>(LegacyUser actor, Guid request, string kind, long? target, object payload,
        Func<NpgsqlConnection, Task<T>> action, CancellationToken ct, bool admin = false) where T : class
    {
        RequireWrite(actor, admin);
        if (request == Guid.Empty) throw CrmFault.Invalid("requestId", "Identyfikator zapisu jest wymagany.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json))));
        await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            await Execute(db, "SET LOCAL lock_timeout='5s'", ct);
            if ((await Read<string>(db, "SELECT to_json(public.lock_shift_reports_writer())", ct)).SingleOrDefault() != "dotnet")
                throw new CrmFault(503, "Zapisy raportów zmianowych nie zostały przełączone.");
            await Execute(db, "SET LOCAL rcm.shift_reports_writer='dotnet'", ct);
            await Execute(db, "SELECT pg_advisory_xact_lock(@key)", ct, P("key", LockKey(request.ToString())));
            var receipt = (await Read<Receipt>(db, """
                SELECT json_build_object('actor',actor_id,'kind',command_type,'target',target_id,'hash',payload_hash,'response',response_json)
                FROM public.shift_report_command_receipts WHERE request_id=@request
                """, ct, P("request", request.ToString()))).SingleOrDefault();
            if (receipt is not null)
            {
                if (receipt.Actor != actor.Id || receipt.Kind != kind || receipt.Target != target || receipt.Hash != hash)
                    throw new CrmFault(409, "Identyfikator zapisu należy do innej operacji lub użytkownika.");
                return receipt.Response.Deserialize<T>(Json) ?? throw new CrmFault(503, "Nie można odczytać potwierdzenia zapisu.");
            }
            var result = await action(db);
            await Execute(db, """
                INSERT INTO public.shift_report_command_receipts(request_id,actor_id,command_type,target_id,payload_hash,response_json)
                VALUES(@request,@actor,@kind,@target,@hash,@response::json)
                """, ct, P("request", request.ToString()), P("actor", actor.Id), P("kind", kind), P("target", target),
                P("hash", hash), P("response", JsonSerializer.Serialize(result, Json)));
            await tx.CommitAsync(ct); return result;
        }
        catch (PostgresException error) when (error.SqlState is "55P03" or "55000" or "40P01")
        { throw new CrmFault(503, "Trwa zapis lub zmiana obsługi raportów. Ponów tę samą operację."); }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        { throw new CrmFault(409, "Raport dla tej daty i zmiany już istnieje lub dane zostały zmienione."); }
    }
    private sealed record Receipt(long Actor, string Kind, long? Target, string Hash, JsonElement Response);
    private sealed record ReportRow(long Id, DateOnly ReportDate, string Shift, long AuthorId, string AuthorName,
        string Status, long Version, int SchemaVersion, JsonElement Fields, DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt, DateTimeOffset? FinalizedAt, long? FinalizedById, string? FinalizedByName,
        int CorrectionCount, DateTimeOffset? DeletedAt);
}
