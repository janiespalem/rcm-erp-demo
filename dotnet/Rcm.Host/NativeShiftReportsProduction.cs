using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeShiftReports
{
    private const string ProductionLinkJson = """
        CASE WHEN l."ReportId" IS NULL THEN NULL ELSE json_build_object('reportId',l."ReportId",'version',l."Version",
            'contractId',l."ContractId",'contractName',c."Name",'sourceVersionAtLink',l."SourceVersionAtLink",
            'linkedBy',l."LinkedBy",'linkedAt',l."LinkedAt") END
        """;
    private const string ProductionReviewJson = """
        CASE WHEN v."Id" IS NULL THEN NULL ELSE json_build_object('id',v."Id",'reportId',v."ReportId",'reportVersion',v."ReportVersion",
            'linkVersion',v."LinkVersion",'contractId',v."ContractId",'reviewerId',v."ReviewerId",
            'decision',v."Decision",'reason',v."Reason",'recordedAt',v."RecordedAt") END
        """;
    private const string ProductionReviewStateSql = """
        CASE WHEN r.deleted_at IS NOT NULL THEN 'deleted' WHEN l."ReportId" IS NULL THEN 'unassigned'
            WHEN r.status='draft' THEN 'draft' WHEN v."Id" IS NULL THEN 'pending' ELSE v."Decision" END
        """;
    private const string ProductionReviewJoins = """
        FROM public.shift_reports r
        LEFT JOIN production."ReportLinks" l ON l."ReportId"=r.id
        LEFT JOIN production."Contracts" c ON c."Id"=l."ContractId"
        LEFT JOIN production."ReportReviews" v ON v."ReportId"=r.id AND v."ReportVersion"=r.version_id
            AND v."LinkVersion"=l."Version" AND v."ContractId"=l."ContractId"
        """;
    private static string ProductionStateJson => "json_build_object('report'," + ReportJson + ",'link'," + ProductionLinkJson
        + ",'currentReview'," + ProductionReviewJson + ",'state'," + ProductionReviewStateSql + ")";
    private sealed record ProductionStateRow(ReportRow Report, ProductionReportLinkDto? Link, ProductionReportReviewDto? CurrentReview, string State);
    private static ProductionReportReviewState ProductionOutput(ProductionStateRow row) => new(Output(row.Report), row.Link, row.CurrentReview, row.State);
    private DateTimeOffset ProductionNow
    {
        get { var value = clock.GetUtcNow(); return new(value.Ticks - value.Ticks % 10, TimeSpan.Zero); }
    }
    private static async Task<bool> AssignedProductionReviewer(NpgsqlConnection db, long actor, CancellationToken ct)
        => (await Read<bool>(db, "SELECT to_json(EXISTS(SELECT 1 FROM production.\"ReviewerAssignments\" WHERE \"UserId\"=@actor AND \"Active\"))", ct, P("actor", actor))).Single();
    private static async Task RequireProductionReviewer(NpgsqlConnection db, long actor, bool locked, CancellationToken ct)
    {
        if (locked) await Execute(db, "SELECT pg_advisory_xact_lock(hashtextextended('production-reviewer:' || @actor::text,0))", ct, P("actor", actor));
        if (!await AssignedProductionReviewer(db, actor, ct)) throw new CrmFault(403, "Akceptacja raportów wymaga jawnego przypisania osoby weryfikującej.");
    }
    private static async Task<ProductionReportReviewState> ProductionState(NpgsqlConnection db, long id, CancellationToken ct)
        => ProductionOutput((await Read<ProductionStateRow>(db, "SELECT " + ProductionStateJson + " " + ProductionReviewJoins + " WHERE r.id=@id", ct, P("id", id))).SingleOrDefault()
            ?? throw new CrmFault(404, "Raport nie istnieje."));
    private async Task<NpgsqlConnection> ProductionReadConnection(LegacyUser actor, CancellationToken ct)
    {
        RequireRead(actor); await CheckProductionReviewRuntime(ct); var db = await Open(ct);
        try { await Execute(db, "SET TIME ZONE 'UTC'", ct); return db; }
        catch { await db.DisposeAsync(); throw; }
    }
    public async Task<ProductionReviewFeatures> ProductionReviewFeatures(LegacyUser actor, CancellationToken ct)
    {
        await using var db = await ProductionReadConnection(actor, ct);
        var writable = (await Read<string>(db, "SELECT to_json(value) FROM public.settings WHERE key='shift_reports_writer'", ct)).SingleOrDefault() == "dotnet";
        return new(true, writable && actor.Role is ("produkcja" or "technolog"), writable && await AssignedProductionReviewer(db, actor.Id, ct));
    }
    public async Task<ProductionReportReviewState> ProductionReport(LegacyUser actor, long id, CancellationToken ct)
    {
        await using var db = await ProductionReadConnection(actor, ct);
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct); await Get(db, id, Admin(actor), false, ct);
        return await ProductionState(db, id, ct);
    }
    public Task<Page<ProductionReportReviewState>> ProductionReviewQueue(LegacyUser actor, Guid? contractId, string state, int page, int pageSize, CancellationToken ct)
        => ProductionReportsPage(actor, contractId, state, page, pageSize, true, ct);
    public Task<Page<ProductionReportReviewState>> ProductionContractReports(LegacyUser actor, Guid contractId, string state, int page, int pageSize, CancellationToken ct)
        => ProductionReportsPage(actor, contractId, state, page, pageSize, false, ct);
    private async Task<Page<ProductionReportReviewState>> ProductionReportsPage(LegacyUser actor, Guid? contractId, string state, int page, int pageSize, bool queue, CancellationToken ct)
    {
        QueryValidation(null, null, null, page, pageSize);
        if (state is not ("pending" or "accepted" or "returned" or "all") && (queue || state != "draft")) throw CrmFault.Invalid("state", "Wybierz właściwy stan raportu.");
        await using var db = await ProductionReadConnection(actor, ct);
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Execute(db, "SET LOCAL TIME ZONE 'UTC'", ct);
        if (queue) await RequireProductionReviewer(db, actor.Id, false, ct);
        else await ProductionContractExists(db, contractId!.Value, ct);
        var where = " WHERE r.deleted_at IS NULL" + (queue ? " AND r.status IN ('finalized','corrected')" : "")
            + " AND l.\"ReportId\" IS NOT NULL AND (@contract::uuid IS NULL OR l.\"ContractId\"=@contract) AND (@state='all' OR (" + ProductionReviewStateSql + ")=@state)";
        var total = (await Read<int>(db, "SELECT to_json(count(*)::int) " + ProductionReviewJoins + where, ct, P("contract", contractId), P("state", state))).Single();
        var rows = await Read<ProductionStateRow>(db, "SELECT " + ProductionStateJson + " " + ProductionReviewJoins + where + " ORDER BY r.report_date DESC,r.shift,r.id LIMIT @limit OFFSET @offset", ct,
            P("contract", contractId), P("state", state), P("limit", pageSize), P("offset", (page - 1) * pageSize));
        return new(rows.Select(ProductionOutput).ToArray(), total, page, pageSize);
    }
    public async Task<ProductionAcceptedOperations> ProductionAcceptedTotals(LegacyUser actor, Guid contractId, CancellationToken ct)
    {
        await using var db = await ProductionReadConnection(actor, ct);
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await ProductionContractExists(db, contractId, ct);
        return (await Read<ProductionAcceptedOperations>(db, """
            SELECT json_build_object('contractId',@contract,'reports',count(*),'assembled',coalesce(sum((r.fields->>'assembled')::bigint),0),
                'prepared',coalesce(sum((r.fields->>'prepared')::bigint),0),'poured',coalesce(sum((r.fields->>'poured')::bigint),0),
                'checked',coalesce(sum((r.fields->>'checked')::bigint),0),'demoulded',coalesce(sum((r.fields->>'demoulded')::bigint),0),
                'damaged',coalesce(sum((r.fields->>'damaged')::bigint),0))
            """ + ProductionReviewJoins + " WHERE l.\"ContractId\"=@contract AND r.deleted_at IS NULL AND r.status IN ('finalized','corrected') AND v.\"Decision\"='accepted'", ct, P("contract", contractId))).Single();
    }
    public async Task<Page<ProductionReportAuditDto>> ProductionReportAudit(LegacyUser actor, long id, int page, int pageSize, CancellationToken ct)
    {
        QueryValidation(null, null, null, page, pageSize); await using var db = await ProductionReadConnection(actor, ct);
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct); await Get(db, id, Admin(actor), false, ct);
        var total = (await Read<int>(db, "SELECT to_json(count(*)::int) FROM production.\"ReportReviewAudit\" WHERE \"ReportId\"=@id", ct, P("id", id))).Single();
        var rows = await Read<ProductionReportAuditDto>(db, """
            SELECT json_build_object('id',"Id",'reportId',"ReportId",'actorId',"ActorId",'action',"Action",'reason',"Reason",'changes',"Changes",'recordedAt',"RecordedAt")
            FROM production."ReportReviewAudit" WHERE "ReportId"=@id ORDER BY "RecordedAt" DESC,"Id" LIMIT @limit OFFSET @offset
            """, ct, P("id", id), P("limit", pageSize), P("offset", (page - 1) * pageSize));
        return new(rows, total, page, pageSize);
    }
    private static async Task ProductionContractExists(NpgsqlConnection db, Guid id, CancellationToken ct)
    {
        if (!(await Read<bool>(db, "SELECT to_json(EXISTS(SELECT 1 FROM production.\"Contracts\" WHERE \"Id\"=@id))", ct, P("id", id))).Single())
            throw new CrmFault(404, "Kontrakt nie istnieje.");
    }
    private static void ProductionLinkVersion(ProductionReportLinkDto? link, long expected)
    {
        if (expected < 0) throw CrmFault.Invalid("expectedLinkVersion", "Wczytaj wersję powiązania.");
        if ((link?.Version ?? 0) != expected) throw new CrmFault(409, "Powiązanie raportu z kontraktem zostało zmienione. Wczytaj aktualne dane.");
    }
    public Task<ProductionReportReviewState> LinkProductionReport(LegacyUser actor, long id, LinkProductionReport command, CancellationToken ct)
    {
        RequireVersion(command.ExpectedReportVersion);
        if (command.ContractId == Guid.Empty) throw CrmFault.Invalid("contractId", "Wybierz kontrakt.");
        return ProductionReviewCommand(actor, command.RequestId, $"report:{id}:link", command, false, async db =>
        {
            var report = await Get(db, id, false, true, ct); CheckVersion(report, actor, command.ExpectedReportVersion, false);
            var before = await ProductionState(db, id, ct); ProductionLinkVersion(before.Link, command.ExpectedLinkVersion);
            await ProductionContractExists(db, command.ContractId, ct);
            var changed = before.Link is not null && before.Link.ContractId != command.ContractId;
            var reason = ProductionReviewReason(command.Reason, changed);
            if (before.Link?.ContractId == command.ContractId) return before;
            var now = ProductionNow;
            await Execute(db, """
                INSERT INTO production."ReportLinks"("ReportId","Version","ContractId","SourceVersionAtLink","LinkedBy","LinkedAt")
                VALUES(@id,1,@contract,@version,@actor,@now)
                ON CONFLICT("ReportId") DO UPDATE SET "Version"=production."ReportLinks"."Version"+1,
                    "ContractId"=excluded."ContractId","SourceVersionAtLink"=excluded."SourceVersionAtLink","LinkedBy"=excluded."LinkedBy","LinkedAt"=excluded."LinkedAt"
                """, ct, P("id", id), P("contract", command.ContractId), P("version", report.Version), P("actor", actor.Id), P("now", now));
            var result = await ProductionState(db, id, ct);
            await ProductionAudit(db, id, actor.Id, changed ? "report_relinked" : "report_linked", reason, new { before = before.Link, after = result.Link }, now, ct);
            return result;
        }, ct);
    }
    private static string ProductionReviewReason(string? reason, bool required)
    {
        if (reason?.Length > 2000) throw CrmFault.Invalid("reason", "Maksymalnie 2000 znaków.");
        if (required && string.IsNullOrWhiteSpace(reason)) throw CrmFault.Invalid("reason", "Podaj powód.");
        return reason?.Trim() ?? "";
    }
    public Task<ProductionReportReviewState> ReviewProductionReport(LegacyUser actor, long id, ReviewProductionReport command, CancellationToken ct)
    {
        RequireVersion(command.ExpectedReportVersion);
        if (command.Decision is not ("accepted" or "returned")) throw CrmFault.Invalid("decision", "Wybierz akceptację lub zwrot do poprawy.");
        var reason = ProductionReviewReason(command.Reason, command.Decision == "returned");
        return ProductionReviewCommand(actor, command.RequestId, $"report:{id}:review", command, true, async db =>
        {
            var report = await Get(db, id, false, true, ct);
            if (report.Version != command.ExpectedReportVersion) throw new CrmFault(409, "Raport został zmieniony. Porównaj aktualną wersję przed akceptacją.");
            if (report.Status is not ("finalized" or "corrected")) throw new CrmFault(409, "Najpierw zakończ raport.");
            var state = await ProductionState(db, id, ct); ProductionLinkVersion(state.Link, command.ExpectedLinkVersion);
            if (state.Link is null) throw new CrmFault(409, "Raport wymaga jawnego powiązania z kontraktem.");
            if (state.CurrentReview is not null) throw new CrmFault(409, "Ta wersja raportu ma już decyzję. Zwrócony raport wymaga korekty i ponownej weryfikacji.");
            if (command.Decision == "accepted") ShiftReportValidation.RequireComplete(ShiftReportValidation.ReadFields(report.Fields, report.SchemaVersion));
            var reviewId = Guid.NewGuid(); var now = ProductionNow;
            await Execute(db, """
                INSERT INTO production."ReportReviews"("Id","ReportId","ReportVersion","LinkVersion","ContractId","ReviewerId","Decision","Reason","Snapshot","RecordedAt")
                VALUES(@review,@id,@version,@linkVersion,@contract,@actor,@decision,@reason,@snapshot::jsonb,@now)
                """, ct, P("review", reviewId), P("id", id), P("version", report.Version), P("linkVersion", state.Link.Version), P("contract", state.Link.ContractId),
                P("actor", actor.Id), P("decision", command.Decision), P("reason", reason), P("snapshot", JsonSerializer.Serialize(Output(report), Json)), P("now", now));
            await ProductionAudit(db, id, actor.Id, "report_" + command.Decision, reason,
                new { reviewId, reportVersion = report.Version, linkVersion = state.Link.Version, contractId = state.Link.ContractId, decision = command.Decision }, now, ct);
            return await ProductionState(db, id, ct);
        }, ct);
    }
    private static Task ProductionAudit(NpgsqlConnection db, long reportId, long actor, string action, string reason, object changes, DateTimeOffset now, CancellationToken ct)
        => Execute(db, """
            INSERT INTO production."ReportReviewAudit"("Id","ReportId","ActorId","Action","Reason","Changes","RecordedAt")
            VALUES(@id,@report,@actor,@action,@reason,@changes::jsonb,@now)
            """, ct, P("id", Guid.NewGuid()), P("report", reportId), P("actor", actor), P("action", action), P("reason", reason), P("changes", JsonSerializer.Serialize(changes, Json)), P("now", now));
    private async Task<ProductionReportReviewState> ProductionReviewCommand(LegacyUser actor, Guid requestId, string operation, object payload, bool reviewer,
        Func<NpgsqlConnection, Task<ProductionReportReviewState>> action, CancellationToken ct)
    {
        if (reviewer) RequireRead(actor); else RequireWrite(actor);
        if (requestId == Guid.Empty) throw CrmFault.Invalid("requestId", "Identyfikator zapisu jest wymagany.");
        await CheckProductionReviewRuntime(ct);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + "\n" + JsonSerializer.Serialize(payload, Json))));
        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"production-reviews:{actor.Id}:{requestId}")));
        await using var db = await Open(ct); await using var transaction = await db.BeginTransactionAsync(ct);
        try
        {
            await Execute(db, "SET LOCAL lock_timeout='3s'", ct); await Execute(db, "SET LOCAL statement_timeout='15s'", ct);
            await Execute(db, "SET LOCAL idle_in_transaction_session_timeout='20s'", ct); await Execute(db, "SET LOCAL TIME ZONE 'UTC'", ct);
            if ((await Read<string>(db, "SELECT to_json(public.lock_shift_reports_writer())", ct)).SingleOrDefault() != "dotnet")
                throw new CrmFault(503, "Weryfikacja wymaga aktywnej obsługi raportów na nowym serwerze.");
            await Execute(db, "SELECT pg_advisory_xact_lock(@key)", ct, P("key", key));
            if (reviewer) await RequireProductionReviewer(db, actor.Id, true, ct);
            var receipt = (await Read<ProductionReceiptRow>(db, """
                SELECT json_build_object('hash',"Fingerprint",'result',"Result") FROM production."ReviewReceipts" WHERE "ActorId"=@actor AND "RequestId"=@request
                """, ct, P("actor", actor.Id), P("request", requestId))).SingleOrDefault();
            if (receipt is not null)
            {
                if (receipt.Hash != hash) throw new CrmFault(409, "Identyfikator zapisu został już użyty dla innej operacji lub danych.");
                return receipt.Result.Deserialize<ProductionReportReviewState>(Json) ?? throw new CrmFault(503, "Nie można odczytać potwierdzenia weryfikacji.");
            }
            var result = await action(db);
            await Execute(db, """
                INSERT INTO production."ReviewReceipts"("ActorId","RequestId","Fingerprint","Result","RecordedAt") VALUES(@actor,@request,@hash,@result::jsonb,@now)
                """, ct, P("actor", actor.Id), P("request", requestId), P("hash", hash), P("result", JsonSerializer.Serialize(result, Json)), P("now", ProductionNow));
            await transaction.CommitAsync(ct); return result;
        }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.DeadlockDetected)
        { throw new CrmFault(503, "Trwa zapis lub zmiana uprawnień. Zachowaj dane i ponów tę samą operację."); }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        { throw new CrmFault(409, "Raport lub jego powiązanie zostały zmienione. Wczytaj aktualne dane."); }
    }
    private sealed record ProductionReceiptRow(string Hash, JsonElement Result);

    public async Task CheckProductionReviewRuntime(CancellationToken ct)
    {
        if (configuration["ShiftReports:Mode"] != "native" || string.IsNullOrWhiteSpace(configuration.GetConnectionString("Production")))
            throw new CrmFault(503, "Weryfikacja produkcji nie została włączona.");
        var source = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("ShiftReports") ?? "");
        var production = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Production")!);
        if (source.Host != production.Host || source.Port != production.Port || source.Database != production.Database)
            throw new CrmFault(503, "Raporty i kontrakty muszą korzystać z tej samej bazy PostgreSQL.");
        await CheckRuntime(ct); await using var db = await Open(ct);
        try
        {
            var allowed = (await Read<bool>(db, """
                SELECT to_json(current_user=session_user AND has_schema_privilege(current_user,'production','USAGE')
                    AND NOT has_schema_privilege(current_user,'production','CREATE')
                    AND NOT has_database_privilege(current_user,current_database(),'CREATE')
                    AND NOT EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND has_schema_privilege(current_user,n.oid,'CREATE'))
                    AND NOT EXISTS(SELECT 1 FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
                        WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND t.relkind IN ('r','p','v','m','f')
                        AND pg_has_role(current_user,t.relowner,'MEMBER'))
                    AND has_table_privilege(current_user,'production."Contracts"','SELECT')
                    AND NOT has_table_privilege(current_user,'production."Contracts"','INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    AND NOT has_any_column_privilege(current_user,'production."Contracts"','INSERT,UPDATE,REFERENCES')
                    AND has_table_privilege(current_user,'production."ReportLinks"','SELECT')
                    AND has_table_privilege(current_user,'production."ReportLinks"','INSERT')
                    AND has_table_privilege(current_user,'production."ReportLinks"','UPDATE')
                    AND has_table_privilege(current_user,'production."ReviewerAssignments"','SELECT')
                    AND NOT has_table_privilege(current_user,'production."ReviewerAssignments"','INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    AND NOT has_any_column_privilege(current_user,'production."ReviewerAssignments"','INSERT,UPDATE,REFERENCES')
                    AND NOT EXISTS(SELECT 1 FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname='production' AND t.relkind IN ('r','p','v','m','f')
                        AND (pg_has_role(current_user,t.relowner,'MEMBER') OR has_table_privilege(current_user,t.oid,'DELETE,TRUNCATE,REFERENCES,TRIGGER')
                            OR (t.relname NOT IN ('Contracts','ReportLinks','ReviewerAssignments','ReportReviews','ReportReviewAudit','ReviewReceipts')
                                AND (has_table_privilege(current_user,t.oid,'SELECT,INSERT,UPDATE') OR has_any_column_privilege(current_user,t.oid,'SELECT,INSERT,UPDATE')))
                            OR (t.relname IN ('ReportReviews','ReportReviewAudit','ReviewReceipts','ReviewerAssignmentAudit')
                                AND (has_table_privilege(current_user,t.oid,'UPDATE') OR has_any_column_privilege(current_user,t.oid,'UPDATE')))))
                    AND has_table_privilege(current_user,'production."ReportReviews"','SELECT')
                    AND has_table_privilege(current_user,'production."ReportReviews"','INSERT')
                    AND has_table_privilege(current_user,'production."ReportReviewAudit"','SELECT')
                    AND has_table_privilege(current_user,'production."ReportReviewAudit"','INSERT')
                    AND has_table_privilege(current_user,'production."ReviewReceipts"','SELECT')
                    AND has_table_privilege(current_user,'production."ReviewReceipts"','INSERT')
                    AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgname='serialize_reviewer_assignment' AND tgenabled='O'
                        AND tgrelid='production."ReviewerAssignments"'::regclass)
                    AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgname='record_reviewer_assignment' AND tgenabled='O'
                        AND tgrelid='production."ReviewerAssignments"'::regclass)
                    AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgname='protect_report_link' AND tgenabled='O' AND tgrelid='production."ReportLinks"'::regclass)
                    AND (SELECT count(*) FROM pg_trigger WHERE tgname='preserve_review_history' AND tgenabled='O'
                        AND tgrelid IN ('production."ReportReviews"'::regclass,'production."ReportReviewAudit"'::regclass,
                            'production."ReviewReceipts"'::regclass,'production."ReviewerAssignmentAudit"'::regclass))=4)
                """, ct)).Single();
            if (!allowed) throw new CrmFault(503, "Skonfiguruj ograniczone uprawnienia weryfikacji produkcji.");
        }
        catch (PostgresException) { throw new CrmFault(503, "Zastosuj migracje i uprawnienia weryfikacji produkcji."); }
    }
}
