using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeTemplates(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string TemplateJson = "json_build_object('id',id,'name',name,'category',coalesce(category,''),'operations',coalesce(operations_json,'[]'::json),'materials',coalesce(materials_json,'[]'::json),'instructions',coalesce(instruction_blocks,'[]'::json),'machines',coalesce(machines_json,'[]'::json),'basePricePln',base_price_pln,'marginPct',coalesce(margin_pct,0),'isActive',coalesce(is_active,false),'projectCode',project_code,'positionNumber',position_nr,'notes',notes,'hasDrawing',coalesce(drawing_path,'')<>'','version',version_id)";
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration.GetConnectionString("Catalog") ?? throw new CrmFault(503, "Katalog nie został skonfigurowany."));
        try { await db.OpenAsync(ct); return db; } catch { await db.DisposeAsync(); throw; }
    }
    private static NpgsqlParameter P(string name, object? value) => value is null ? new(name, NpgsqlDbType.Unknown) { Value = DBNull.Value } : new(name, value);
    private static async Task<T[]> Read<T>(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] args)
    {
        await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(args);
        await using var reader = await command.ExecuteReaderAsync(ct); List<T> rows = [];
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return rows.ToArray();
    }
    private static async Task Execute(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] args)
    { await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(args); await command.ExecuteNonQueryAsync(ct); }
    internal static void QueryValidation(string? q, string? category, string? project, int page, int size)
    {
        if (q?.Length > 200 || category?.Length > 50 || project?.Length > 50 || page is < 1 or > 1_000_000 || size is < 1 or > 100)
            throw CrmFault.Invalid("page", "Nieprawidłowy filtr lub strona katalogu (od 1 do 100 pozycji). ");
    }
    public async Task<Page<ProductTemplateDto>> List(string? q, string? category, string? projectCode, bool archived, int page, int pageSize, CancellationToken ct)
    {
        QueryValidation(q, category, projectCode, page, pageSize);
        await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        const string filter = " WHERE (@archived OR is_active=true) AND (@category='' OR category=@category) AND (@project='' OR project_code=@project) AND (@q='' OR strpos(lower(name || ' ' || coalesce(project_code,'') || ' ' || coalesce(position_nr,'')),lower(@q))>0)";
        NpgsqlParameter[] Args() => [P("archived", archived), P("category", category ?? ""), P("project", projectCode ?? ""), P("q", q?.Trim() ?? "")];
        var total = (await Read<long>(db, "SELECT to_json(count(*)) FROM public.product_templates" + filter, ct, Args()))[0];
        var rows = await Read<ProductTemplateDto>(db, "SELECT " + TemplateJson + " FROM public.product_templates" + filter + " ORDER BY project_code NULLS LAST,position_nr NULLS LAST,id LIMIT @limit OFFSET @offset", ct,
            [..Args(), P("limit", pageSize), P("offset", (page - 1) * pageSize)]);
        await tx.CommitAsync(ct); return new(rows, checked((int)total), page, pageSize);
    }
    public async Task<ProductTemplateDto> Detail(long id, CancellationToken ct)
    { await using var db = await Open(ct); return await Get(db, id, ct); }
    private static async Task<ProductTemplateDto> Get(NpgsqlConnection db, long id, CancellationToken ct, bool locked = false)
        => (await Read<ProductTemplateDto>(db, "SELECT " + TemplateJson + " FROM public.product_templates WHERE id=@id" + (locked ? " FOR UPDATE" : ""), ct, P("id", id))).SingleOrDefault()
            ?? throw new CrmFault(404, "Szablon nie znaleziony.");
    public async Task<Page<TemplateProjectDto>> Projects(string? q, int page, int pageSize, CancellationToken ct)
    {
        QueryValidation(q, null, null, page, pageSize);
        await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        const string from = " FROM public.product_templates WHERE is_active=true AND project_code IS NOT NULL AND (@q='' OR strpos(lower(project_code),lower(@q))>0)";
        var total = (await Read<long>(db, "SELECT to_json(count(DISTINCT project_code))" + from, ct, P("q", q?.Trim() ?? "")))[0];
        var rows = await Read<TemplateProjectDto>(db, "SELECT json_build_object('code',project_code,'positionsCount',count(*))" + from + " GROUP BY project_code ORDER BY project_code LIMIT @limit OFFSET @offset", ct,
            P("q", q?.Trim() ?? ""), P("limit", pageSize), P("offset", (page - 1) * pageSize));
        await tx.CommitAsync(ct); return new(rows, checked((int)total), page, pageSize);
    }
    public async Task<TemplateFeatures> Features(CancellationToken ct)
    {
        await using var db = await Open(ct);
        var writer = (await Read<string>(db, "SELECT to_json(value) FROM public.settings WHERE key='catalog_writer'", ct)).SingleOrDefault();
        var rate = (await Read<string>(db, "SELECT to_json(value) FROM public.settings WHERE key='labor_rate_pln'", ct)).SingleOrDefault();
        var labor = double.TryParse(rate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : 100;
        return new(true, writer == "dotnet", true, true, true, labor);
    }
    private static void Validate(ProductTemplateDraft d)
    {
        if (d is null) throw CrmFault.Invalid("draft", "Podaj dane szablonu.");
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 200) throw CrmFault.Invalid("name", "Nazwa jest wymagana (do 200 znaków).");
        if (d.Category is null || d.Category.Length > 50 || d.ProjectCode?.Length > 50 || d.PositionNumber?.Length > 50) throw CrmFault.Invalid("category", "Kategoria, projekt i pozycja mogą mieć do 50 znaków.");
        if (d.Notes?.Length > 10000) throw CrmFault.Invalid("notes", "Uwagi mogą mieć do 10000 znaków.");
        if (d.BasePricePln is { } price && (!double.IsFinite(price) || price is < 0 or > 99_999_999.99)) throw CrmFault.Invalid("basePricePln", "Nieprawidłowa cena bazowa.");
        if (!double.IsFinite(d.MarginPct) || d.MarginPct is < 0 or > 1) throw CrmFault.Invalid("marginPct", "Marża musi być od 0 do 1.");
        foreach (var array in new[] { d.Operations, d.Materials, d.Instructions, d.Machines })
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 200 || Encoding.UTF8.GetByteCount(array.GetRawText()) > 60000)
                throw CrmFault.Invalid("rows", "Każda lista może zawierać do 200 pozycji.");
    }
    private static NpgsqlParameter[] DraftArgs(ProductTemplateDraft d) => [P("name", d.Name), P("category", d.Category),
        P("operations", d.Operations.GetRawText()), P("materials", d.Materials.GetRawText()), P("instructions", d.Instructions.GetRawText()), P("machines", d.Machines.GetRawText()),
        P("price", d.BasePricePln), P("margin", d.MarginPct), P("project", d.ProjectCode), P("position", d.PositionNumber), P("notes", d.Notes)];
    public Task<ProductTemplateDto> Create(LegacyUser actor, CreateProductTemplate c, CancellationToken ct)
    {
        RequireTech(actor);
        Validate(c.Draft);
        return Change(actor, c.RequestId, "template_create", null, c, async db => (await Read<ProductTemplateDto>(db, """
            INSERT INTO public.product_templates(name,category,operations_json,materials_json,instruction_blocks,machines_json,base_price_pln,margin_pct,project_code,position_nr,notes,is_active)
            VALUES(@name,@category,@operations::json,@materials::json,@instructions::json,@machines::json,@price,@margin,@project,@position,@notes,true) RETURNING
            """ + " " + TemplateJson, ct, DraftArgs(c.Draft)))[0], ct);
    }
    public Task<ProductTemplateDto> Update(LegacyUser actor, long id, UpdateProductTemplate c, CancellationToken ct)
    {
        RequireTech(actor);
        Validate(c.Draft); RequireVersion(c.ExpectedVersion);
        return Change(actor, c.RequestId, "template_update", id, c, async db =>
        {
            await Version(db, id, c.ExpectedVersion, ct);
            return (await Read<ProductTemplateDto>(db, """
                UPDATE public.product_templates SET name=@name,category=@category,operations_json=@operations::json,materials_json=@materials::json,
                instruction_blocks=@instructions::json,machines_json=@machines::json,base_price_pln=@price,margin_pct=@margin,project_code=@project,position_nr=@position,notes=@notes WHERE id=@id RETURNING
                """ + " " + TemplateJson, ct, [..DraftArgs(c.Draft), P("id", id)]))[0];
        }, ct);
    }
    public Task<ProductTemplateDto> SetActive(LegacyUser actor, long id, ProductTemplateVersionCommand c, bool active, CancellationToken ct)
    {
        RequireTech(actor);
        RequireVersion(c.ExpectedVersion);
        return Change(actor, c.RequestId, active ? "template_restore" : "template_archive", id, c, async db =>
        {
            var row = await Version(db, id, c.ExpectedVersion, ct);
            if (!active && !row.IsActive) throw new CrmFault(409, "Szablon już zarchiwizowany.");
            return (await Read<ProductTemplateDto>(db, "UPDATE public.product_templates SET is_active=@active WHERE id=@id RETURNING " + TemplateJson, ct, P("active", active), P("id", id)))[0];
        }, ct);
    }
    private static void RequireVersion(long version)
    { if (version < 1) throw CrmFault.Invalid("expectedVersion", "Wczytaj aktualną wersję szablonu."); }
    private static async Task<ProductTemplateDto> Version(NpgsqlConnection db, long id, long expected, CancellationToken ct)
    {
        var row = await Get(db, id, ct, true);
        if (row.Version != expected) throw new CrmFault(409, "Szablon zmienił się. Porównaj aktualną wersję przed zapisem.");
        return row;
    }
    internal static void RequireTech(LegacyUser actor)
    { if (actor.Id <= 0 || actor.Role != "technolog") throw new CrmFault(403, "Ta operacja wymaga uprawnień technologa."); }
    private async Task<T> Change<T>(LegacyUser actor, Guid request, string kind, long? target, object payload, Func<NpgsqlConnection, Task<T>> action, CancellationToken ct) where T : class
    {
        RequireTech(actor);
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
        { throw new CrmFault(409, "Szablon jest używany lub identyfikator zapisu należy do innej operacji."); }
    }
    public async Task CheckRuntime(CancellationToken ct)
    {
        await using var db = await Open(ct);
        var ok = (await Read<bool>(db, """
            SELECT to_json(has_table_privilege(current_user,'public.product_templates','SELECT')
              AND has_table_privilege(current_user,'public.product_templates','INSERT')
              AND has_table_privilege(current_user,'public.product_templates','UPDATE')
              AND NOT has_table_privilege(current_user,'public.product_templates','DELETE')
              AND has_sequence_privilege(current_user,pg_get_serial_sequence('public.product_templates','id'),'USAGE')
              AND (SELECT count(*) FROM pg_trigger WHERE tgrelid='public.product_templates'::regclass
                  AND tgname IN ('require_template_writer','increment_catalog_version') AND tgenabled='O')=2)
            """, ct)).SingleOrDefault();
        if (!ok) throw new CrmFault(503, "Skonfiguruj ograniczone uprawnienia szablonów.");
    }
    private sealed record Receipt(long Actor, string Kind, long? Target, string Hash, JsonElement Response);
}
