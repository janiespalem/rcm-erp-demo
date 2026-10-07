using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed class NativeCatalog(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string MaterialJson = "json_build_object('id',id,'name',name,'category',category,'defaultRatePlnKg',default_rate_pln_kg,'isActive',coalesce(is_active,false),'notes',notes,'version',version_id)";
    private const string OperationJson = "json_build_object('id',id,'name',name,'department',department,'defaultRate',default_rate,'formula',formula,'version',version_id)";
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration.GetConnectionString("Catalog") ?? throw new CrmFault(503, "Katalog nie został skonfigurowany."));
        try { await db.OpenAsync(ct); return db; } catch { await db.DisposeAsync(); throw; }
    }
    private static NpgsqlParameter P(string name, object? value) => value is null ? new(name, NpgsqlDbType.Unknown) { Value = DBNull.Value } : new(name, value);
    private static async Task<T[]> Read<T>(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return rows.ToArray();
    }
    private static async Task Execute(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    { await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync(ct); }
    private static void QueryValidation(string? q, int page, int pageSize)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100 || q?.Length > 200)
            throw CrmFault.Invalid("page", "Wybierz stronę i rozmiar od 1 do 100; wyszukiwanie do 200 znaków.");
    }
    public async Task<Page<CatalogMaterialDto>> Materials(string? q, bool archived, int page, int pageSize, CancellationToken ct)
    {
        QueryValidation(q, page, pageSize); await using var db = await Open(ct);
        const string where = "WHERE (@archived OR is_active=true) AND (@q='' OR strpos(lower(name || ' ' || coalesce(category,'')),lower(@q))>0)";
        NpgsqlParameter[] Args() => [P("archived", archived), P("q", q?.Trim() ?? "")];
        var total = (await Read<int>(db, "SELECT to_json(count(*)::int) FROM public.approved_materials " + where, ct, Args()))[0];
        var rows = await Read<CatalogMaterialDto>(db, "SELECT " + MaterialJson + " FROM public.approved_materials " + where + " ORDER BY name,id LIMIT @limit OFFSET @offset", ct, [.. Args(), P("limit", pageSize), P("offset", (page - 1) * pageSize)]);
        return new(rows, total, page, pageSize);
    }
    public async Task<Page<CatalogOperationDto>> Operations(string? q, int page, int pageSize, CancellationToken ct)
    {
        QueryValidation(q, page, pageSize); await using var db = await Open(ct);
        const string where = "WHERE @q='' OR strpos(lower(name || ' ' || coalesce(department,'') || ' ' || coalesce(formula,'')),lower(@q))>0";
        var total = (await Read<int>(db, "SELECT to_json(count(*)::int) FROM public.operation_catalog " + where, ct, P("q", q?.Trim() ?? "")))[0];
        var rows = await Read<CatalogOperationDto>(db, "SELECT " + OperationJson + " FROM public.operation_catalog " + where + " ORDER BY name,id LIMIT @limit OFFSET @offset", ct, P("q", q?.Trim() ?? ""), P("limit", pageSize), P("offset", (page - 1) * pageSize));
        return new(rows, total, page, pageSize);
    }
    public async Task<CatalogMaterialDto> Material(long id, CancellationToken ct)
    { await using var db = await Open(ct); return (await Read<CatalogMaterialDto>(db, "SELECT " + MaterialJson + " FROM public.approved_materials WHERE id=@id", ct, P("id", id))).SingleOrDefault() ?? throw new CrmFault(404, "Materiał nie znaleziony."); }
    public async Task<CatalogOperationDto> Operation(long id, CancellationToken ct)
    { await using var db = await Open(ct); return (await Read<CatalogOperationDto>(db, "SELECT " + OperationJson + " FROM public.operation_catalog WHERE id=@id", ct, P("id", id))).SingleOrDefault() ?? throw new CrmFault(404, "Operacja nie znaleziona."); }
    public async Task<CatalogOperationDto[]> Suggest(string? text, string? material, CancellationToken ct)
    {
        if (text?.Length > 10000 || material?.Length > 1000) throw CrmFault.Invalid("text", "Opis jest zbyt długi.");
        await using var db = await Open(ct);
        var all = await Read<CatalogOperationDto>(db, "SELECT " + OperationJson + " FROM public.operation_catalog ORDER BY name,id", ct);
        return CatalogKeywords.Suggest(all, text, material);
    }
    public async Task<CatalogFeatures> Features(CancellationToken ct)
    {
        await using var db = await Open(ct);
        var writer = (await Read<string>(db, "SELECT to_json(value) FROM public.settings WHERE key='catalog_writer'", ct)).SingleOrDefault();
        var rate = (await Read<string>(db, "SELECT to_json(value) FROM public.settings WHERE key='labor_rate_pln'", ct)).SingleOrDefault();
        var labor = double.TryParse(rate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) && parsed >= 0 ? parsed : 100;
        return new(true, true, writer == "dotnet", labor);
    }
    private static void Validate(string name, string? grouping, double? rate, double maximum, string? notes = null, string? formula = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw CrmFault.Invalid("name", "Nazwa jest wymagana (do 100 znaków).");
        if (grouping?.Length > 50) throw CrmFault.Invalid("category", "Maksymalnie 50 znaków.");
        if (rate is { } value && (!double.IsFinite(value) || value < 0 || value > maximum)) throw CrmFault.Invalid("rate", "Nieprawidłowa stawka.");
        if (notes?.Length > 10000) throw CrmFault.Invalid("notes", "Maksymalnie 10000 znaków.");
        if (formula?.Length > 255) throw CrmFault.Invalid("formula", "Maksymalnie 255 znaków.");
    }
    public Task<CatalogMaterialDto> CreateMaterial(LegacyUser actor, CreateCatalogMaterial c, CancellationToken ct)
    {
        Validate(c.Name, c.Category, c.DefaultRatePlnKg, 9999.99, c.Notes);
        return Change(actor, c.RequestId, "material_create", null, c, async db => (await Read<CatalogMaterialDto>(db,
            "INSERT INTO public.approved_materials(name,category,default_rate_pln_kg,is_active,notes) VALUES(@name,@category,@rate,@active,@notes) RETURNING " + MaterialJson, ct,
            P("name", c.Name), P("category", c.Category), P("rate", c.DefaultRatePlnKg), P("active", c.IsActive), P("notes", c.Notes)))[0], ct);
    }
    public Task<CatalogMaterialDto> UpdateMaterial(LegacyUser actor, long id, UpdateCatalogMaterial c, CancellationToken ct)
    {
        Validate(c.Name, c.Category, c.DefaultRatePlnKg, 9999.99, c.Notes); RequireVersion(c.ExpectedVersion);
        return Change(actor, c.RequestId, "material_update", id, c, async db =>
        {
            await Version(db, "approved_materials", id, c.ExpectedVersion, ct);
            return (await Read<CatalogMaterialDto>(db, "UPDATE public.approved_materials SET name=@name,category=@category,default_rate_pln_kg=@rate,is_active=@active,notes=@notes WHERE id=@id RETURNING " + MaterialJson, ct,
                P("id", id), P("name", c.Name), P("category", c.Category), P("rate", c.DefaultRatePlnKg), P("active", c.IsActive), P("notes", c.Notes)))[0];
        }, ct);
    }
    public Task<CatalogOperationDto> CreateOperation(LegacyUser actor, CreateCatalogOperation c, CancellationToken ct)
    {
        var formula = CatalogKeywords.Auto(c.Name, c.Department, c.Formula); Validate(c.Name, c.Department, c.DefaultRate, 1_000_000, formula: formula);
        return Change(actor, c.RequestId, "operation_create", null, c, async db => (await Read<CatalogOperationDto>(db,
            "INSERT INTO public.operation_catalog(name,department,default_rate,formula) VALUES(@name,@department,@rate,@formula) RETURNING " + OperationJson, ct,
            P("name", c.Name), P("department", c.Department), P("rate", c.DefaultRate), P("formula", formula)))[0], ct);
    }
    public Task<CatalogOperationDto> UpdateOperation(LegacyUser actor, long id, UpdateCatalogOperation c, CancellationToken ct)
    {
        var formula = CatalogKeywords.Auto(c.Name, c.Department, c.Formula); Validate(c.Name, c.Department, c.DefaultRate, 1_000_000, formula: formula); RequireVersion(c.ExpectedVersion);
        return Change(actor, c.RequestId, "operation_update", id, c, async db =>
        {
            await Version(db, "operation_catalog", id, c.ExpectedVersion, ct);
            return (await Read<CatalogOperationDto>(db, "UPDATE public.operation_catalog SET name=@name,department=@department,default_rate=@rate,formula=@formula WHERE id=@id RETURNING " + OperationJson, ct,
                P("id", id), P("name", c.Name), P("department", c.Department), P("rate", c.DefaultRate), P("formula", formula)))[0];
        }, ct);
    }
    public Task<CatalogMutationResult> Remove(LegacyUser actor, long id, CatalogVersionCommand c, bool material, CancellationToken ct)
    {
        RequireVersion(c.ExpectedVersion);
        var table = material ? "approved_materials" : "operation_catalog";
        return Change(actor, c.RequestId, material ? "material_archive" : "operation_delete", id, c, async db =>
        {
            await Version(db, table, id, c.ExpectedVersion, ct);
            await Execute(db, material ? "UPDATE public.approved_materials SET is_active=false WHERE id=@id" : "DELETE FROM public.operation_catalog WHERE id=@id", ct, P("id", id));
            return new CatalogMutationResult(id, c.ExpectedVersion + 1, !material);
        }, ct);
    }
    private static void RequireVersion(long version)
    { if (version < 1) throw CrmFault.Invalid("expectedVersion", "Wczytaj aktualną wersję wpisu."); }
    private static async Task Version(NpgsqlConnection db, string table, long id, long expected, CancellationToken ct)
    {
        var version = (await Read<long>(db, "SELECT to_json(version_id) FROM public." + table + " WHERE id=@id FOR UPDATE", ct, P("id", id))).Select(v => (long?)v).SingleOrDefault();
        if (version is null) throw new CrmFault(404, "Wpis katalogu nie znaleziony.");
        if (version != expected) throw new CrmFault(409, "Wpis zmienił się. Porównaj aktualną wersję przed ponownym zapisem.");
    }
    private async Task<T> Change<T>(LegacyUser actor, Guid request, string kind, long? target, object payload, Func<NpgsqlConnection, Task<T>> action, CancellationToken ct) where T : class
    {
        if (actor.Id <= 0 || actor.Role != "technolog") throw new CrmFault(403, "Edycja katalogu wymaga uprawnień technologa.");
        if (request == Guid.Empty) throw CrmFault.Invalid("requestId", "Identyfikator zapisu jest wymagany.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json))));
        await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            await Execute(db, "SET LOCAL lock_timeout='5s'", ct);
            if ((await Read<string>(db, "SELECT to_json(public.lock_catalog_writer())", ct)).SingleOrDefault() != "dotnet") throw new CrmFault(503, "Zapisy katalogu nie zostały przełączone.");
            await Execute(db, "SET LOCAL rcm.catalog_writer='dotnet'", ct);
            var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("catalog:" + request)));
            await Execute(db, "SELECT pg_advisory_xact_lock(@key)", ct, P("key", key));
            var receipt = (await Read<Receipt>(db, "SELECT json_build_object('actor',actor_id,'kind',command_type,'target',target_id,'hash',payload_hash,'response',response_json) FROM public.catalog_command_receipts WHERE request_id=@request", ct, P("request", request.ToString()))).SingleOrDefault();
            if (receipt is not null)
            {
                if (receipt.Actor != actor.Id || receipt.Kind != kind || receipt.Target != target || receipt.Hash != hash) throw new CrmFault(409, "Identyfikator zapisu należy do innej operacji lub użytkownika.");
                return receipt.Response.Deserialize<T>(Json) ?? throw new CrmFault(503, "Nie można odczytać potwierdzenia.");
            }
            var result = await action(db);
            await Execute(db, "INSERT INTO public.catalog_command_receipts(request_id,actor_id,command_type,target_id,payload_hash,response_json) VALUES(@request,@actor,@kind,@target,@hash,@response::json)", ct,
                P("request", request.ToString()), P("actor", actor.Id), P("kind", kind), P("target", target), P("hash", hash), P("response", JsonSerializer.Serialize(result, Json)));
            await tx.CommitAsync(ct); return result;
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        { throw new CrmFault(409, e.SqlState == PostgresErrorCodes.UniqueViolation ? "Wpis o tej nazwie już istnieje." : "Operacja jest używana w zleceniu i nie może zostać usunięta."); }
    }
    public async Task CheckRuntime(CancellationToken ct)
    {
        await using var db = await Open(ct);
        var ok = (await Read<bool>(db, """
            SELECT to_json(NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole
                AND has_table_privilege(current_user,'public.approved_materials','SELECT')
                AND has_table_privilege(current_user,'public.approved_materials','INSERT')
                AND has_table_privilege(current_user,'public.approved_materials','UPDATE')
                AND has_table_privilege(current_user,'public.operation_catalog','SELECT')
                AND has_table_privilege(current_user,'public.operation_catalog','INSERT')
                AND has_table_privilege(current_user,'public.operation_catalog','UPDATE')
                AND has_table_privilege(current_user,'public.operation_catalog','DELETE')
                AND has_table_privilege(current_user,'public.catalog_command_receipts','SELECT')
                AND has_table_privilege(current_user,'public.catalog_command_receipts','INSERT')
                AND has_table_privilege(current_user,'public.settings','SELECT')
                AND has_sequence_privilege(current_user,pg_get_serial_sequence('public.approved_materials','id'),'USAGE')
                AND has_sequence_privilege(current_user,pg_get_serial_sequence('public.operation_catalog','id'),'USAGE')
                AND has_function_privilege(current_user,'public.lock_catalog_writer()','EXECUTE')
                AND NOT has_table_privilege(current_user,'public.settings','UPDATE')
                AND NOT has_table_privilege(current_user,'public.users','SELECT')
                AND NOT has_table_privilege(current_user,'public.orders','UPDATE')
                AND NOT has_table_privilege(current_user,'public.product_templates','DELETE')
                AND NOT has_table_privilege(current_user,'public.approved_materials','DELETE')
                AND NOT has_table_privilege(current_user,'public.catalog_command_receipts','UPDATE,DELETE')
                AND (SELECT count(*) FROM pg_trigger WHERE tgrelid IN ('public.approved_materials'::regclass,'public.operation_catalog'::regclass)
                    AND tgname IN ('require_catalog_writer','increment_catalog_version') AND tgenabled='O')=4)
            FROM pg_roles r WHERE r.rolname=current_user
            """, ct)).SingleOrDefault();
        if (!ok) throw new CrmFault(503, "Skonfiguruj ograniczone uprawnienia katalogu.");
    }
    private sealed record Receipt(long Actor, string Kind, long? Target, string Hash, JsonElement Response);
}
