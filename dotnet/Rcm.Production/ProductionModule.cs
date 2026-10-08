using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Rcm.Calculators;
using Rcm.Contracts;

namespace Rcm.Production;

public sealed record ProductionActor(long UserId, string Role);
public sealed class ProductionFault(int status, string message, Dictionary<string, string[]>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public Dictionary<string, string[]>? Errors { get; } = errors;
    public static ProductionFault Invalid(string field, string message) => new(422, "Sprawdź formularz.", new() { [field] = [message] });
    public static ProductionFault Conflict() => new(409, "Dane zostały zmienione. Wczytaj aktualną wersję i porównaj zmiany.");
}

public sealed class ProductionModule(ProductionDb db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now
    {
        get { var value = clock.GetUtcNow(); return new(value.Ticks - value.Ticks % 10, TimeSpan.Zero); }
    }
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).DateTime);
    public static bool CanRead(ProductionActor actor) => actor.UserId > 0 && actor.Role is "biuro" or "technolog" or "ceo" or "produkcja";
    public static bool CanWrite(ProductionActor actor) => actor.UserId > 0 && actor.Role is "biuro" or "technolog";

    private static void Require(ProductionActor actor, bool write = false)
    {
        if (!(write ? CanWrite(actor) : CanRead(actor))) throw new ProductionFault(403, "Brak uprawnień do modułu produkcji.");
    }
    public async Task<ProductionFeatures> Features(ProductionActor actor, CancellationToken ct)
    {
        Require(actor); await CheckRuntime(ct);
        return new(true, CanWrite(actor), Today);
    }
    private static void PageCheck(int page, int pageSize, string? q = null)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100 || q?.Length > 200)
            throw ProductionFault.Invalid("page", "Rozmiar strony od 1 do 100, wyszukiwanie do 200 znaków.");
    }
    private static void Limit(string? value, int maximum, string field)
    {
        if (value?.Length > maximum) throw ProductionFault.Invalid(field, $"Maksymalnie {maximum} znaków.");
    }
    private static void Masses(SteelMasses? masses, string field)
    {
        if (masses is not null && new[] { masses.Diameter6, masses.Diameter12, masses.Diameter16 }.Any(m => m is < 0 or > 1_000_000_000_000))
            throw ProductionFault.Invalid(field, "Masa w gramach musi być nieujemna i nie przekraczać 1000000000000.");
    }
    private ProductionContractFields Fields(ProductionContractFields? f)
    {
        if (f is null || string.IsNullOrWhiteSpace(f.Name)) throw ProductionFault.Invalid("name", "Wpisz nazwę kontraktu.");
        Limit(f.Name, 240, "name"); Limit(f.Reference, 240, "reference");
        if (f.PlannedTetrapods is < 0 or > 1_000_000) throw ProductionFault.Invalid("plannedTetrapods", "Ilość musi być od 0 do 1000000.");
        if (f.OpeningDate > Today) throw ProductionFault.Invalid("openingDate", "Stan początkowy nie może pochodzić z przyszłości.");
        Masses(f.OpeningSteel, "openingSteel");
        if (f.OpeningDate is null && f.OpeningSteel is { } steel && (steel.Diameter6 is not null || steel.Diameter12 is not null || steel.Diameter16 is not null))
            throw ProductionFault.Invalid("openingDate", "Podaj datę stanu początkowego stali.");
        return f with { Name = f.Name.Trim(), Reference = f.Reference?.Trim() ?? "", OpeningSteel = f.OpeningSteel ?? new() };
    }
    private SteelDeliveryFields Fields(SteelDeliveryFields? f)
    {
        if (f?.Steel is null) throw ProductionFault.Invalid("steel", "Podaj masę dostawy.");
        if (f.DeliveryDate == default) throw ProductionFault.Invalid("deliveryDate", "Podaj datę dostawy.");
        if (f.DeliveryDate > Today) throw ProductionFault.Invalid("deliveryDate", "Data dostawy nie może pochodzić z przyszłości.");
        Masses(f.Steel, "steel");
        if (!(f.Steel.Diameter6 > 0 || f.Steel.Diameter12 > 0 || f.Steel.Diameter16 > 0))
            throw ProductionFault.Invalid("steel", "Podaj przynajmniej jedną dodatnią masę.");
        Limit(f.Note, 8000, "note"); return f with { Note = f.Note?.Trim() ?? "" };
    }
    private static string Reason(string? reason, bool required)
    {
        Limit(reason, 2000, "reason");
        if (required && string.IsNullOrWhiteSpace(reason)) throw ProductionFault.Invalid("reason", "Podaj powód korekty.");
        return reason?.Trim() ?? "";
    }
    private static void Version(long actual, long expected, string field = "expectedVersion")
    {
        if (expected < 1) throw ProductionFault.Invalid(field, "Wczytaj wersję zapisu.");
        if (actual != expected) throw ProductionFault.Conflict();
    }
    private static ProductionContractFields Fields(ProductionContract c) => new(c.Name, c.Reference, c.PlannedTetrapods,
        c.Deadline, c.OpeningDate, new(c.Opening6, c.Opening12, c.Opening16), c.NormConfirmed);
    private static SteelDeliveryFields Fields(SteelDelivery d) => new(d.DeliveryDate, new(d.Steel6, d.Steel12, d.Steel16), d.Note);
    private static void Apply(ProductionContract c, ProductionContractFields f)
    {
        c.Name = f.Name; c.Reference = f.Reference; c.PlannedTetrapods = f.PlannedTetrapods; c.Deadline = f.Deadline;
        c.OpeningDate = f.OpeningDate; c.Opening6 = f.OpeningSteel?.Diameter6; c.Opening12 = f.OpeningSteel?.Diameter12;
        c.Opening16 = f.OpeningSteel?.Diameter16; c.NormConfirmed = f.NormConfirmed;
    }
    private static void Apply(SteelDelivery d, SteelDeliveryFields f)
    {
        d.DeliveryDate = f.DeliveryDate; d.Steel6 = f.Steel.Diameter6; d.Steel12 = f.Steel.Diameter12;
        d.Steel16 = f.Steel.Diameter16; d.Note = f.Note;
    }
    private ProductionContractDto Dto(ProductionContract c, SteelMasses delivered)
    {
        long? Total(long? initial, long? added)
        {
            if (c.OpeningDate is null || initial is null || added is null) return null;
            try { return checked(initial.Value + added.Value); }
            catch (OverflowException) { throw new ProductionFault(503, "Suma dostaw przekracza obsługiwany zakres. Skontaktuj się z administratorem."); }
        }
        var masses = new SteelMasses(Total(c.Opening6, delivered.Diameter6), Total(c.Opening12, delivered.Diameter12), Total(c.Opening16, delivered.Diameter16));
        var norm = new ProductionNorm(c.BasketsPerTetrapod, new(c.Norm6, c.Norm12, c.Norm16));
        try { return new(c.Id, c.Version, Fields(c), norm, TetrapodCoverage.Calculate(masses, norm, c.PlannedTetrapods), c.IsSynthetic, c.CreatedAt, c.UpdatedAt); }
        catch (ArgumentException) { throw new ProductionFault(503, "Nieobsługiwana norma kontraktu. Skontaktuj się z administratorem."); }
        catch (OverflowException) { throw new ProductionFault(503, "Wynik wyliczenia przekracza obsługiwany zakres."); }
    }
    private static SteelDeliveryDto Dto(SteelDelivery d, ProductionContract c) => new(d.Id, d.ContractId, d.Version, Fields(d),
        d.CreatedBy, d.CreatedAt, d.UpdatedBy, d.UpdatedAt, c.OpeningDate is { } date && d.DeliveryDate < date);
    private sealed record DeliveryTotals(Guid ContractId, long? Steel6, long? Steel12, long? Steel16);
    private async Task<Dictionary<Guid, SteelMasses>> Delivered(Guid[] ids, CancellationToken ct)
    {
        try
        {
            var rows = await db.Deliveries.AsNoTracking().Join(db.Contracts.AsNoTracking(), d => d.ContractId, c => c.Id,
                    (d, c) => new { Delivery = d, c.OpeningDate })
                .Where(x => ids.Contains(x.Delivery.ContractId) && x.OpeningDate != null && x.Delivery.DeliveryDate >= x.OpeningDate)
                .GroupBy(x => x.Delivery.ContractId)
                .Select(g => new DeliveryTotals(g.Key,
                    g.Count(x => x.Delivery.Steel6 == null) > 0 ? null : g.Sum(x => x.Delivery.Steel6),
                    g.Count(x => x.Delivery.Steel12 == null) > 0 ? null : g.Sum(x => x.Delivery.Steel12),
                    g.Count(x => x.Delivery.Steel16 == null) > 0 ? null : g.Sum(x => x.Delivery.Steel16))).ToArrayAsync(ct);
            return rows.ToDictionary(x => x.ContractId, x => new SteelMasses(x.Steel6, x.Steel12, x.Steel16));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
        { throw new ProductionFault(503, "Suma dostaw przekracza obsługiwany zakres."); }
    }
    private async Task<ProductionContractDto> Dto(ProductionContract c, CancellationToken ct) => Dto(c,
        (await Delivered([c.Id], ct)).GetValueOrDefault(c.Id, new(0, 0, 0)));
    private async Task<ProductionContract> Find(Guid id, CancellationToken ct) =>
        await db.Contracts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new ProductionFault(404, "Nie znaleziono kontraktu.");
    private async Task<ProductionContract> Lock(Guid id, CancellationToken ct) =>
        await db.Contracts.FromSqlInterpolated($"SELECT * FROM production.\"Contracts\" WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new ProductionFault(404, "Nie znaleziono kontraktu.");

    public async Task<Page<ProductionContractDto>> Contracts(ProductionActor actor, string? q, int page, int pageSize, CancellationToken ct)
    {
        Require(actor); PageCheck(page, pageSize, q); await using var snapshot = await ReadSnapshot(ct);
        var query = db.Contracts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q)) { var term = q.Trim().ToLowerInvariant(); query = query.Where(c => c.Name.ToLower().Contains(term) || c.Reference.ToLower().Contains(term)); }
        var count = await query.CountAsync(ct);
        var contracts = await query.OrderBy(c => c.Name).ThenBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        var ids = contracts.Select(c => c.Id).ToArray();
        var deliveries = await Delivered(ids, ct);
        return new(contracts.Select(c => Dto(c, deliveries.GetValueOrDefault(c.Id, new(0, 0, 0)))).ToArray(), count, page, pageSize);
    }
    public async Task<ProductionContractDto> Contract(ProductionActor actor, Guid id, CancellationToken ct)
    {
        Require(actor); await using var snapshot = await ReadSnapshot(ct); return await Dto(await Find(id, ct), ct);
    }
    public async Task<Page<SteelDeliveryDto>> Deliveries(ProductionActor actor, Guid id, int page, int pageSize, CancellationToken ct)
    {
        Require(actor); PageCheck(page, pageSize); await using var snapshot = await ReadSnapshot(ct); var c = await Find(id, ct);
        var query = db.Deliveries.AsNoTracking().Where(d => d.ContractId == id); var count = await query.CountAsync(ct);
        var deliveries = await query.OrderByDescending(d => d.DeliveryDate).ThenByDescending(d => d.CreatedAt).ThenBy(d => d.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return new(deliveries.Select(d => Dto(d, c)).ToArray(), count, page, pageSize);
    }
    public async Task<SteelDeliveryDto> Delivery(ProductionActor actor, Guid id, Guid deliveryId, CancellationToken ct)
    {
        Require(actor); await using var snapshot = await ReadSnapshot(ct); var c = await Find(id, ct);
        var delivery = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(d => d.ContractId == id && d.Id == deliveryId, ct)
            ?? throw new ProductionFault(404, "Nie znaleziono dostawy.");
        return Dto(delivery, c);
    }
    public async Task<Page<ProductionChangeDto>> Audit(ProductionActor actor, Guid id, int page, int pageSize, CancellationToken ct)
    {
        Require(actor); PageCheck(page, pageSize); await using var snapshot = await ReadSnapshot(ct); await Find(id, ct);
        var query = db.Changes.AsNoTracking().Where(a => a.ContractId == id); var count = await query.CountAsync(ct);
        var changes = await query.OrderByDescending(a => a.RecordedAt).ThenBy(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new ProductionChangeDto(a.Id, a.ActorId, a.Action, a.Reason, a.Changes, a.RecordedAt)).ToArrayAsync(ct);
        return new(changes, count, page, pageSize);
    }
    private void Audit(ProductionActor actor, Guid contract, string action, string reason, object changes)
        => db.Changes.Add(new() { Id = Guid.NewGuid(), ContractId = contract, ActorId = actor.UserId, Action = action,
            Reason = reason, Changes = JsonSerializer.Serialize(changes, Json), RecordedAt = Now });
    private static Dictionary<string, object> Changes(object before, object after)
    {
        var a = JsonSerializer.SerializeToElement(before, Json); var b = JsonSerializer.SerializeToElement(after, Json);
        return b.EnumerateObject().Where(p => a.GetProperty(p.Name).GetRawText() != p.Value.GetRawText())
            .ToDictionary(p => p.Name, p => (object)new { before = a.GetProperty(p.Name), after = p.Value });
    }
    public Task<ProductionContractDto> Create(ProductionActor actor, CreateProductionContract cmd, CancellationToken ct) =>
        Command(actor, cmd.RequestId, "contract:create", cmd, async () =>
        {
            var fields = Fields(cmd.Fields); var c = new ProductionContract { Id = Guid.NewGuid(), IsSynthetic = cmd.IsSynthetic, CreatedAt = Now, UpdatedAt = Now };
            Apply(c, fields); db.Contracts.Add(c); Audit(actor, c.Id, "contract_created", "", new { fields, norm = new ProductionNorm(c.BasketsPerTetrapod, new(c.Norm6, c.Norm12, c.Norm16)), isSynthetic = c.IsSynthetic });
            return await Task.FromResult(Dto(c, new SteelMasses(0, 0, 0)));
        }, ct);
    public Task<ProductionContractDto> Save(ProductionActor actor, Guid id, SaveProductionContract cmd, CancellationToken ct) =>
        Command(actor, cmd.RequestId, $"contract:{id}:save", cmd, async () =>
        {
            var fields = Fields(cmd.Fields); var c = await Lock(id, ct); Version(c.Version, cmd.ExpectedVersion); var before = Fields(c);
            var sensitive = before.OpeningDate != fields.OpeningDate || before.OpeningSteel != fields.OpeningSteel || before.NormConfirmed != fields.NormConfirmed;
            var reason = Reason(cmd.Reason, sensitive); var changes = Changes(before, fields);
            if (changes.Count != 0)
            {
                Apply(c, fields); c.Version++; c.UpdatedAt = Now; Audit(actor, c.Id, "contract_changed", reason, changes);
                await db.SaveChangesAsync(ct);
            }
            return await Dto(c, ct);
        }, ct);
    public Task<SteelDeliveryResult> AddDelivery(ProductionActor actor, Guid id, CreateSteelDelivery cmd, CancellationToken ct) =>
        Command(actor, cmd.RequestId, $"contract:{id}:delivery:create", cmd, async () =>
        {
            var fields = Fields(cmd.Fields); var c = await Lock(id, ct); Version(c.Version, cmd.ExpectedContractVersion, "expectedContractVersion");
            var d = new SteelDelivery { Id = Guid.NewGuid(), ContractId = id, CreatedBy = actor.UserId, UpdatedBy = actor.UserId, CreatedAt = Now, UpdatedAt = Now };
            Apply(d, fields); db.Deliveries.Add(d); c.Version++; c.UpdatedAt = Now;
            Audit(actor, id, "delivery_created", "", new { deliveryId = d.Id, fields });
            return new SteelDeliveryResult(Dto(d, c), c.Version);
        }, ct);
    public Task<SteelDeliveryResult> CorrectDelivery(ProductionActor actor, Guid id, Guid deliveryId, CorrectSteelDelivery cmd, CancellationToken ct) =>
        Command(actor, cmd.RequestId, $"contract:{id}:delivery:{deliveryId}:correct", cmd, async () =>
        {
            var fields = Fields(cmd.Fields); var reason = Reason(cmd.Reason, true); var c = await Lock(id, ct);
            Version(c.Version, cmd.ExpectedContractVersion, "expectedContractVersion");
            var d = await db.Deliveries.SingleOrDefaultAsync(d => d.ContractId == id && d.Id == deliveryId, ct) ?? throw new ProductionFault(404, "Nie znaleziono dostawy.");
            Version(d.Version, cmd.ExpectedVersion); var changes = Changes(Fields(d), fields);
            if (changes.Count == 0) throw ProductionFault.Invalid("fields", "Korekta musi zmieniać dane dostawy.");
            Apply(d, fields); d.Version++; d.UpdatedAt = Now; d.UpdatedBy = actor.UserId; c.Version++; c.UpdatedAt = Now;
            Audit(actor, id, "delivery_corrected", reason, new { deliveryId, changes });
            return new SteelDeliveryResult(Dto(d, c), c.Version);
        }, ct);

    private async Task<T> Command<T>(ProductionActor actor, Guid requestId, string operation, object payload, Func<Task<T>> action, CancellationToken ct)
    {
        Require(actor, true); await CheckRuntime(ct);
        if (requestId == Guid.Empty) throw ProductionFault.Invalid("requestId", "Brak identyfikatora zapisu.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + "\n" + JsonSerializer.Serialize(payload, Json))));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '15s'", ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '20s'", ct);
            var lockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"production:{actor.UserId}:{requestId}")));
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
            var receipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.ActorId == actor.UserId && r.RequestId == requestId, ct);
            if (receipt is not null)
            {
                if (receipt.Fingerprint != fingerprint) throw new ProductionFault(409, "Identyfikator zapisu został już użyty dla innych danych.");
                return JsonSerializer.Deserialize<T>(receipt.Result, Json) ?? throw new ProductionFault(503, "Nie można odczytać potwierdzenia zapisu.");
            }
            var result = await action();
            db.Receipts.Add(new() { ActorId = actor.UserId, RequestId = requestId, Fingerprint = fingerprint, Result = JsonSerializer.Serialize(result, Json), RecordedAt = Now });
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return result;
        }
        catch (DbUpdateConcurrencyException) { throw ProductionFault.Conflict(); }
        catch (Exception e) when (DatabaseTimeout(e))
        { throw new ProductionFault(503, "Zapis czekał zbyt długo. Zachowaj formularz i ponów ten sam zapis."); }
        finally { db.ChangeTracker.Clear(); }
    }
    private static bool DatabaseTimeout(Exception error) => error is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.DeadlockDetected }
        || error.InnerException is not null && DatabaseTimeout(error.InnerException);

    private async Task<IDbContextTransaction> ReadSnapshot(CancellationToken ct)
    {
        await CheckRuntime(ct);
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '15s'", ct);
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public async Task CheckRuntime(CancellationToken ct)
    {
        try
        {
            if ((await db.Database.GetPendingMigrationsAsync(ct)).Any()) throw new ProductionFault(503, "Zastosuj migracje modułu produkcji.");
            await db.Database.OpenConnectionAsync(ct);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT current_user=session_user AND r.rolcanlogin
                    AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolbypassrls AND NOT r.rolreplication AND NOT r.rolinherit
                    AND NOT EXISTS (SELECT 1 FROM pg_roles a WHERE (a.rolsuper OR a.rolcreatedb OR a.rolcreaterole OR a.rolbypassrls)
                        AND pg_has_role(current_user, a.oid, 'MEMBER'))
                    AND NOT has_schema_privilege(current_user, 'production', 'CREATE')
                    AND has_schema_privilege(current_user, 'production', 'USAGE')
                    AND NOT has_database_privilege(current_user, current_database(), 'CREATE')
                    AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member=r.oid)
                    AND NOT EXISTS (SELECT 1 FROM pg_namespace n WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname <> 'information_schema' AND has_schema_privilege(current_user,n.oid,'CREATE'))
                    AND (SELECT count(*) FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
                        WHERE n.nspname='production' AND t.relkind='r' AND t.relname IN ('Contracts','Deliveries','Changes','Receipts','__EFMigrationsHistory'))=5
                    AND NOT EXISTS (SELECT 1 FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
                        WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%' AND t.relkind IN ('r','p','v','m','f')
                        AND (pg_has_role(current_user,t.relowner,'MEMBER')
                            OR ((n.nspname <> 'production' OR t.relname NOT IN ('Contracts','Deliveries','Changes','Receipts','__EFMigrationsHistory'))
                                AND (has_table_privilege(current_user,t.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                                OR has_any_column_privilege(current_user,t.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                            OR (n.nspname='production' AND has_table_privilege(current_user,t.oid,'DELETE,TRUNCATE,REFERENCES,TRIGGER'))
                            OR (n.nspname='production' AND t.relname IN ('Changes','Receipts','__EFMigrationsHistory')
                                AND (has_table_privilege(current_user,t.oid,'UPDATE') OR has_any_column_privilege(current_user,t.oid,'UPDATE')))
                            OR (n.nspname='production' AND t.relname='__EFMigrationsHistory'
                                AND (has_table_privilege(current_user,t.oid,'INSERT') OR has_any_column_privilege(current_user,t.oid,'INSERT')))))
                    AND has_table_privilege(current_user,'production."Contracts"','SELECT')
                    AND has_table_privilege(current_user,'production."Contracts"','INSERT')
                    AND has_table_privilege(current_user,'production."Contracts"','UPDATE')
                    AND has_table_privilege(current_user,'production."Deliveries"','SELECT')
                    AND has_table_privilege(current_user,'production."Deliveries"','INSERT')
                    AND has_table_privilege(current_user,'production."Deliveries"','UPDATE')
                    AND has_table_privilege(current_user,'production."Changes"','SELECT')
                    AND has_table_privilege(current_user,'production."Changes"','INSERT')
                    AND has_table_privilege(current_user,'production."Receipts"','SELECT')
                    AND has_table_privilege(current_user,'production."Receipts"','INSERT')
                    AND has_table_privilege(current_user,'production."__EFMigrationsHistory"','SELECT')
                    AND (SELECT count(*) FROM pg_trigger WHERE tgrelid IN ('production."Contracts"'::regclass,'production."Deliveries"'::regclass)
                        AND tgname IN ('protect_contract_identity','protect_delivery_identity') AND tgenabled='O')=2
                FROM pg_roles r WHERE r.rolname=current_user
                """;
            if (await command.ExecuteScalarAsync(ct) is not true) throw new ProductionFault(503, "Skonfiguruj ograniczone uprawnienia modułu produkcji.");
        }
        catch (PostgresException) { throw new ProductionFault(503, "Sprawdź migracje i uprawnienia modułu produkcji."); }
        catch (NpgsqlException) { throw new ProductionFault(503, "Moduł produkcji jest chwilowo niedostępny."); }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
