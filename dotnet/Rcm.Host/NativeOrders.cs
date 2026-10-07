using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Rcm.Contracts;
using Rcm.Crm;

namespace Rcm.Host;

internal sealed partial class NativeOrders(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new UtcTimestampConverter(), new LegacyMaterialConverter() }
    };
    private const string OrderSelect = "SELECT row_to_json(o) FROM public.orders o";
    private static readonly string[] Statuses = ["draft", "standard", "niestandard", "quoted", "rejected", "in_production", "gotowe", "wydane"];
    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Europe/Warsaw").DateTime);

    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(configuration.GetConnectionString("Orders")
            ?? throw new CrmFault(503, "Obsługa zleceń nie została skonfigurowana."));
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static NpgsqlParameter P(string name, object? value) => value is null
        ? new(name, NpgsqlDbType.Unknown) { Value = DBNull.Value }
        : new(name, value);
    public async Task CheckRuntime(CancellationToken ct)
    {
        await using var db = await Open(ct);
        var ready = (await Read<bool>(db, """
            SELECT to_json(
              NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=current_user AND (rolsuper OR rolcreatedb OR rolcreaterole))
              AND has_function_privilege(current_user,'public.lock_order_writer()','EXECUTE')
              AND NOT has_table_privilege(current_user,'public.settings','UPDATE')
              AND NOT has_column_privilege(current_user,'public.settings','value','UPDATE')
              AND NOT has_table_privilege(current_user,'public.users','SELECT')
              AND (SELECT bool_and(has_table_privilege(current_user, t, 'SELECT')) FROM unnest(ARRAY[
                'public.orders','public.quotes','public.order_events','public.order_counters','public.order_create_receipts',
                'public.order_command_receipts','public.order_attachments','public.product_templates','public.approved_materials',
                'public.operation_catalog','public.constraint_rules','public.settings','public.order_operations',
                'public.parameter_requests','public.stock_movements','public.price_history']) t)
              AND (SELECT bool_and(has_table_privilege(current_user,t,'INSERT')) FROM unnest(ARRAY[
                'public.orders','public.quotes','public.order_events','public.order_counters','public.order_create_receipts',
                'public.order_command_receipts','public.order_attachments','public.product_templates','public.order_operations',
                'public.parameter_requests','public.stock_movements','public.price_history']) t)
              AND (SELECT bool_and(has_table_privilege(current_user,t,'UPDATE')) FROM unnest(ARRAY[
                'public.orders','public.quotes','public.order_counters','public.order_operations','public.parameter_requests']) t)
              AND has_table_privilege(current_user,'public.order_attachments','DELETE'))
            """, ct))[0];
        if (!ready) throw new CrmFault(503, "Ograniczone uprawnienia modułu zleceń wymagają konfiguracji.");
    }
    private static async Task<T[]> Read<T>(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] args)
    {
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddRange(args);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return rows.ToArray();
    }
    private static async Task<int> Execute(NpgsqlConnection db, string sql, CancellationToken ct, params NpgsqlParameter[] args)
    {
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddRange(args);
        return await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task EnsureWriter(NpgsqlConnection db, CancellationToken ct)
    {
        await Execute(db, "SET LOCAL lock_timeout = '5s'", ct);
        var writer = await Read<string>(db, "SELECT to_json(public.lock_order_writer())", ct);
        if (writer.SingleOrDefault() != "dotnet") throw new CrmFault(503, "Zapisy zleceń nie zostały przełączone na nowy serwer.");
        await Execute(db, "SET LOCAL rcm.orders_writer = 'dotnet'", ct);
    }
    private static async Task<OrderDto> GetOrder(NpgsqlConnection db, long id, CancellationToken ct, bool locked = false)
        => (await Read<OrderDto>(db, OrderSelect + " WHERE id=@id" + (locked ? " FOR UPDATE" : ""), ct, P("id", id))).SingleOrDefault()
            ?? throw new CrmFault(404, "Zlecenie nie znalezione.");
    private static async Task<OrderDto> LockedOrder(NpgsqlConnection db, long id, CancellationToken ct)
    {
        var order = await GetOrder(db, id, ct, true);
        if (order.ArchivedAt is not null) throw new CrmFault(409, "Zarchiwizowane zlecenie jest tylko do odczytu.");
        return order;
    }
    private static Task Event(NpgsqlConnection db, LegacyUser actor, long id, string eventType, string? oldStatus, string? newStatus, string? note, CancellationToken ct)
        => Execute(db, """
            INSERT INTO public.order_events(order_id,user_role,user_name,event_type,old_status,new_status,note,created_at)
            VALUES(@id,@role,@name,@event,@old,@new,@note,timezone('UTC',now()))
            """, ct, P("id", id), P("role", actor.Role), P("name", actor.Name), P("event", eventType), P("old", oldStatus), P("new", newStatus), P("note", note));
    private static void RequireEdit(LegacyUser actor, bool technologOnly = false)
    {
        if (actor.Id <= 0 || actor.Role != "technolog" && (technologOnly || actor.Role != "biuro"))
            throw new CrmFault(403, "Brak uprawnień do tej operacji.");
    }

    public async Task<Page<OrderDto>> Query(string? q, string? status, bool archived, int page, int pageSize, CancellationToken ct, bool includePendingQuestions = false)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100 || q?.Length > 200)
            throw CrmFault.Invalid("page", "Wybierz stronę i rozmiar od 1 do 100 pozycji.");
        if (!string.IsNullOrEmpty(status) && !Statuses.Contains(status)) throw CrmFault.Invalid("status", "Nieprawidłowy status zlecenia.");
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        const string filter = " WHERE (archived_at IS NOT NULL)=@archived AND (@status='' OR status::text=@status) AND (@q='' OR strpos(lower(client),@q)>0 OR strpos(lower(order_number),@q)>0 OR strpos(lower(description),@q)>0)";
        NpgsqlParameter[] Args() => [P("archived", archived), P("status", status ?? ""), P("q", q?.Trim().ToLowerInvariant() ?? "")];
        var total = (await Read<long>(db, "SELECT to_json(count(*)) FROM public.orders" + filter, ct, Args()))[0];
        var rows = await Read<OrderDto>(db, OrderSelect + filter + " ORDER BY created_at DESC,id DESC LIMIT @limit OFFSET @offset", ct,
            [..Args(), P("limit", pageSize), P("offset", (page - 1) * pageSize)]);
        if (includePendingQuestions && rows.Length > 0)
        {
            var pending = await Read<PendingQuestionCount>(db, """
                SELECT json_build_object('order_id',order_id,'count',count(*)::int) FROM public.parameter_requests
                WHERE order_id=ANY(@ids) AND status::text='pending' GROUP BY order_id
                """, ct, P("ids", rows.Select(row => row.Id).ToArray()));
            var counts = pending.ToDictionary(row => row.OrderId, row => row.Count);
            rows = rows.Select(row => row with { PendingQuestions = counts.GetValueOrDefault(row.Id) }).ToArray();
        }
        await tx.CommitAsync(ct);
        return new(rows, checked((int)total), page, pageSize);
    }
    public async Task<OrderDto> Detail(long id, CancellationToken ct)
    { await using var db = await Open(ct); return await GetOrder(db, id, ct); }
    public async Task<OrderEventDto[]> Events(long id, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await GetOrder(db, id, ct);
        return await Read<OrderEventDto>(db, "SELECT row_to_json(e) FROM public.order_events e WHERE order_id=@id ORDER BY created_at ASC,id ASC", ct, P("id", id));
    }
    public async Task<OrderLookups> Lookups(CancellationToken ct)
    {
        await using var db = await Open(ct);
        return new(await Read<OrderTemplateDto>(db, "SELECT row_to_json(t) FROM public.product_templates t WHERE is_active ORDER BY id", ct),
            await Read<OrderApprovedMaterialDto>(db, "SELECT row_to_json(m) FROM public.approved_materials m WHERE is_active ORDER BY id", ct),
            await Read<OrderOperationDto>(db, "SELECT row_to_json(o) FROM public.operation_catalog o ORDER BY name", ct),
            await ReadLaborRate(db, ct));
    }
    private static async Task<double> ReadLaborRate(NpgsqlConnection db, CancellationToken ct)
    {
        var value = (await Read<string>(db, "SELECT to_json(value) FROM public.settings WHERE key='labor_rate_pln'", ct)).SingleOrDefault();
        if (value is null) return 100;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || !double.IsFinite(rate) || rate is <= 0 or > 10_000)
            throw new CrmFault(503, "Nieprawidłowa stawka robocizny na serwerze.");
        return rate;
    }
    private static void Validate(OrderFields? f)
    {
        if (f is null) throw CrmFault.Invalid("fields", "Podaj dane zlecenia.");
        var errors = new Dictionary<string, string[]>();
        void Length(string key, string? value, int max) { if (value?.Length > max) errors[key] = [$"Maksymalnie {max} znaków."]; }
        if (f.Client is null) errors["client"] = ["Podaj klienta."];
        Length("client", f.Client, 200); Length("orderNumber", f.OrderNumber, 20); Length("material", f.Material, 100);
        Length("orderType", f.OrderType, 20); Length("sopName", f.SopName, 200); Length("drawingNumber", f.DrawingNumber, 50);
        Length("dimensions", f.Dimensions, 120); Length("contact", f.Contact, 160);
        if (f.Quantity is < 1 or > 1_000_000) errors["quantity"] = ["Podaj ilość od 1 do 1 000 000."];
        if (!double.IsFinite(f.EstimatedValue) || f.EstimatedValue is < 0 or > 99_999_999.99) errors["estimatedValue"] = ["Nieprawidłowa wartość."];
        if (f.WeightKg is { } weight && (!double.IsFinite(weight) || weight is < 0 or > 9_999_999.999)) errors["weightKg"] = ["Nieprawidłowa masa."];
        if (errors.Count != 0) throw new CrmFault(422, "Sprawdź dane zlecenia.", errors);
    }
    private static string FieldJson(OrderFields f) => JsonSerializer.Serialize(f with { MaterialsJson = f.MaterialsJson ?? [] }, Json);
    private const string EditableColumns = "order_number,client,deadline,approved_material_id,material,has_drawing,order_type,sop_name,purpose,notes,estimated_value,description,requires_visit,quantity,is_defence,weight_kg,drawing_number,dimensions,delivery_address,contact";

    public async Task<OrderDto> Create(LegacyUser actor, CreateOrder command, CancellationToken ct) =>
        (await CreateCore(actor, command, false, ct)).Order;

    public async Task<OrderIntakeResult> Intake(LegacyUser actor, CreateOrder command, CancellationToken ct)
    {
        var result = await CreateCore(actor, command, true, ct);
        return new(result.Order, result.Triage!);
    }

    private async Task<(OrderDto Order, OrderTriageResult? Triage)> CreateCore(LegacyUser actor,
        CreateOrder command, bool intake, CancellationToken ct)
    {
        RequireEdit(actor); Validate(command.Fields);
        if (command.RequestId == Guid.Empty) throw CrmFault.Invalid("requestId", "Identyfikator zapisu jest wymagany.");
        var payload = FieldJson(command.Fields);
        var hash = (intake ? "ci1:" : "cs1:") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..60];
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        // Match the Python receipt lock so a switch cannot let an in-flight retry create twice.
        var key = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(command.RequestId.ToString())));
        await Execute(db, "SELECT pg_advisory_xact_lock(@key)", ct, P("key", key));
        var receipt = (await Read<CreateReceipt>(db, "SELECT row_to_json(r) FROM public.order_create_receipts r WHERE request_id=@request", ct, P("request", command.RequestId.ToString()))).SingleOrDefault();
        if (receipt is not null)
        {
            if (receipt.ActorId != actor.Id || receipt.PayloadHash != hash)
                throw new CrmFault(409, "Identyfikator zapisu należy do innej operacji lub poprzedniej wersji serwera. Sprawdź listę zleceń przed ponowieniem.");
            if (!intake) return (receipt.ResponseJson.Deserialize<OrderDto>(Json)!, null);
            var previous = receipt.ResponseJson.Deserialize<OrderIntakeResult>(Json)!;
            return (previous.Order, previous.Triage);
        }
        var order = await InsertOrder(db, command.Fields, ct);
        await Event(db, actor, order.Id, "created", null, "draft", null, ct);
        OrderTriageResult? triage = null;
        if (intake)
        {
            triage = await TriageInTransaction(db, actor, order, ct);
            order = await LockedOrder(db, order.Id, ct);
        }
        object response = intake ? new OrderIntakeResult(order, triage!) : order;
        await Execute(db, """
            INSERT INTO public.order_create_receipts(request_id,actor_id,payload_hash,response_json,created_at)
            VALUES(@request,@actor,@hash,@response::json,now())
            """, ct, P("request", command.RequestId.ToString()), P("actor", actor.Id), P("hash", hash), P("response", JsonSerializer.Serialize(response, Json)));
        await tx.CommitAsync(ct);
        return (order, triage);
    }

    private static async Task<OrderDto> InsertOrder(NpgsqlConnection db, OrderFields fields, CancellationToken ct)
    {
        var number = fields.OrderNumber?.Trim();
        if (fields.OrderNumber is not null && number == "") throw CrmFault.Invalid("orderNumber", "Numer zlecenia nie może być pusty.");
        if (number is null)
        {
            await Execute(db, "INSERT INTO public.order_counters(year,next_seq) VALUES(@year,1) ON CONFLICT(year) DO NOTHING", ct, P("year", Today.Year));
            var sequence = (await Read<long>(db, """
                UPDATE public.order_counters SET next_seq=greatest(next_seq,
                  coalesce((SELECT max(split_part(order_number,'/',1)::bigint)+1 FROM public.orders
                    WHERE order_number ~ @pattern),1))+1
                WHERE year=@year RETURNING to_json(next_seq-1)
                """, ct, P("year", Today.Year), P("pattern", "^[0-9]+/" + Today.Year + "$")))[0];
            number = $"{sequence}/{Today.Year}";
        }
        fields = fields with { OrderNumber = number };
        var columns = EditableColumns + ",materials_json,template_id,is_internal";
        OrderDto order;
        try
        {
            order = (await Read<OrderDto>(db, $"""
                INSERT INTO public.orders({columns},status,version_id,created_at)
                SELECT {columns},'draft',1,timezone('UTC',now()) FROM json_populate_record(NULL::public.orders, @payload::json)
                RETURNING row_to_json(orders)
                """, ct, P("payload", FieldJson(fields))))[0];
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { throw new CrmFault(409, "Numer zlecenia jest już używany."); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation) { throw CrmFault.Invalid("fields", "Wybrany materiał lub produkt już nie istnieje."); }
        return order;
    }
    public async Task<OrderDto> Edit(LegacyUser actor, long id, EditOrder command, CancellationToken ct)
    {
        RequireEdit(actor); Validate(command.Fields);
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await EnsureWriter(db, ct);
        var old = await LockedOrder(db, id, ct);
        if (command.ExpectedVersion != old.VersionId) throw new CrmFault(409, "Zlecenie zostało zmienione. Porównaj aktualną wersję; formularz pozostaje otwarty.");
        var number = command.Fields.OrderNumber?.Trim();
        if (command.Fields.OrderNumber is not null && number == "") throw CrmFault.Invalid("orderNumber", "Numer zlecenia nie może być pusty.");
        var fields = command.Fields with { OrderNumber = number };
        OrderDto result;
        try
        {
            result = (await Read<OrderDto>(db, $"""
                UPDATE public.orders SET ({EditableColumns})=(SELECT {EditableColumns} FROM json_populate_record(NULL::public.orders,@payload::json)),version_id=version_id+1
                WHERE id=@id RETURNING row_to_json(orders)
                """, ct, P("id", id), P("payload", FieldJson(fields))))[0];
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { throw new CrmFault(409, "Numer zlecenia jest już używany."); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation) { throw CrmFault.Invalid("approvedMaterialId", "Wybrany materiał już nie istnieje."); }
        await Event(db, actor, id, "edited", null, null, "Dane zlecenia", ct);
        await tx.CommitAsync(ct);
        return result;
    }
    private sealed record CreateReceipt(long ActorId, string PayloadHash, JsonElement ResponseJson);
    private sealed record PendingQuestionCount(long OrderId, int Count);
    private sealed class LegacyMaterialConverter : JsonConverter<OrderMaterialDto>
    {
        public override OrderMaterialDto Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement;
            if (value.ValueKind == JsonValueKind.String) return new(value.GetString());
            string? name = null;
            if (value.ValueKind != JsonValueKind.Object) return new();
            foreach (var alias in new[] { "name", "material", "mat" })
                if (value.TryGetProperty(alias, out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(text.GetString())) { name = text.GetString(); break; }
            return new(name, value.TryGetProperty("qty_kg", out var qty) && qty.ValueKind == JsonValueKind.Number && qty.TryGetDouble(out var number) ? number : 0);
        }
        public override void Write(Utf8JsonWriter writer, OrderMaterialDto value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteString("name", value.Name); writer.WriteNumber("qty_kg", value.QtyKg); writer.WriteEndObject();
        }
    }
    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUniversalTime());
    }
}
