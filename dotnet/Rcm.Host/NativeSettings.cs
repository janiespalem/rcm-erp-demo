using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed class NativeSettings(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static void RequireRead(LegacyUser actor)
    { if (actor.Id <= 0 || actor.Role is not ("biuro" or "technolog" or "ceo")) throw new CrmFault(403, "Brak dostępu do ustawień."); }
    internal static bool Control(string key) => key.EndsWith("_writer", StringComparison.OrdinalIgnoreCase);
    internal static void Validate(string key, string? value)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 50) throw CrmFault.Invalid("key", "Klucz wymaga od 1 do 50 znaków.");
        if (Control(key)) throw new CrmFault(403, "Zmiana obsługi modułu wymaga kontrolowanego wdrożenia.");
        if (value is null || value.Length > 200 || value.Contains('\0') || key.Contains('\0')) throw CrmFault.Invalid("newValue", "Wartość wymaga tekstu do 200 znaków.");
        var range = key switch
        {
            "labor_rate_pln" => (0.01, 10_000d), "min_order_value" => (0d, 1_000_000d),
            "default_overhead_pct" or "default_margin_pct" => (0d, 1d), _ => ((double, double)?)null
        };
        if (range is { } bounds && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number) || number < bounds.Item1 || number > bounds.Item2))
            throw CrmFault.Invalid("newValue", $"Podaj liczbę od {bounds.Item1.ToString(CultureInfo.InvariantCulture)} do {bounds.Item2.ToString(CultureInfo.InvariantCulture)}.");
    }
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var db = new NpgsqlConnection(configuration.GetConnectionString("Catalog") ?? throw new CrmFault(503, "Ustawienia katalogu nie zostały skonfigurowane."));
        try { await db.OpenAsync(ct); return db; } catch { await db.DisposeAsync(); throw; }
    }
    private static async Task<T[]> Read<T>(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct); List<T> result = [];
        while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return result.ToArray();
    }
    private static async Task Execute(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    { await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync(ct); }
    public async Task<SettingDto[]> List(LegacyUser actor, CancellationToken ct)
    {
        RequireRead(actor); await using var db = await Open(ct);
        return await Read<SettingDto>(db, "SELECT json_build_object('key',key,'value',value,'label',label,'version',version_id) FROM public.settings WHERE right(lower(key),7)<>'_writer' ORDER BY key", ct);
    }
    public async Task<SettingFeatures> Features(LegacyUser actor, CancellationToken ct)
    {
        RequireRead(actor); await using var db = await Open(ct);
        var writer = (await Read<string>(db, "SELECT to_json(value) FROM public.settings WHERE key='catalog_writer'", ct)).SingleOrDefault();
        return new(true, actor.Role == "technolog" && writer == "dotnet");
    }
    public async Task<SettingDto> Update(LegacyUser actor, string key, UpdateSetting command, CancellationToken ct)
    {
        RequireRead(actor); if (actor.Role != "technolog") throw new CrmFault(403, "Edycja ustawień wymaga uprawnień technologa.");
        Validate(key, command.NewValue);
        if (command.RequestId == Guid.Empty) throw CrmFault.Invalid("requestId", "Identyfikator zapisu jest wymagany.");
        if (command.ExpectedVersion < 1 || command.ExpectedVersion == long.MaxValue) throw CrmFault.Invalid("expectedVersion", "Wczytaj aktualną wersję ustawienia.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { key, command }, Json))));
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("catalog:" + command.RequestId)));
        await using var db = await Open(ct); await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            await Execute(db, "SET LOCAL lock_timeout='5s'", ct);
            if ((await Read<string>(db, "SELECT to_json(public.lock_catalog_writer())", ct)).SingleOrDefault() != "dotnet")
                throw new CrmFault(503, "Zapisy katalogu nie zostały przełączone.");
            await Execute(db, "SET LOCAL rcm.catalog_writer='dotnet'", ct);
            await Execute(db, "SELECT pg_advisory_xact_lock(@key)", ct, new NpgsqlParameter("key", lockKey));
            var receipt = (await Read<Receipt>(db, "SELECT json_build_object('actor',actor_id,'kind',command_type,'target',target_id,'hash',payload_hash,'response',response_json) FROM public.catalog_command_receipts WHERE request_id=@request", ct,
                new NpgsqlParameter("request", command.RequestId.ToString()))).SingleOrDefault();
            if (receipt is not null)
            {
                if (receipt.Actor != actor.Id || receipt.Kind != "setting_update" || receipt.Target is not null || receipt.Hash != hash)
                    throw new CrmFault(409, "Identyfikator zapisu należy do innej operacji lub użytkownika.");
                return receipt.Response.Deserialize<SettingDto>(Json) ?? throw new CrmFault(503, "Nie można odczytać potwierdzenia zapisu.");
            }
            var result = (await Read<SettingDto>(db, "SELECT public.update_business_setting(@key,@value,@version)", ct,
                new NpgsqlParameter("key", key), new NpgsqlParameter("value", command.NewValue), new NpgsqlParameter("version", command.ExpectedVersion)))[0];
            await Execute(db, "INSERT INTO public.catalog_command_receipts(request_id,actor_id,command_type,target_id,payload_hash,response_json) VALUES(@request,@actor,'setting_update',NULL,@hash,@response::json)", ct,
                new NpgsqlParameter("request", command.RequestId.ToString()), new NpgsqlParameter("actor", actor.Id), new NpgsqlParameter("hash", hash), new NpgsqlParameter("response", JsonSerializer.Serialize(result, Json)));
            await tx.CommitAsync(ct); return result;
        }
        catch (PostgresException error) when (error.SqlState == "P0002") { throw new CrmFault(404, "Ustawienie nie istnieje."); }
        catch (PostgresException error) when (error.SqlState == "40001") { throw new CrmFault(409, "Ustawienie zmieniło się. Porównaj aktualną wersję przed zapisem."); }
        catch (PostgresException error) when (error.SqlState is "55P03" or "55000" or "40P01") { throw new CrmFault(503, "Trwa zapis lub przełączenie katalogu. Ponów tę samą operację."); }
    }
    public async Task CheckRuntime(CancellationToken ct)
    {
        await using var db = await Open(ct);
        var ready = (await Read<bool>(db, """
            SELECT to_json(has_function_privilege(current_user,'public.update_business_setting(text,text,bigint)','EXECUTE')
                AND NOT has_table_privilege(current_user,'public.settings','INSERT,UPDATE,DELETE')
                AND NOT EXISTS(SELECT 1 FROM information_schema.columns c WHERE c.table_schema='public' AND c.table_name='settings'
                    AND has_column_privilege(current_user,'public.settings',c.column_name,'UPDATE'))
                AND EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid='public.update_business_setting(text,text,bigint)'::regprocedure
                    AND p.prosecdef AND EXISTS(SELECT 1 FROM unnest(p.proconfig) setting
                        WHERE regexp_replace(setting,'[[:space:]]','','g')='search_path=pg_catalog,pg_temp')
                    AND NOT EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) acl
                        WHERE acl.grantee=0 AND acl.privilege_type='EXECUTE'))
                AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='public.settings'::regclass AND tgname='increment_setting_version' AND tgenabled='O'))
            """, ct)).SingleOrDefault();
        if (!ready) throw new CrmFault(503, "Skonfiguruj ograniczoną zmianę ustawień katalogu.");
    }
    private sealed record Receipt(long Actor, string Kind, long? Target, string Hash, JsonElement Response);
}
